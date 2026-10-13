using Amc.Cli.Infrastructure;
using Amc.Core.Domain;
using static Amc.Tests.Cli.CliHarness;

namespace Amc.Tests.Cli;

public class ResolverTests
{
    private static readonly List<Agent> Agents =
    [
        Agent("claude:c441aaaa-1111-2222-3333-444455556666", "Refactor billing"),
        Agent("claude:c441aaaa-1111-2222-3333-444455556666/a319bbbb", "Write billing tests", parent: "claude:c441aaaa-1111-2222-3333-444455556666"),
        Agent("claude:c442cccc-9999-8888-7777-666655554444", "Fix login page"),
        Agent("claude:d0000000-0000-0000-0000-000000000000", "Fix login API"),
    ];

    [Theory]
    [InlineData("claude:c442cccc-9999-8888-7777-666655554444", "claude:c442cccc-9999-8888-7777-666655554444")] // exact id
    [InlineData("c442", "claude:c442cccc-9999-8888-7777-666655554444")]                                     // short prefix
    [InlineData("claude:c442", "claude:c442cccc-9999-8888-7777-666655554444")]                              // full-id prefix
    [InlineData("a319", "claude:c441aaaa-1111-2222-3333-444455556666/a319bbbb")]                            // sub-agent id prefix
    [InlineData("c441aaaa-1111-2222-3333-444455556666", "claude:c441aaaa-1111-2222-3333-444455556666")]     // exact session id beats prefix of child
    [InlineData("c441aaaa-1111-2222-3333-444455556666/a3", "claude:c441aaaa-1111-2222-3333-444455556666/a319bbbb")]
    [InlineData("REFACTOR", "claude:c441aaaa-1111-2222-3333-444455556666")]                                 // title substring, case-insensitive
    [InlineData("login page", "claude:c442cccc-9999-8888-7777-666655554444")]
    public void Resolves_unique_references(string query, string expected) =>
        Assert.Equal(expected, AgentResolver.Resolve(Agents, query).Id);

    [Theory]
    [InlineData("c4")]        // id prefix shared by two sessions
    [InlineData("c441")]      // parent and child share the session prefix
    [InlineData("billing")]   // two titles
    [InlineData("fix login")] // two titles
    public void Ambiguous_references_are_rejected_with_candidates(string query)
    {
        var ex = Assert.Throws<UsageException>(() => AgentResolver.Resolve(Agents, query));
        Assert.Contains("ambiguous", ex.Message);
        Assert.Contains("Use a longer id prefix", ex.Message);
    }

    [Fact]
    public void Unknown_reference_is_an_error_not_usage()
    {
        var ex = Assert.Throws<CliException>(() => AgentResolver.Resolve(Agents, "zzz"));
        Assert.Equal(ExitCodes.Error, ex.ExitCode);
    }

    [Fact]
    public void Short_ids_use_the_last_segment()
    {
        Assert.Equal("c441aaaa", AgentRef.Short("claude:c441aaaa-1111-2222-3333-444455556666"));
        Assert.Equal("a319bbbb", AgentRef.Short("claude:c441aaaa-1111/a319bbbbcccc"));
    }

    [Fact]
    public void Worktrees_resolve_by_branch_name_and_path()
    {
        var wts = new List<Worktree>
        {
            new() { Id = "wt:aaaa11112222", Root = "/home/u/dev/app", RepositoryId = "repo:1", Branch = "main", IsMain = true },
            new() { Id = "wt:bbbb11112222", Root = "/home/u/dev/app-wt/feature-x", RepositoryId = "repo:1", Branch = "agent/feature-x" },
            new() { Id = "wt:cccc11112222", Root = "/home/u/dev/app2", RepositoryId = "repo:2", Branch = "main", IsMain = true },
        };
        Assert.Equal("wt:bbbb11112222", WorktreeRef.Resolve(wts, "agent/feature-x", "/").Id);
        Assert.Equal("wt:bbbb11112222", WorktreeRef.Resolve(wts, "feature-x", "/").Id);
        Assert.Equal("wt:aaaa11112222", WorktreeRef.Resolve(wts, "aaaa", "/").Id);
        Assert.Equal("wt:bbbb11112222", WorktreeRef.Resolve(wts, "/home/u/dev/app-wt/feature-x/src/A.cs", "/").Id);
        Assert.Equal("wt:aaaa11112222", WorktreeRef.Resolve(wts, ".", "/home/u/dev/app/src").Id);
        Assert.Equal("wt:aaaa11112222", WorktreeRef.Resolve(wts, "app", "/").Id);   // exact name wins over "app2"
        Assert.Throws<UsageException>(() => WorktreeRef.Resolve(wts, "pp", "/"));   // name substring of two
        Assert.Throws<UsageException>(() => WorktreeRef.Resolve(wts, "main", "/")); // branch of two repos
        Assert.Throws<CliException>(() => WorktreeRef.Resolve(wts, "/elsewhere", "/"));
    }
}
