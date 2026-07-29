using System.Text.Json;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// R5's dispatch-path wiring (doc/roadmap/lane-codex-runtime.md): a <c>runAgent</c> action with
/// <c>isolation: "worktree"</c> creates/reuses the ticket's git worktree and records it durably on
/// the ticket, WITHOUT bypassing the R4 file-lease gate — a worktree dispatch that conflicts with an
/// active lease is still blocked/warned exactly like a non-worktree dispatch (checkout separation is
/// not proof that eventual-merge scopes are disjoint; see FileLeaseStore's remarks). Also covers the
/// Done-triggered cleanup path. Uses real git repositories in temp directories, and the same
/// OllamaValidationError fast-fail idiom as <see cref="ActionExecutorFileLeaseTests"/> so no real
/// claude CLI is ever spawned.
/// </summary>
[Collection("MockClaude")]
public class ActionExecutorWorktreeTests
{
    private const string NonClaudeModel = "qwen3-coder:30b"; // triggers OllamaValidationError, no subprocess
    private const string LeasedGlob = "src/**";

    private sealed class Harness : IDisposable
    {
        public required TempDir Tmp { get; init; }
        public required ProjectService Projects { get; init; }
        public required TicketService Tickets { get; init; }
        public required FileLeaseStore Leases { get; init; }
        public required AgentRunRegistry Runs { get; init; }
        public required ActionExecutor Executor { get; init; }
        public required ProjectRuntime Runtime { get; init; }
        public required string Slug { get; init; }
        public required string Workspace { get; init; }

        public void Dispose() => Tmp.Dispose();
    }

