using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amc.Cli.Infrastructure;
using Amc.Core.Providers.Claude;

namespace Amc.Cli.Hooks;

/// <summary>A computed change to settings.json, shown to the user before anything is written.</summary>
public sealed record HooksPlan(string SettingsPath, string TargetPath, bool Exists, JsonObject Before, JsonObject After, IReadOnlyList<string> Changes)
{
    public bool Changed => !JsonNode.DeepEquals(Before, After);
}

/// <summary>
/// File IO around <see cref="ClaudeHooksConfig"/>: load (missing = {}), plan, timestamped backup,
/// atomic write (temp file in the same directory + rename) preserving the file mode. Symlinked
/// settings files are written through to their target so the link survives.
/// </summary>
public static class HooksFile
{
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep the hook command readable (no ')
    };

    public static string DefaultSettingsPath(CliContext ctx) =>
        Path.Combine(ctx.Env("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(ctx.Home, ".claude"), "settings.json");

    public static string ResolveTarget(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var info = new FileInfo(full);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                return target.FullName;
        }
        catch (IOException) { }
        return full;
    }

    public static (bool Exists, JsonObject Settings) Load(string path)
    {
        var target = ResolveTarget(path);
        if (!File.Exists(target)) return (false, new JsonObject());
        var text = File.ReadAllText(target);
        if (string.IsNullOrWhiteSpace(text)) return (true, new JsonObject());
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o
                ? (true, o)
                : throw new CliException($"{path} does not contain a JSON object; not touching it");
        }
        catch (JsonException ex)
        {
            throw new CliException($"{path} is not valid JSON ({ex.Message}); not touching it");
        }
    }

    public static HooksPlan PlanInstall(string path, string command, string? rewakeCommand = null)
    {
        var (exists, before) = Load(path);
        var after = ClaudeHooksConfig.Install(before, command, rewakeCommand);
        return new HooksPlan(path, ResolveTarget(path), exists, before, after, Summarize(before, after));
    }

    /// <summary>Install/uninstall for another AI tool's hook config (see ToolHooksConfig profiles).</summary>
    public static HooksPlan PlanTool(string path, Func<JsonObject, JsonObject> change)
    {
        var (exists, before) = Load(path);
        var after = change(before);
        return new HooksPlan(path, ResolveTarget(path), exists, before, after, Summarize(before, after));
    }

    public static HooksPlan PlanUninstall(string path)
    {
        var (exists, before) = Load(path);
        var after = ClaudeHooksConfig.Uninstall(before);
        return new HooksPlan(path, ResolveTarget(path), exists, before, after, Summarize(before, after));
    }

    /// <summary>Human-readable per-event summary of what changes. Foreign hooks are counted to show they are kept.</summary>
    public static List<string> Summarize(JsonObject before, JsonObject after)
    {
        var lines = new List<string>();
        var events = Keys(before).Union(Keys(after)).ToList();
        var foreign = 0;
        foreach (var ev in events)
        {
            var oldOurs = Commands(before, ev, ours: true);
            var newOurs = Commands(after, ev, ours: true);
            var oldForeign = Commands(before, ev, ours: false);
            var newForeign = Commands(after, ev, ours: false);
            foreign += newForeign.Count;
            if (oldForeign.Count != newForeign.Count)
                lines.Add($"! {ev}: foreign hooks changed {oldForeign.Count} → {newForeign.Count}");
            if (oldOurs.SequenceEqual(newOurs)) continue;
            if (oldOurs.Count == 0) lines.Add($"+ {ev}: add agent-mission-control hook");
            else if (newOurs.Count == 0) lines.Add($"- {ev}: remove agent-mission-control hook" + (oldOurs.Count > 1 ? $" (×{oldOurs.Count})" : ""));
            else lines.Add($"~ {ev}: update agent-mission-control hook command");
        }
        if (foreign > 0) lines.Add($"= {foreign} other hook(s) left untouched");
        return lines;
    }

    private static IEnumerable<string> Keys(JsonObject s) => s["hooks"] is JsonObject h ? h.Select(kv => kv.Key) : [];

    private static List<string> Commands(JsonObject s, string ev, bool ours) =>
        s["hooks"]?[ev] is JsonArray groups
            ? groups.OfType<JsonObject>().SelectMany(g => g["hooks"] as JsonArray ?? [])
                .Where(h => ClaudeHooksConfig.IsOurs(h) == ours).Select(h => h?.ToJsonString() ?? "").ToList()
            : [];

    public static string Serialize(JsonObject settings) => settings.ToJsonString(WriteOptions) + "\n";

    /// <summary>Writes the plan: backup first (if the file exists), then atomic replace. Returns the backup path.</summary>
    public static string? Apply(HooksPlan plan, DateTimeOffset now)
    {
        var target = plan.TargetPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        UnixFileMode? mode = null;
        string? backup = null;
#pragma warning disable CA1416 // guarded by OperatingSystem.IsWindows()
        if (File.Exists(target))
        {
            if (!OperatingSystem.IsWindows()) mode = File.GetUnixFileMode(target);
            backup = BackupPath(target, now);
            File.Copy(target, backup, overwrite: false);
            if (mode is { } m) File.SetUnixFileMode(backup, m);
        }
        WriteAtomic(target, Serialize(plan.After), mode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite));
#pragma warning restore CA1416
        return backup;
    }

    public static string BackupPath(string target, DateTimeOffset now)
    {
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        var candidate = $"{target}.amc-backup-{stamp}";
        for (var i = 1; File.Exists(candidate); i++) candidate = $"{target}.amc-backup-{stamp}-{i}";
        return candidate;
    }

    /// <summary>Temp file in the same directory (same filesystem), fsync, then rename over the target.</summary>
    public static void WriteAtomic(string target, string content, UnixFileMode mode)
    {
        var dir = Path.GetDirectoryName(target)!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(target)}.amc-tmp-{Guid.NewGuid():N}");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var fs = new FileStream(tmp, options))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, mode); // exact mode, independent of umask
            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
