using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// C5 part 1: <c>parallelRunAgents</c>. The action declares branches inline; everything it does
/// afterwards is a team run (C4), because an ad-hoc <see cref="TeamDefinition"/> is exactly what a
/// <see cref="TeamRun"/>'s definition snapshot already stores.
/// <para>
/// The load-bearing claim these tests defend is that the action <b>does not dispatch anything</b>.
/// It puts one sub-ticket per branch in the dispatch column and lets the ordinary per-agent
/// automation start it — which is the only reason a branch queues behind
/// <see cref="RunConcurrencyGate"/> and takes its R4 file leases like every other run.
/// </para>
/// </summary>
public sealed class ParallelRunAgentsTests
{
    // ── The plan (spec → definition), I/O-free ──────────────────────────────

    [Fact]
    public void Branches_become_one_role_and_one_task_each()
    {
        var definition = ParallelRunPlan.ToDefinition(new ParallelRunAgentsActionSpec
        {
            RunSlug = "review-fanout",
            Name = "Parallel review",
            MaxConcurrency = 2,
            Join = "quorum",
            Quorum = 2,
            Synthesizer = "producer",
            OnPartialFailure = "failFast",
            Branches =
            [
                new ParallelBranchSpec { Agent = "programmer", Key = "security", Title = "Security lane", Prompt = "Audit the diff." },
                new ParallelBranchSpec { Agent = "qa-tester" },
                new ParallelBranchSpec { Agent = "programmer", Key = "performance" }
            ]
        });

        Assert.Equal("review-fanout", definition.Slug);
        Assert.Equal("Parallel review", definition.Name);
        Assert.True(definition.IsExecutable);
        Assert.Equal(2, definition.MaxConcurrency);
        Assert.Equal(TeamJoinMode.Quorum, definition.JoinPolicy.Mode);
        Assert.Equal(2, definition.JoinPolicy.Quorum);
        Assert.Equal(TeamPartialFailure.FailFast, definition.PartialFailure);
        Assert.Equal(ParallelRunPlan.SynthesizerRoleId, definition.SynthesizerRole);
        Assert.Equal("producer", definition.FindRole(ParallelRunPlan.SynthesizerRoleId)!.AgentSlug);

        // Three branch tasks, no edges between them: parallel is the absence of dependencies.
        Assert.Equal(["security", "qa-tester", "performance"], definition.TaskGraph.Select(t => t.Key));
        Assert.All(definition.TaskGraph, task => Assert.Empty(task.DependsOn));
        Assert.Equal("Security lane", definition.FindTask("security")!.Title);
        Assert.Equal("Audit the diff.", definition.FindTask("security")!.Prompt);
        // No key given → the agent slug is the key, and no title given → the agent slug is the title.
        Assert.Equal("qa-tester", definition.FindTask("qa-tester")!.Title);
    }

    [Theory]
    // A spec that cannot describe a runnable fan-out is refused rather than best-effort executed:
    // each of these would otherwise silently run something other than what was written.
    [InlineData("no branches")]
    [InlineData("blank agent")]
    [InlineData("duplicate key")]
    [InlineData("reserved key")]
    [InlineData("unknown join")]
    [InlineData("unknown partial failure")]
    [InlineData("quorum too large")]
    [InlineData("negative concurrency")]
    public void A_spec_that_cannot_run_is_refused(string flaw)
    {
        var spec = new ParallelRunAgentsActionSpec
        {
            Branches =
            [
                new ParallelBranchSpec { Agent = "programmer" },
                new ParallelBranchSpec { Agent = "qa-tester" }
            ]
        };

        var expected = flaw switch
        {
            "no branches" => Apply(spec, s => s.Branches.Clear(), "at least one branch"),
            "blank agent" => Apply(spec, s => s.Branches[1].Agent = "  ", "no agent"),
            "duplicate key" => Apply(spec, s => s.Branches[1].Key = "programmer", "used twice"),
            "reserved key" => Apply(spec, s => s.Branches[1].Key = ParallelRunPlan.SynthesizerRoleId, "reserved key"),
            "unknown join" => Apply(spec, s => s.Join = "whenever", "Unknown join policy"),
            "unknown partial failure" => Apply(spec, s => s.OnPartialFailure = "shrug", "Unknown onPartialFailure"),
            "quorum too large" => Apply(spec, s => { s.Join = "quorum"; s.Quorum = 5; }, "exceeds"),
            _ => Apply(spec, s => s.MaxConcurrency = -1, "negative")
        };

        var exception = Assert.Throws<ParallelRunPlanException>(() => ParallelRunPlan.ToDefinition(spec));
        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);