    private static async Task RunGitAsync(string cwd, string args)
    {
        var res = await ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromSeconds(30));
        Assert.True(res.Success, $"git {args} failed in {cwd}: {res.Stderr}");
    }

    private static async Task InitRepoAsync(string workspace)
    {
        await RunGitAsync(workspace, "init -q");
        await RunGitAsync(workspace, "config user.email test@example.com");
        await RunGitAsync(workspace, "config user.name \"GigaClaw Test\"");
        await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "hello\n");
        await RunGitAsync(workspace, "add -A");
        await RunGitAsync(workspace, "commit -q -m initial");
    }

    private static async Task<Harness> BuildAsync(string projectName, string agentName, string? enforcement, bool gitInit)
    {
        var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync(projectName);
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        if (gitInit) await InitRepoAsync(workspace);
        WriteContracts(workspace, agentName, enforcement);

        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var runner = new ClaudeRunner(sessions, runs, new RunConcurrencyGate(4), NullLogger<ClaudeRunner>.Instance);
        var cost = new CostTracker();
        var appSettings = new AppSettingsService(tmp.Path);
        var leases = new FileLeaseStore(projects);

        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost,
            new LocalizationService(appSettings), projects,
            new RunStateManager(runs, cost, tickets, NullLogger.Instance),
            FakeHttpClientFactory.Unused,
            TestTeamRuns.For(projects, tickets),
            NullLogger.Instance,
            outboundGate: null,
            leases: leases);

        return new Harness
        {
            Tmp = tmp,
            Projects = projects,
            Tickets = tickets,
            Leases = leases,
            Runs = runs,
            Executor = executor,
            Slug = project.Slug,
            Workspace = workspace,
            Runtime = new ProjectRuntime(project.Slug)
            {
                Workspace = workspace,
                Config = new AutomationConfig { Automations = [] },
            },
        };
    }

    private static void WriteContracts(string workspace, string agentName, string? enforcement)
    {
        var agentsDir = Path.Combine(workspace, ".agents");
        Directory.CreateDirectory(agentsDir);
        var enforcementLine = enforcement is null ? "" : $"\"enforcement\": {JsonSerializer.Serialize(enforcement)},";
        var manifest = $$"""
            {
              "version": 1,
              "defaults": {
                "maxDispatchAttempts": 3,
                "retryBackoffSeconds": 300,
                "requireAtomicHandoff": true,
                "requireAuthorOnBoardWrites": true
              },
              "agents": {
                "{{agentName}}": {
                  {{enforcementLine}}
                  "dispatches": ["assignment"],
                  "riskClass": "code-write",
                  "allowedWriteGlobs": ["{{LeasedGlob}}"],
                  "ticketExit": ["Review", "Blocked", "Done"]
                }
              }
            }
            """;
        File.WriteAllText(Path.Combine(agentsDir, "contracts.json"), manifest);
    }

    private static AutomationRule MakeAutomation(string agentName, string isolation = "worktree") => new()
    {
        Id = "test-worktree-dispatch",
        Enabled = true,
        Trigger = new StatusChangeTriggerSpec { To = "Doing" },
        Actions = [new RunAgentActionSpec { Agent = agentName, MaxTurns = 1, Model = NonClaudeModel, Isolation = isolation }],
    };

    private static async Task<List<string>> CommentsAsync(Harness h, int ticketId)
    {
        var ticket = await h.Tickets.GetTicketAsync(h.Slug, ticketId);
        return ticket!.Comments.Select(c => c.Content).ToList();
    }

    private static async Task<AgentRun?> WaitForAnyRunAsync(AgentRunRegistry runs, string slug, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var run = runs.AllForProject(slug).FirstOrDefault();
                if (run is not null && !runs.ActiveForProject(slug).Any())
                    return run;
                await Task.Delay(20, cts.Token);
            }
        }
        catch (OperationCanceledException) { /* timed out with no run — a valid outcome here */ }
        return runs.AllForProject(slug).FirstOrDefault();
    }

    // ── Creation + durable state ────────────────────────────────────────────

    [Fact]
    public async Task Worktree_isolation_creates_and_records_the_ticket_worktree_then_dispatches()
    {
        using var h = await BuildAsync("wt-create", "programmer", enforcement: "block", gitInit: true);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Doing");

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, MakeAutomation("programmer"),
            new TriggerFiring(ticket.Id, ticket.Title, "Doing"), CancellationToken.None);

        var run = await WaitForAnyRunAsync(h.Runs, h.Slug, TimeSpan.FromSeconds(10));
        Assert.NotNull(run); // no conflicting lease — dispatch proceeds

        var after = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        Assert.Equal("ticket/" + ticket.Id, after!.WorktreeBranch);
        Assert.NotNull(after.WorktreePath);
        Assert.True(Directory.Exists(after.WorktreePath));
        Assert.Equal("active", after.WorktreeStatus);
    }

    [Fact]
    public async Task Re_dispatching_the_same_ticket_reuses_the_existing_worktree_idempotently()
    {
        using var h = await BuildAsync("wt-reuse", "programmer", enforcement: "block", gitInit: true);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Doing");
        var automation = MakeAutomation("programmer");

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, automation, new TriggerFiring(ticket.Id, ticket.Title, "Doing"), CancellationToken.None);
        await WaitForAnyRunAsync(h.Runs, h.Slug, TimeSpan.FromSeconds(10));
        var firstPath = (await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!.WorktreePath;
        Assert.NotNull(firstPath);

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, automation, new TriggerFiring(ticket.Id, ticket.Title, "Doing"), CancellationToken.None);
        await WaitForAnyRunAsync(h.Runs, h.Slug, TimeSpan.FromSeconds(10));
        var secondPath = (await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!.WorktreePath;

        Assert.Equal(firstPath, secondPath);
        var list = await ProcessRunner.RunAsync("git", "worktree list --porcelain", h.Workspace, TimeSpan.FromSeconds(30));
        var entries = list.Stdout.Split('\n').Count(l => l.StartsWith("worktree ", StringComparison.Ordinal));
        Assert.Equal(2, entries); // main checkout + exactly one ticket worktree, not two
    }

    // ── Fails closed rather than falling back to in-place execution ─────────

    [Fact]
    public async Task Worktree_isolation_fails_the_dispatch_closed_when_the_workspace_is_not_a_git_repo()
    {
        using var h = await BuildAsync("wt-not-git", "programmer", enforcement: "block", gitInit: false);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Doing");

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, MakeAutomation("programmer"),
            new TriggerFiring(ticket.Id, ticket.Title, "Doing"), CancellationToken.None);

        var run = await WaitForAnyRunAsync(h.Runs, h.Slug, TimeSpan.FromSeconds(2));
        Assert.Null(run); // never falls back to in-place execution

        var receipt = Assert.Single(await CommentsAsync(h, ticket.Id), c => c.Contains("worktree-isolation-failure/v1"));
        using var doc = JsonDocument.Parse(receipt);
        Assert.Equal("NotAGitRepo", doc.RootElement.GetProperty("outcome").GetString());

        var after = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        Assert.Null(after!.WorktreeBranch);
    }

    // ── The R4 lease gate is never bypassed by isolation ─────────────────────

    [Fact]
    public async Task A_worktree_dispatch_that_conflicts_with_an_active_lease_is_still_blocked_and_never_creates_a_worktree()
    {
        using var h = await BuildAsync("wt-lease-block", "programmer", enforcement: "block", gitInit: true);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Doing");

        var holder = await h.Leases.AcquireAsync(
            h.Slug, ticket.Id, "run-holder", "code-janitor", [LeasedGlob], DateTime.UtcNow, TimeSpan.FromMinutes(30));
        Assert.True(holder.IsAcquired);

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, MakeAutomation("programmer"),
            new TriggerFiring(ticket.Id, ticket.Title, "Doing"), CancellationToken.None);

        var run = await WaitForAnyRunAsync(h.Runs, h.Slug, TimeSpan.FromSeconds(2));
        Assert.Null(run); // block mode: the dispatch never reaches ClaudeRunner.RunAsync

        var receipt = Assert.Single(await CommentsAsync(h, ticket.Id), c => c.Contains("file-lease-denial/v1"));
        using var doc = JsonDocument.Parse(receipt);
        Assert.Equal("block", doc.RootElement.GetProperty("enforcementMode").GetString());

        // The lease gate ran BEFORE worktree creation: a blocked dispatch never touched git, and
        // the ticket carries no worktree state at all.
        var after = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        Assert.Null(after!.WorktreeBranch);
        Assert.Null(after.WorktreePath);
        Assert.False(Directory.Exists(WorktreeManager.PathFor(h.Workspace, ticket.Id)));

        // Only the holder's lease is active — the blocked worktree dispatch never claimed one.
        Assert.Single(await h.Leases.ListActiveAsync(h.Slug));
    }

    [Fact]
    public async Task A_worktree_dispatch_with_a_disjoint_scope_proceeds_and_still_holds_its_own_lease()
    {
        using var h = await BuildAsync("wt-lease-disjoint", "programmer", enforcement: "block", gitInit: true);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Doing");

        var holder = await h.Leases.AcquireAsync(
            h.Slug, ticket.Id, "run-holder", "writer", ["docs/**"], DateTime.UtcNow, TimeSpan.FromMinutes(30));
        Assert.True(holder.IsAcquired);

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, MakeAutomation("programmer"),
            new TriggerFiring(ticket.Id, ticket.Title, "Doing"), CancellationToken.None);

        var run = await WaitForAnyRunAsync(h.Runs, h.Slug, TimeSpan.FromSeconds(10));
        Assert.NotNull(run); // disjoint scopes: the lease gate let the worktree dispatch through

        var after = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        Assert.NotNull(after!.WorktreeBranch);
        Assert.DoesNotContain(await CommentsAsync(h, ticket.Id), c => c.Contains("file-lease-denial/v1"));
    }

    // ── Done-triggered cleanup ────────────────────────────────────────────────

    private static AutomationRule MakeDoneAutomation() => new()
    {
        Id = "test-worktree-done",
        Enabled = true,
        Trigger = new StatusChangeTriggerSpec { To = "Done" },
        Actions = [new MoveTicketStatusActionSpec { To = "Done" }],
    };

    [Fact]
    public async Task A_ticket_reaching_Done_with_a_clean_merged_worktree_gets_it_cleaned_up()
    {
        using var h = await BuildAsync("wt-done-clean", "programmer", enforcement: "block", gitInit: true);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Review");

        var ensured = await WorktreeManager.EnsureAsync(h.Workspace, ticket.Id, CancellationToken.None);
        Assert.True(ensured.IsReady);
        await h.Tickets.SetWorktreeStateAsync(h.Slug, ticket.Id, ensured.Branch!, ensured.Path!, "active");
        await RunGitAsync(h.Workspace, $"merge --ff-only {ensured.Branch}"); // makes the branch "merged"

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, MakeDoneAutomation(),
            new TriggerFiring(ticket.Id, ticket.Title, "Review"), CancellationToken.None);

        var after = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        Assert.Equal("Done", after!.Status);
        Assert.Equal("cleaned", after.WorktreeStatus);
        Assert.False(Directory.Exists(ensured.Path));
    }

    [Fact]
    public async Task A_ticket_reaching_Done_with_a_dirty_worktree_is_flagged_never_silently_deleted()
    {
        using var h = await BuildAsync("wt-done-dirty", "programmer", enforcement: "block", gitInit: true);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Review");

        var ensured = await WorktreeManager.EnsureAsync(h.Workspace, ticket.Id, CancellationToken.None);
        Assert.True(ensured.IsReady);
        await h.Tickets.SetWorktreeStateAsync(h.Slug, ticket.Id, ensured.Branch!, ensured.Path!, "active");
        await File.WriteAllTextAsync(Path.Combine(ensured.Path!, "scratch.txt"), "uncommitted");

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, MakeDoneAutomation(),
            new TriggerFiring(ticket.Id, ticket.Title, "Review"), CancellationToken.None);

        var after = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        Assert.Equal("Done", after!.Status);
        Assert.Equal("dirty", after.WorktreeStatus);
        Assert.True(Directory.Exists(ensured.Path)); // never silently deleted
        Assert.True(File.Exists(Path.Combine(ensured.Path!, "scratch.txt")));

        var receipt = Assert.Single(await CommentsAsync(h, ticket.Id), c => c.Contains("worktree-cleanup-blocked/v1"));
        using var doc = JsonDocument.Parse(receipt);
        Assert.Equal("Dirty", doc.RootElement.GetProperty("outcome").GetString());
    }
}
