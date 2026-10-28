using System.Text;

namespace Amc.Core.Risk;

/// <summary>A redirection such as <c>&gt; out.txt</c> or <c>2&gt;&amp;1</c>. <see cref="Target"/> is null for heredocs.</summary>
public sealed record ShellRedirect(string Op, string? Target)
{
    /// <summary>True when the redirection writes to a file (not a descriptor duplicate like <c>2&gt;&amp;1</c>).</summary>
    public bool WritesFile => Target is not null && Op.Contains('>') && !(Op.EndsWith('&') && (Target == "-" || Target.All(char.IsAsciiDigit)));
}

/// <summary>One simple command of a command line (words already unquoted).</summary>
public sealed class ShellCommand
{
    public List<string> Words { get; } = [];
    public List<ShellRedirect> Redirects { get; } = [];
    /// <summary>Bodies of <c>$(…)</c>, <c>`…`</c> and <c>&lt;(…)</c> inside this command (classified as command lines of their own).</summary>
    public List<string> Substitutions { get; } = [];
    /// <summary>Heredoc bodies fed to this command (e.g. SQL piped into psql, a script piped into bash).</summary>
    public List<string> Heredocs { get; } = [];
    /// <summary>Operator that connected this command to the previous one: "" (first), ";", "&amp;&amp;", "||", "|", "&amp;", "\n" or "(".</summary>
    public string Before { get; set; } = "";
    /// <summary>The command as written (trimmed).</summary>
    public string Text { get; set; } = "";
}

/// <summary>Which shell's quoting rules a command line follows.</summary>
public enum ShellDialect
{
    /// <summary>sh / bash / zsh (also Git Bash on Windows): backslash escapes, '…' and "…" quotes, # comments.</summary>
    Posix,
    /// <summary>Windows PowerShell / pwsh: backslash is a path separator, backtick escapes, '…' literal ('' inside), "…" with backtick escapes, # comments.</summary>
    PowerShell,
    /// <summary>cmd.exe: only "…" quotes, ^ escapes, &amp; / &amp;&amp; / || / | separators, no comments, no $ or backtick syntax.</summary>
    Cmd,
}

/// <summary>
/// A small, forgiving POSIX-shell / PowerShell / cmd tokenizer. It never executes or expands anything:
/// it splits a command line into simple commands (on ; &amp;&amp; || | &amp; newlines and parentheses), removes quotes,
/// records redirections, command substitutions and heredoc bodies. Unbalanced quotes are tolerated.
/// With <c>windows: true</c> a backslash is a path separator, not an escape (PowerShell escapes with a backtick);
/// <see cref="ShellDialect.Cmd"/> follows cmd.exe's rules instead.
/// </summary>
public static class ShellLexer
{
    public static List<ShellCommand> Parse(string text, bool windows = false) =>
        Parse(text, windows ? ShellDialect.PowerShell : ShellDialect.Posix);

    public static List<ShellCommand> Parse(string text, ShellDialect dialect)
    {
        var p = new Parser(text ?? "", dialect);
        p.Run();
        return p.Commands;
    }

    private sealed class Parser(string s, ShellDialect dialect)
    {
        private readonly bool windows = dialect != ShellDialect.Posix;
        private readonly bool cmd = dialect == ShellDialect.Cmd;
        public readonly List<ShellCommand> Commands = [];
        private ShellCommand _cur = new();
        private readonly StringBuilder _word = new();
        private bool _inWord;
        private string? _pendingRedirect;
        private readonly List<(string Delim, bool StripTabs)> _heredocs = [];
        private int _cmdStart;
        private int _i;