        static string Apply(ParallelRunAgentsActionSpec spec, Action<ParallelRunAgentsActionSpec> mutate, string expected)
        {
            mutate(spec);
            return expected;
        }
    }

    // ── Fan-out through the normal dispatch path ────────────────────────────

    [Fact]
    public async Task The_action_creates_one_sub_ticket_per_branch_and_dispatches_nothing_itself()
    {
        using var sut = await Sut.CreateAsync("pra-fanout");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");

        await sut.FireAsync(parent, Spec(s =>
        {
            s.Branches[0].Prompt = "Audit the diff.";
            s.Synthesizer = "producer";
        }));

        var run = Assert.Single(await sut.Teams.ListRunsAsync(sut.Slug, parent.Id));
        Assert.Equal(TeamRunStatus.Running, run.Status);

        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        Assert.Equal(3, tasks.Count);
        foreach (var task in tasks)
        {
            var ticket = await sut.Tickets.GetTicketAsync(sut.Slug, task.TicketId);
            Assert.Equal(parent.Id, ticket!.ParentId);
            Assert.Equal(task.AgentSlug, ticket.AssignedTo);
            // The dispatch column the per-agent ticketInColumn automations already watch. This is
            // the handover: nothing else in this action knows how to start an agent.
            Assert.Equal(TeamRunService.ReadyStatus, ticket.Status);
            Assert.Equal(TeamTaskStatus.Dispatched, task.Status);
            Assert.Empty(task.BlockedByTicketIds);
        }
        Assert.Equal("Audit the diff.",
            (await sut.Tickets.GetTicketAsync(sut.Slug, tasks.Single(t => t.TemplateKey == "programmer").TicketId))!.Description);

        // The action itself started no claude subprocess: no run was ever registered.
        Assert.Empty(sut.AgentRuns.AllForProject(sut.Slug));
    }

    [Fact]
    public async Task Firing_again_while_the_run_is_open_re_attaches_instead_of_fanning_out_twice()
    {
        using var sut = await Sut.CreateAsync("pra-idempotent");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");

        await sut.FireAsync(parent, Spec());
        await sut.FireAsync(parent, Spec());

        var run = Assert.Single(await sut.Teams.ListRunsAsync(sut.Slug, parent.Id));
        Assert.Equal(3, (await sut.Teams.ListTasksAsync(sut.Slug, run.Id)).Count);
        Assert.Equal(3, (await sut.Tickets.ListTicketsAsync(sut.Slug, parentId: parent.Id)).Count);
    }

    [Fact]
    public async Task An_unrunnable_spec_leaves_a_receipt_on_the_ticket_instead_of_a_half_run()
    {
        using var sut = await Sut.CreateAsync("pra-bad-spec");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");

        await sut.FireAsync(parent, Spec(s => s.Join = "whenever"));

        Assert.Empty(await sut.Teams.ListRunsAsync(sut.Slug, parent.Id));
        var ticket = await sut.Tickets.GetTicketAsync(sut.Slug, parent.Id);
        Assert.Contains(ticket!.Activities, entry =>
            entry.Text.Contains("Parallel branches could not be started", StringComparison.Ordinal)
            && entry.Text.Contains("whenever", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_ad_hoc_run_resumes_after_every_service_is_thrown_away()
    {
        // The crux of implementing the action as a team run: the ad-hoc definition is never stored
        // as a TeamDefinition row, so if the snapshot were not authoritative a restart would lose it.
        using var tmp = new TempDir();
        long runId;
        int parentId;
        {
            using var first = await Sut.AttachAsync(tmp, "pra-restart", create: true);
            var parent = await first.Tickets.CreateTicketAsync(first.Slug, "Review the release", status: "Review");
            parentId = parent.Id;
            await first.FireAsync(parent, Spec(s => s.Synthesizer = "producer"));
            runId = (await first.Teams.ListRunsAsync(first.Slug, parent.Id)).Single().Id;

            foreach (var task in await first.Teams.ListTasksAsync(first.Slug, runId))
                await first.Tickets.MoveTicketAsync(first.Slug, task.TicketId, "Done", "automation");
        }

        using var second = await Sut.AttachAsync(tmp, "pra-restart", create: false);
        var resumed = await second.Runs.ReconcileRunAsync(second.Slug, runId);

        // A brand-new set of services read the snapshot, saw every lane reported, and joined.
        Assert.Equal(TeamRunStatus.Joining, resumed!.Status);
        Assert.Equal(3, resumed.Definition.TaskGraph.Count);
        var synthesis = await second.Tickets.GetTicketAsync(second.Slug, resumed.SynthesisTicketId!.Value);
        Assert.Equal("producer", synthesis!.AssignedTo);
        Assert.Equal(parentId, synthesis.ParentId);
    }

    // ── Max concurrency ─────────────────────────────────────────────────────

    [Fact]
    public async Task Max_concurrency_holds_branches_out_of_the_dispatch_column_until_a_slot_frees()
    {
        using var sut = await Sut.CreateAsync("pra-concurrency");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");

        await sut.FireAsync(parent, Spec(s => s.MaxConcurrency = 2));

        var run = (await sut.Teams.ListRunsAsync(sut.Slug, parent.Id)).Single();
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        Assert.Equal(2, tasks.Count(t => t.Status == TeamTaskStatus.Dispatched));

        // The third branch is not merely flagged as waiting — it is out of the dispatch column, so
        // the ordinary ticketInColumn automation cannot pick it up while the cap is full.
        var held = tasks.Single(t => t.Status == TeamTaskStatus.Pending);
        Assert.Equal(TeamRunService.HoldStatus, (await sut.Tickets.GetTicketAsync(sut.Slug, held.TicketId))!.Status);

        // One lane reports; the freed slot releases the held branch on the next reconcile.
        var first = tasks.First(t => t.Status == TeamTaskStatus.Dispatched);
        await sut.Tickets.MoveTicketAsync(sut.Slug, first.TicketId, "Done", "automation");
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);

        var after = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        Assert.Equal(TeamTaskStatus.Dispatched, after.Single(t => t.Id == held.Id).Status);
        Assert.Equal(TeamRunService.ReadyStatus, (await sut.Tickets.GetTicketAsync(sut.Slug, held.TicketId))!.Status);
        Assert.Equal(2, after.Count(t => t.Status == TeamTaskStatus.Dispatched));
    }

    [Fact]
    public async Task Zero_max_concurrency_releases_every_branch_at_once()
    {
        using var sut = await Sut.CreateAsync("pra-unlimited");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");

        await sut.FireAsync(parent, Spec(s => s.MaxConcurrency = 0));

        var run = (await sut.Teams.ListRunsAsync(sut.Slug, parent.Id)).Single();
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        Assert.Equal(3, tasks.Count(t => t.Status == TeamTaskStatus.Dispatched));
    }

    // ── Partial failure ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_branch_is_synthesized_with_the_gap_named()
    {
        using var sut = await Sut.CreateAsync("pra-gaps");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        await sut.FireAsync(parent, Spec(s =>
        {
            s.Synthesizer = "producer";
            s.OnPartialFailure = "synthesize";
        }));

        var run = (await sut.Teams.ListRunsAsync(sut.Slug, parent.Id)).Single();
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        var failing = tasks.Single(t => t.TemplateKey == "qa-tester");
        foreach (var task in tasks.Where(t => t.Id != failing.Id))
            await sut.Tickets.MoveTicketAsync(sut.Slug, task.TicketId, "Done", "automation");

        await sut.Runs.FailTaskAsync(sut.Slug, failing.TicketId, "the harness never came up");

        var joined = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Joining, joined!.Status);

        var synthesis = await sut.Tickets.GetTicketAsync(sut.Slug, joined.SynthesisTicketId!.Value);
        Assert.Equal("producer", synthesis!.AssignedTo);
        // Synthesize-with-gaps: the missing lane is named with its reason, never silently dropped.
        Assert.Contains("Lanes missing (1 of 3)", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("qa-tester", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("the harness never came up", synthesis.Description, StringComparison.Ordinal);

        // …and the parent carries the receipt either way.
        var parentTicket = await sut.Tickets.GetTicketAsync(sut.Slug, parent.Id);
        Assert.Contains(parentTicket!.Activities, entry => entry.Text.Contains("joined", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fail_fast_skips_the_synthesis_and_closes_the_run_with_a_receipt()
    {
        using var sut = await Sut.CreateAsync("pra-failfast");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        await sut.FireAsync(parent, Spec(s =>
        {
            s.Synthesizer = "producer";
            s.Join = "firstFailure";
            s.OnPartialFailure = "failFast";
        }));

        var run = (await sut.Teams.ListRunsAsync(sut.Slug, parent.Id)).Single();
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        var failing = tasks.Single(t => t.TemplateKey == "qa-tester");

        await sut.Runs.FailTaskAsync(sut.Slug, failing.TicketId, "the harness never came up");

        var closed = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Failed, closed!.Status);
        Assert.Null(closed.SynthesisTicketId);

        // No synthesis sub-ticket exists at all — the three branches are the only children.
        var children = await sut.Tickets.ListTicketsAsync(sut.Slug, parentId: parent.Id);
        Assert.Equal(3, children.Count);
        Assert.DoesNotContain(children, child => child.Title.StartsWith("Synthesize:", StringComparison.Ordinal));

        // The decision changes what happens next, never whether it is recorded.
        var parentTicket = await sut.Tickets.GetTicketAsync(sut.Slug, parent.Id);
        Assert.Contains(parentTicket!.Activities, entry =>
            entry.Text.Contains("fail-fast", StringComparison.Ordinal)
            && entry.Text.Contains("skipping synthesis", StringComparison.Ordinal));
        Assert.Contains(parentTicket.Activities, entry =>
            entry.Text.Contains("failed", StringComparison.Ordinal)
            && entry.Text.Contains("the harness never came up", StringComparison.Ordinal));

        // First-failure also stops the lanes that were still running, so a decided run stops
        // spending tokens.
        var settled = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        Assert.Equal(2, settled.Count(t => t.Status == TeamTaskStatus.Cancelled));
        foreach (var cancelled in settled.Where(t => t.Status == TeamTaskStatus.Cancelled))
            Assert.Equal(TeamRunService.ParkedStatus,
                (await sut.Tickets.GetTicketAsync(sut.Slug, cancelled.TicketId))!.Status);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    /// <summary>Three branches on three different agents, no synthesizer unless a test adds one.</summary>
    internal static ParallelRunAgentsActionSpec Spec(Action<ParallelRunAgentsActionSpec>? tweak = null)
    {
        var spec = new ParallelRunAgentsActionSpec
        {
            RunSlug = "parallel-review",
            Name = "Parallel review",
            Branches =
            [
                new ParallelBranchSpec { Agent = "programmer" },
                new ParallelBranchSpec { Agent = "qa-tester" },
                new ParallelBranchSpec { Agent = "groomer" }
            ]
        };
        tweak?.Invoke(spec);
        return spec;
    }

    internal static AutomationRule Automation(ParallelRunAgentsActionSpec spec) => new()
    {
        Id = "fan-out",
        Enabled = true,
        Trigger = new TicketInColumnTriggerSpec { Columns = ["Review"] },
        Conditions = [],
        Actions = [spec]
    };

    internal sealed class Sut : IDisposable
    {
        internal static readonly string[] Agents = ["programmer", "qa-tester", "groomer", "producer"];

        private readonly TempDir? _owned;

        private Sut(TempDir? owned, ProjectService projects, TicketService tickets, string slug, string workspace)
        {
            _owned = owned;
            Projects = projects;
            Tickets = tickets;
            Slug = slug;
            Teams = new TeamStore(projects, tickets);
            var members = new MemberService(projects);
            Runs = new TeamRunService(Teams, tickets, members, new AgentTeamService(), NullLogger<TeamRunService>.Instance);

            AgentRuns = new AgentRunRegistry();
            var sessions = new SessionRegistry();
            var cost = new CostTracker();
            Gate = new RunConcurrencyGate(maxConcurrent: 1);
            // A real lease store, always: without a contracts.json declaring write globs the scope
            // resolver finds nothing and leasing is NotApplicable, so this changes nothing for the
            // tests that do not care — and it is the real wiring for the one that does.
            Leases = new FileLeaseStore(projects);
            Executor = new ActionExecutor(
                tickets, members, new LabelService(projects), sessions, AgentRuns,
                new ClaudeRunner(sessions, AgentRuns, Gate, NullLogger<ClaudeRunner>.Instance),
                cost, new LocalizationService(new AppSettingsService(projects.DataDir)), projects,
                new RunStateManager(AgentRuns, cost, tickets, NullLogger.Instance),
                FakeHttpClientFactory.Unused, Runs, NullLogger.Instance,
                outboundGate: null, leases: Leases);

            Runtime = new ProjectRuntime(slug) { Workspace = workspace, Config = new AutomationConfig() };
        }

        public ProjectService Projects { get; }
        public TicketService Tickets { get; }
        public TeamStore Teams { get; }
        public TeamRunService Runs { get; }
        public ActionExecutor Executor { get; }
        public AgentRunRegistry AgentRuns { get; }
        public RunConcurrencyGate Gate { get; }
        public FileLeaseStore Leases { get; }
        public ProjectRuntime Runtime { get; }
        public string Slug { get; }

        public static async Task<Sut> CreateAsync(string name)
        {
            var tmp = new TempDir();
            return await AttachAsync(tmp, name, create: true, owned: tmp);
        }

        public static async Task<Sut> AttachAsync(TempDir tmp, string name, bool create, TempDir? owned = null)
        {
            var projects = new ProjectService(tmp.Path);
            var project = create
                ? await projects.CreateProjectAsync(name)
                : (await projects.ListProjectsAsync()).Single(p => p.Name == name);
            var workspace = projects.ResolveWorkspacePath(project);
            Directory.CreateDirectory(workspace);
            var members = new MemberService(projects);
            var tickets = new TicketService(projects, members);

            if (create)
            {
                // Every branch is a sub-ticket assigned to its agent, so the agent has to be a member.
                var existing = (await members.ListMembersAsync(project.Slug))
                    .Select(member => member.Slug)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var agent in Agents.Where(agent => !existing.Contains(agent)))
                    await members.CreateMemberAsync(project.Slug, agent);
            }

            return new Sut(owned, projects, tickets, project.Slug, workspace);
        }

        public Task FireAsync(Ticket parent, ParallelRunAgentsActionSpec spec) =>
            Executor.ExecuteAutomationAsync(
                Runtime, Automation(spec),
                new TriggerFiring(parent.Id, parent.Title, parent.Status),
                CancellationToken.None);

        /// <summary>
        /// The ordinary per-agent dispatch: the automation an agent's <c>ticketInColumn</c> rule
        /// carries. Nothing about it knows this ticket came from a parallel fan-out — which is
        /// exactly the point.
        /// </summary>
        public Task DispatchAsync(Ticket branchTicket, string agent, CancellationToken ct, string? model = null) =>
            Executor.ExecuteAutomationAsync(
                Runtime,
                new AutomationRule
                {
                    Id = $"dispatch-{agent}",
                    Enabled = true,
                    Trigger = new TicketInColumnTriggerSpec { Columns = [TeamRunService.ReadyStatus], AssigneeSlug = agent },
                    Conditions = [],
                    Actions = [new RunAgentActionSpec { Agent = agent, MaxTurns = 1, Model = model }]
                },
                new TriggerFiring(branchTicket.Id, branchTicket.Title, branchTicket.Status),
                ct);

        /// <summary>Declares one write scope per agent, in block mode — the R4 contract shape.</summary>
        public void WriteContracts(string glob)
        {
            var agentsDir = Path.Combine(Runtime.Workspace!, ".agents");
            Directory.CreateDirectory(agentsDir);
            var entries = string.Join(",\n", Agents.Select(agent => $$"""
                    "{{agent}}": {
                      "enforcement": "block",
                      "dispatches": ["assignment"],
                      "riskClass": "code-write",
                      "allowedWriteGlobs": ["{{glob}}"],
                      "ticketExit": ["Review", "Blocked", "Done"]
                    }
                """));
            File.WriteAllText(Path.Combine(agentsDir, "contracts.json"), $$"""
                {
                  "version": 1,
                  "defaults": {
                    "maxDispatchAttempts": 3,
                    "retryBackoffSeconds": 300,
                    "requireAtomicHandoff": true,
                    "requireAuthorOnBoardWrites": true
                  },
                  "agents": {
                {{entries}}
                  }
                }
                """);
        }

        public void Dispose() => _owned?.Dispose();
    }
}

/// <summary>
/// The acceptance criterion C5 exists to prove: a parallel branch is dispatched by the <b>normal</b>
/// path, so it queues behind <see cref="RunConcurrencyGate"/> and takes its R4 file leases like any
/// other agent run. The proof is negative and unfakeable — the test holds the gate's only slot and
/// shows the branch cannot start until it is handed back.
/// </summary>
[Collection("MockClaude")]
public sealed class ParallelBranchDispatchGateTests
{
    [Fact]
    public async Task A_branch_cannot_start_while_the_run_concurrency_gate_is_full()
    {
        using var sut = await ParallelRunAgentsTests.Sut.CreateAsync("pra-gate");
        var workspace = sut.Runtime.Workspace!;
        foreach (var agent in ParallelRunAgentsTests.Sut.Agents)
            TestSkillBuilder.Create(workspace, agent, scenario: "default");

        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        await sut.FireAsync(parent, ParallelRunAgentsTests.Spec());

        var run = (await sut.Teams.ListRunsAsync(sut.Slug, parent.Id)).Single();
        var task = (await sut.Teams.ListTasksAsync(sut.Slug, run.Id)).First();
        var branchTicket = (await sut.Tickets.GetTicketAsync(sut.Slug, task.TicketId))!;

        // Take the gate's only slot from outside, exactly as another project's automation would.
        Assert.Equal(1, sut.Gate.Snapshot().Max);
        using var held = await sut.Gate.AcquireAsync(isChat: false, agentName: "someone-else", CancellationToken.None);
        Assert.Equal(1, sut.Gate.Snapshot().Active);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await sut.DispatchAsync(branchTicket, task.AgentSlug, cts.Token);

        // The branch is parked *on the gate*: the dispatch handed the run over, and the run is
        // waiting for a slot rather than executing. Had parallelRunAgents launched a subprocess of
        // its own, nothing would ever be queued here.
        while (sut.Gate.Snapshot().Queued == 0 && !cts.IsCancellationRequested)
            await Task.Delay(20, CancellationToken.None);
        Assert.Equal(1, sut.Gate.Snapshot().Queued);

        var agentRun = Assert.Single(sut.AgentRuns.AllForTicket(sut.Slug, branchTicket.Id));
        Assert.Equal(task.AgentSlug, agentRun.AgentName);
        Assert.NotEqual(AgentRunStatus.Completed, agentRun.Status);

        // Hand the slot back; only then can the branch run at all.
        held.Dispose();
        while (sut.AgentRuns.ActiveForProject(sut.Slug).Any() && !cts.IsCancellationRequested)
            await Task.Delay(20, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Completed, agentRun.Status);
        Assert.Equal(0, sut.Gate.Snapshot().Queued);
    }

    [Fact]
    public async Task A_branch_whose_file_scope_is_already_leased_is_refused_before_it_runs()
    {
        // The other half of the same criterion: being on the normal dispatch path is also what
        // subjects a branch to R4's file leases, which is the whole reason two lanes editing the
        // same files is safe to declare in one action.
        using var sut = await ParallelRunAgentsTests.Sut.CreateAsync("pra-lease");
        sut.WriteContracts("src/**");

        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        await sut.FireAsync(parent, ParallelRunAgentsTests.Spec());

        var run = (await sut.Teams.ListRunsAsync(sut.Slug, parent.Id)).Single();
        var task = (await sut.Teams.ListTasksAsync(sut.Slug, run.Id)).First();
        var branchTicket = (await sut.Tickets.GetTicketAsync(sut.Slug, task.TicketId))!;

        var holder = await sut.Leases.AcquireAsync(
            sut.Slug, branchTicket.Id, "run-holder", "code-janitor", ["src/**"],
            DateTime.UtcNow, TimeSpan.FromMinutes(30));
        Assert.True(holder.IsAcquired);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await sut.DispatchAsync(branchTicket, task.AgentSlug, cts.Token);

        // Block mode: the lease gate refused the branch before ClaudeRunner was ever called, and
        // said so on the branch's own sub-ticket.
        Assert.Empty(sut.AgentRuns.AllForProject(sut.Slug));
        var refused = await sut.Tickets.GetTicketAsync(sut.Slug, branchTicket.Id);
        Assert.Contains(refused!.Comments, comment =>
            comment.Content.Contains("file-lease-denial/v1", StringComparison.Ordinal)
            && comment.Content.Contains("run-holder", StringComparison.Ordinal));
        Assert.Single(await sut.Leases.ListActiveAsync(sut.Slug));
    }
}
