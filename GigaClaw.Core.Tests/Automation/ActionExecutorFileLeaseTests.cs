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
/// R4's dispatch-path wiring (doc/roadmap/lane-codex-runtime.md): <see cref="ActionExecutor.TryAcquireDispatchLeaseAsync"/>
/// leases the ticket's declared file scope before <see cref="ClaudeRunner.RunAsync"/> is ever
/// called, and the block/warn split governs what happens on a real conflict. Unlike
/// <see cref="FileLeaseStoreTests"/> (which exercises <see cref="FileLeaseStore"/> directly), this
/// suite dispatches through the real <see cref="ActionExecutor.ExecuteAutomationAsync"/> path with a
/// real <see cref="FileLeaseStore"/> and a real <see cref="ContractPolicy"/> loaded from
/// <c>contracts.json</c>, so it proves the wiring rather than the primitives.
/// <para>
/// Dispatches use a non-Claude model with no <c>LocalModelBaseUrl</c> configured
/// (<see cref="ClaudeRunner"/>'s <c>OllamaValidationError</c> path — see
/// <see cref="OllamaLocalModelTests"/>), which fails a dispatched run immediately without spawning
/// a subprocess. That is exactly what this suite needs: whether <see cref="ClaudeRunner.RunAsync"/>
/// was ever *called* — i.e. whether a run was registered in <see cref="AgentRunRegistry"/> at all —
/// is the observable signal for "did the lease gate let this dispatch through", independent of
/// whether the run then succeeds.
/// </para>
/// </summary>
[Collection("MockClaude")]
public class ActionExecutorFileLeaseTests
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

    private static async Task<Harness> BuildAsync(string projectName, string agentName, string? enforcement)
    {
        var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync(projectName);
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
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

    /// <summary>Same shape as <c>PolicyBlockModeTests.Manifest</c>: one agent entry, declaring
    /// <see cref="LeasedGlob"/> as its <c>allowedWriteGlobs</c> — the R4 fallback scope source when
    /// a ticket has no handoff <c>ownedFiles</c> yet.</summary>
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

    private static AutomationRule MakeAutomation(string agentName) => new()
    {
        Id = "test-lease-dispatch",
        Enabled = true,
        Trigger = new StatusChangeTriggerSpec { To = "Doing" },
        Actions = [new RunAgentActionSpec { Agent = agentName, MaxTurns = 1, Model = NonClaudeModel }],
    };

    private static async Task<List<string>> CommentsAsync(Harness h, int ticketId)
    {
        var ticket = await h.Tickets.GetTicketAsync(h.Slug, ticketId);
        return ticket!.Comments.Select(c => c.Content).ToList();
    }

    /// <summary>Writes a v1 handoff comment declaring <paramref name="ownedFiles"/> — the P9
    /// artifact <see cref="FileLeaseScopeResolver"/> prefers over the agent's own
    /// <c>allowedWriteGlobs</c> (doc/roadmap/index.md: "C6 handoff artifacts ... ownedFiles is the
    /// interface R4 leases consume").</summary>
    private static async Task WriteHandoffAsync(
        Harness h, int ticketId, string agent, string runId, params string[] ownedFiles)
    {
        var handoff = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            agent,
            ticketId,
            runId,
            summary = "Handed off.",
            inputs = Array.Empty<object>(),
            outputs = Array.Empty<object>(),
            ownedFiles,
            producedAtUtc = DateTime.UtcNow.ToString("O"),
        });
        var comment = $"GIGACLAW-HANDOFF v1 {agent} ticket-{ticketId} run-{runId}\n```json\n{handoff}\n```";
        await h.Tickets.AddCommentAsync(h.Slug, ticketId, comment, "automation");
    }

    /// <summary>Polls until <see cref="AgentRunRegistry"/> settles: either a run was registered for
    /// the project (the dispatch went through the lease gate, whether or not it then succeeds) or
    /// the timeout proves none ever will (the dispatch was blocked before <see cref="ClaudeRunner.RunAsync"/>
    /// was called).</summary>
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
        catch (OperationCanceledException) { /* timed out with no run — that is a valid outcome here */ }
        return runs.AllForProject(slug).FirstOrDefault();
    }

    // ── Block mode: fails closed ────────────────────────────────────────────

    [Fact]
    public async Task Block_mode_conflict_never_dispatches_and_writes_a_block_receipt()
    {
        using var h = await BuildAsync("lease-block-mode", "programmer", enforcement: "block");
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Doing");

        var holder = await h.Leases.AcquireAsync(
            h.Slug, ticket.Id, "run-holder", "code-janitor", [LeasedGlob], DateTime.UtcNow, TimeSpan.FromMinutes(30));
        Assert.True(holder.IsAcquired);

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, MakeAutomation("programmer"),
            new TriggerFiring(ticket.Id, ticket.Title, "Doing"), CancellationToken.None);

        // Give the (synchronous, in-process) dispatch attempt time to either register a run or not.
        var run = await WaitForAnyRunAsync(h.Runs, h.Slug, TimeSpan.FromSeconds(2));
        Assert.Null(run); // block mode: ClaudeRunner.RunAsync was never called

        var comments = await CommentsAsync(h, ticket.Id);
        var receipt = Assert.Single(comments, c => c.Contains("file-lease-denial/v1"));
        using var doc = JsonDocument.Parse(receipt);
        Assert.Equal("file-lease-denial/v1", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal("programmer", doc.RootElement.GetProperty("agent").GetString());
        Assert.Equal("block", doc.RootElement.GetProperty("enforcementMode").GetString());
        Assert.Equal("run-holder", doc.RootElement.GetProperty("conflictingRunId").GetString());
        Assert.Equal("code-janitor", doc.RootElement.GetProperty("conflictingAgent").GetString());
        Assert.Contains(LeasedGlob, doc.RootElement.GetProperty("scope").EnumerateArray().Select(e => e.GetString()));

        // Only the holder's lease is active — the blocked dispatch never claimed one.
        Assert.Single(await h.Leases.ListActiveAsync(h.Slug));
    }

    // ── Warn mode: mirrors R2/R3 shadow semantics — logs, but proceeds ─────

    [Fact]
    public async Task Warn_mode_conflict_still_dispatches_and_writes_a_warn_receipt()
    {
        using var h = await BuildAsync("lease-warn-mode", "programmer", enforcement: "warn");
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Doing");

        var holder = await h.Leases.AcquireAsync(
            h.Slug, ticket.Id, "run-holder", "code-janitor", [LeasedGlob], DateTime.UtcNow, TimeSpan.FromMinutes(30));
        Assert.True(holder.IsAcquired);

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, MakeAutomation("programmer"),
            new TriggerFiring(ticket.Id, ticket.Title, "Doing"), CancellationToken.None);

        // Warn mode: the dispatch is not stopped, so a run is registered — same as R2's shadow
        // mode not stopping an out-of-glob write. It fails fast via OllamaValidationError, but the
        // observable fact under test is that ClaudeRunner.RunAsync was actually called.
        var run = await WaitForAnyRunAsync(h.Runs, h.Slug, TimeSpan.FromSeconds(10));
        Assert.NotNull(run);
        Assert.Equal("programmer", run!.AgentName);

        var comments = await CommentsAsync(h, ticket.Id);
        var receipt = Assert.Single(comments, c => c.Contains("file-lease-denial/v1"));
        using var doc = JsonDocument.Parse(receipt);
        Assert.Equal("warn", doc.RootElement.GetProperty("enforcementMode").GetString());
        Assert.Equal("run-holder", doc.RootElement.GetProperty("conflictingRunId").GetString());

        // The warn-mode dispatch proceeded without registering a lease of its own — the holder's
        // lease is still the only active one.
        Assert.Single(await h.Leases.ListActiveAsync(h.Slug));
    }

    [Fact]
    public async Task An_agent_with_no_enforcement_key_defaults_to_warn_and_proceeds()
    {
        // Same default as R3's ContractPolicy: absent enforcement means shadow mode, not blocking.
        using var h = await BuildAsync("lease-default-mode", "programmer", enforcement: null);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Doing");

        var holder = await h.Leases.AcquireAsync(
            h.Slug, ticket.Id, "run-holder", "code-janitor", [LeasedGlob], DateTime.UtcNow, TimeSpan.FromMinutes(30));
        Assert.True(holder.IsAcquired);

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, MakeAutomation("programmer"),
            new TriggerFiring(ticket.Id, ticket.Title, "Doing"), CancellationToken.None);

        var run = await WaitForAnyRunAsync(h.Runs, h.Slug, TimeSpan.FromSeconds(10));
        Assert.NotNull(run);
    }

    // ── Disjoint scopes: no conflict at all ─────────────────────────────────

    [Fact]
    public async Task Disjoint_scope_dispatches_normally_and_holds_its_own_lease_alongside_the_other()
    {
        using var h = await BuildAsync("lease-disjoint", "programmer", enforcement: "block");
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Doing");

        var holder = await h.Leases.AcquireAsync(
            h.Slug, ticket.Id, "run-holder", "writer", ["docs/**"], DateTime.UtcNow, TimeSpan.FromMinutes(30));
        Assert.True(holder.IsAcquired);

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, MakeAutomation("programmer"),
            new TriggerFiring(ticket.Id, ticket.Title, "Doing"), CancellationToken.None);

        var run = await WaitForAnyRunAsync(h.Runs, h.Slug, TimeSpan.FromSeconds(10));
        Assert.NotNull(run);

        // Disjoint scopes both proceed: no conflict was ever raised (no denial receipt), and the
        // pre-existing docs/** holder was never touched by the src/** dispatch's acquire/release —
        // it is unaffected by a scope that never intersected it. (The dispatch's own src/** lease
        // is acquired and then released again as part of normal run completion — Ollama's
        // fast-fail path completes almost immediately — so it is not asserted as still active
        // here; disjointness is what's under test, not the post-completion snapshot.)
        Assert.Contains(await h.Leases.ListActiveAsync(h.Slug), l => l.RunId == "run-holder");
        Assert.DoesNotContain(await CommentsAsync(h, ticket.Id), c => c.Contains("file-lease-denial/v1"));
    }

    // ── Scope source: handoff ownedFiles beats allowedWriteGlobs ────────────

    [Fact]
    public async Task Handoff_ownedFiles_is_the_leased_scope_when_present_not_the_agents_allowedWriteGlobs()
    {
        using var h = await BuildAsync("lease-handoff-scope", "programmer", enforcement: "block");
        // contracts.json declares allowedWriteGlobs=["src/**"], but the ticket's newest handoff
        // claims "assets/**" — FileLeaseScopeResolver must prefer the handoff.
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Doing");
        await WriteHandoffAsync(h, ticket.Id, "programmer", "run-prior", "assets/**");

        // A holder on assets/** proves the handoff scope was leased: if allowedWriteGlobs
        // ("src/**") had been used instead, this would not conflict and the run would proceed.
        var holder = await h.Leases.AcquireAsync(
            h.Slug, ticket.Id, "run-holder", "designer", ["assets/**"], DateTime.UtcNow, TimeSpan.FromMinutes(30));
        Assert.True(holder.IsAcquired);

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, MakeAutomation("programmer"),
            new TriggerFiring(ticket.Id, ticket.Title, "Doing"), CancellationToken.None);

        var run = await WaitForAnyRunAsync(h.Runs, h.Slug, TimeSpan.FromSeconds(2));
        Assert.Null(run); // blocked: the handoff's assets/** scope conflicted, not src/**

        var receipt = Assert.Single(await CommentsAsync(h, ticket.Id), c => c.Contains("file-lease-denial/v1"));
        using var doc = JsonDocument.Parse(receipt);
        Assert.Contains("assets/**", doc.RootElement.GetProperty("scope").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Handoff_scope_that_does_not_conflict_proceeds_even_though_allowedWriteGlobs_would_have()
    {
        using var h = await BuildAsync("lease-handoff-disjoint", "programmer", enforcement: "block");
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Doing");
        await WriteHandoffAsync(h, ticket.Id, "programmer", "run-prior", "assets/**");

        // Holder overlaps allowedWriteGlobs (src/**) but not the handoff's actual scope
        // (assets/**) — if the resolver wrongly fell back to allowedWriteGlobs, this would block.
        var holder = await h.Leases.AcquireAsync(
            h.Slug, ticket.Id, "run-holder", "code-janitor", [LeasedGlob], DateTime.UtcNow, TimeSpan.FromMinutes(30));
        Assert.True(holder.IsAcquired);

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, MakeAutomation("programmer"),
            new TriggerFiring(ticket.Id, ticket.Title, "Doing"), CancellationToken.None);

        var run = await WaitForAnyRunAsync(h.Runs, h.Slug, TimeSpan.FromSeconds(10));
        Assert.NotNull(run);
        Assert.DoesNotContain(await CommentsAsync(h, ticket.Id), c => c.Contains("file-lease-denial/v1"));
    }
}