        public void Run()
        {
            while (_i < s.Length)
            {
                var c = s[_i];
                if (cmd && CmdChar(c)) continue;
                if (c == '#' && !_inWord) { SkipComment(); continue; }
                switch (c)
                {
                    case '\n':
                        EndWord();
                        if (_heredocs.Count > 0) ReadHeredocBodies(); // leaves _i on the newline that ends the last body
                        EndCommand("\n", _i, 1);
                        continue;
                    case ' ' or '\t' or '\r':
                        EndWord(); _i++; continue;
                    case ';':
                        EndCommand(";", _i, At(_i + 1) == ';' ? 2 : 1); continue;
                    case '&':
                        if (At(_i + 1) == '&') { EndCommand("&&", _i, 2); continue; }
                        if (At(_i + 1) == '>') { Redirect(); continue; }
                        EndCommand("&", _i, 1); continue;
                    case '|':
                        if (At(_i + 1) == '|') { EndCommand("||", _i, 2); continue; }
                        EndCommand("|", _i, At(_i + 1) == '&' ? 2 : 1); continue;
                    case '(' or ')':
                        if (c == '(' && _inWord && _word.Length > 0 && _word[^1] == '=') { Append(c); _i++; continue; } // a=(x y) array
                        EndCommand("(", _i, 1); continue;
                    case '>' or '<':
                        if (c == '<' && At(_i + 1) == '(') { _i++; Substitution('(', ')', "<("); continue; }
                        Redirect(); continue;
                    case '\'':
                        SingleQuoted(); continue;
                    case '"':
                        DoubleQuoted(); continue;
                    case '\\' when !windows:
                        if (At(_i + 1) == '\n') { _i += 2; continue; }
                        if (_i + 1 < s.Length) Append(s[_i + 1]);
                        _i += 2; continue;
                    case '`':
                        if (windows) { if (_i + 1 < s.Length) Append(s[_i + 1]); _i += 2; continue; }
                        _i++; BacktickSubstitution(); continue;
                    case '$' when At(_i + 1) == '(':
                        _i++; Substitution('(', ')', "$("); continue;
                    case '$' when At(_i + 1) == '{':
                        Append('$'); _i++; Balanced('{', '}', out var inner); _word.Append('{').Append(inner).Append('}'); continue;
                    default:
                        Append(c); _i++; continue;
                }
            }
            EndCommand("", s.Length, 0);
        }

        private char At(int i) => i < s.Length ? s[i] : '\0';

        /// <summary>cmd.exe: ^ escapes, ' # ` $ are ordinary characters, a leading @ (echo suppression) is dropped. True when handled.</summary>
        private bool CmdChar(char c)
        {
            switch (c)
            {
                case '^':
                    if (At(_i + 1) == '\n') { _i += 2; return true; } // line continuation
                    if (_i + 1 < s.Length) Append(s[_i + 1]);
                    _i += 2; return true;
                case '@' when !_inWord && _cur.Words.Count == 0:
                    _i++; return true;
                case '\'' or '#' or '`' or '$':
                    Append(c); _i++; return true;
                case '"':
                    _inWord = true;
                    _i++;
                    while (_i < s.Length && s[_i] != '"') _word.Append(s[_i++]);
                    _i++;
                    return true;
                default:
                    return false;
            }
        }

        private void Append(char c) { _word.Append(c); _inWord = true; }

        private void SkipComment()
        {
            while (_i < s.Length && s[_i] != '\n') _i++;
        }

        private void SingleQuoted()
        {
            _inWord = true;
            _i++;
            while (_i < s.Length)
            {
                if (s[_i] == '\'')
                {
                    if (windows && At(_i + 1) == '\'') { _word.Append('\''); _i += 2; continue; } // PowerShell '' escape
                    _i++;
                    return;
                }
                _word.Append(s[_i++]);
            }
        }

        private void DoubleQuoted()
        {
            _inWord = true;
            _i++;
            while (_i < s.Length)
            {
                var c = s[_i];
                if (c == '"') { _i++; return; }
                if (!windows && c == '\\' && At(_i + 1) is '"' or '\\' or '$' or '`' or '\n') { if (s[_i + 1] != '\n') _word.Append(s[_i + 1]); _i += 2; continue; }
                if (windows && c == '`' && _i + 1 < s.Length) { _word.Append(s[_i + 1]); _i += 2; continue; }
                if (c == '$' && At(_i + 1) == '(') { _i++; Substitution('(', ')', "$("); continue; }
                if (!windows && c == '`') { _i++; BacktickSubstitution(); continue; }
                _word.Append(c);
                _i++;
            }
        }

        /// <summary>At an opening bracket: reads to the matching close (quotes respected), records the body as a substitution.</summary>
        private void Substitution(char open, char close, string prefix)
        {
            Balanced(open, close, out var inner);
            _inWord = true;
            _word.Append(prefix).Append(inner).Append(close);
            // $((…)) is arithmetic, not a command.
            if (!(prefix == "$(" && inner.StartsWith('(')) && inner.Trim().Length > 0) _cur.Substitutions.Add(inner);
        }

        private void Balanced(char open, char close, out string inner)
        {
            // _i is on `open`.
            var start = ++_i;
            var depth = 1;
            char quote = '\0';
            while (_i < s.Length)
            {
                var c = s[_i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else if (c == '\\' && quote == '"' && !windows) _i++;
                }
                else if (c is '\'' or '"') quote = c;
                else if (c == '\\' && !windows) _i++;
                else if (c == open) depth++;
                else if (c == close && --depth == 0) { inner = s[start.._i]; _i++; return; }
                _i++;
            }
            inner = s[Math.Min(start, s.Length)..];
        }

        private void BacktickSubstitution()
        {
            var start = _i;
            while (_i < s.Length && s[_i] != '`') { if (s[_i] == '\\') _i++; _i++; }
            var inner = s[start..Math.Min(_i, s.Length)];
            _i++;
            _inWord = true;
            _word.Append('`').Append(inner).Append('`');
            if (inner.Trim().Length > 0) _cur.Substitutions.Add(inner);
        }

        private void Redirect()
        {
            // A bare number right before the operator is a file descriptor (2>file), not an argument.
            if (_inWord && _word.Length > 0 && _word.ToString().All(char.IsAsciiDigit)) { _word.Clear(); _inWord = false; }
            else EndWord();
            var op = new StringBuilder();
            if (s[_i] == '&') { op.Append('&'); _i++; }
            var c = s[_i];
            op.Append(c); _i++;
            if (At(_i) == c) { op.Append(c); _i++; }
            if (op.ToString() == "<<" && At(_i) == '<') { op.Append('<'); _i++; }
            else if (op.ToString() == "<<" && At(_i) == '-') { op.Append('-'); _i++; }
            else if (At(_i) is '&' or '|') { op.Append(s[_i]); _i++; }
            else if (c == '<' && At(_i) == '>') { op.Append('>'); _i++; }
            _pendingRedirect = op.ToString();
        }

        private void EndWord()
        {
            if (!_inWord) return;
            var w = _word.ToString();
            _word.Clear();
            _inWord = false;
            if (_pendingRedirect is { } op)
            {
                _pendingRedirect = null;
                if (op is "<<" or "<<-")
                {
                    _heredocs.Add((w, op == "<<-"));
                    _cur.Redirects.Add(new ShellRedirect(op, null));
                }
                else _cur.Redirects.Add(new ShellRedirect(op, w));
                return;
            }
            _cur.Words.Add(w);
        }

        private void EndCommand(string sep, int at, int width)
        {
            EndWord();
            if (_pendingRedirect is { } op) { _cur.Redirects.Add(new ShellRedirect(op, null)); _pendingRedirect = null; }
            if (_cur.Words.Count > 0 || _cur.Redirects.Count > 0 || _cur.Heredocs.Count > 0)
            {
                _cur.Text = s[Math.Min(_cmdStart, at)..at].Trim();
                Commands.Add(_cur);
                _cur = new ShellCommand { Before = sep };
            }
            else if (sep is "|" or "&&" or "||") _cur.Before = sep;
            else if (_cur.Before == "") _cur.Before = sep;
            _i = at + width;
            _cmdStart = _i;
        }

        /// <summary>At the newline ending a line that declared heredocs: consumes their bodies (attached to the current command).</summary>
        private void ReadHeredocBodies()
        {
            foreach (var (delim, stripTabs) in _heredocs)
            {
                var body = new StringBuilder();
                while (_i < s.Length)
                {
                    var lineStart = _i + 1;
                    if (lineStart > s.Length) { _i = s.Length; break; }
                    var nl = s.IndexOf('\n', lineStart);
                    var end = nl < 0 ? s.Length : nl;
                    var line = s[lineStart..end].TrimEnd('\r');
                    _i = end;
                    if ((stripTabs ? line.TrimStart('\t') : line) == delim) break;
                    body.Append(line).Append('\n');
                    if (nl < 0) break;
                }
                _cur.Heredocs.Add(body.ToString());
            }
            _heredocs.Clear();
            if (_i >= s.Length) _i = s.Length;
        }
    }
}
