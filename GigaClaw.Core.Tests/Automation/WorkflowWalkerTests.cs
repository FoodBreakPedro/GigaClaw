using System.Security.Cryptography;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Automation.Runners;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Automation.Workflow;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// C5 follow-up: the runtime that walks a ticket through its project's declared workflow graph.
/// <para>
/// Everything here is hermetic — no <c>claude</c> subprocess is ever started, because the walker
/// deliberately starts none: a task state materializes a sub-ticket in the dispatch column and the
/// ordinary per-agent automation is what would dispatch it. Driving the services directly, the way
/// <c>TeamRunLifecycleTests</c> and <c>Sp3GateTests</c> do, is therefore the whole walk.
/// </para>
/// </summary>
public sealed class WorkflowWalkerTests
{
    // ── The graphs under test ───────────────────────────────────────────────

    /// <summary>
    /// The full shape the C5 doc declares: entry → task → task → verdict gate → fan-out → join →
    /// terminal, with the gate's FIX arm closing a legal (gated) cycle back to the entry.
    /// </summary>
    private static WorkflowGraph Pipeline(int maxCycles = 2) => new()
    {
        Initial = "draft",
        MaxCycles = maxCycles,
        States =
        [
            new WorkflowState("draft", WorkflowStateKind.Task)
            {
                Role = "blog-writer",
                Next = [new WorkflowTransition("review")]
            },
            new WorkflowState("review", WorkflowStateKind.Task)
            {
                Role = "blog-reviewer",
                Next = [new WorkflowTransition("verdict")]
            },
            new WorkflowState("verdict", WorkflowStateKind.Gate)
            {
                Gate = new VerdictIsConditionSpec { Verdicts = ["SHIP"] },
                Next =
                [
                    new WorkflowTransition("split") { When = "SHIP" },
                    new WorkflowTransition("draft") { When = "FIX" },
                    new WorkflowTransition("escalated") { When = "BLOCK" }
                ]
            },
            new WorkflowState("split", WorkflowStateKind.FanOut)
            {
                Next = [new WorkflowTransition("seo"), new WorkflowTransition("social")]
            },
            new WorkflowState("seo", WorkflowStateKind.Task)
            {
                Role = "blog-seo",
                Next = [new WorkflowTransition("gather")]
            },
            new WorkflowState("social", WorkflowStateKind.Task)
            {
                Role = "blog-repurpose",
                Next = [new WorkflowTransition("gather")]
            },
            new WorkflowState("gather", WorkflowStateKind.Join)
            {
                JoinOf = "split",
                Next = [new WorkflowTransition("publish")]
            },
            new WorkflowState("publish", WorkflowStateKind.Terminal),
            new WorkflowState("escalated", WorkflowStateKind.Terminal)
        ]
    };

    private static readonly string[] Agents = ["blog-writer", "blog-reviewer", "blog-seo", "blog-repurpose"];

    [Fact]
    public void The_graphs_under_test_are_ones_the_loader_would_accept()
    {
        Assert.Empty(Pipeline().Validate());
        Assert.Empty(Pipeline(maxCycles: 1).Validate());
    }

    // ── The end-to-end walk ─────────────────────────────────────────────────

    [Fact]
    public async Task A_ticket_walks_entry_task_gate_fan_out_join_and_terminal_end_to_end()
    {
        using var sut = await Sut.CreateAsync("walk-e2e", Pipeline());
        var ticket = await sut.OpenWalkAsync("Ship the launch post");

        // ── entry task ──
        var walk = await sut.AdvanceAsync(ticket.Id);
        var draft = sut.Child(await sut.ReloadAsync(ticket.Id), step: 1, "draft");
        Assert.Equal("blog-writer", draft.AssignedTo);
        Assert.Equal("Todo", draft.Status);   // the column the per-agent dispatch automation watches
        Assert.Equal("draft", walk.Open!.State);

        // ── second task ──
        await sut.ResolveAsync(draft.Id);
        walk = await sut.AdvanceAsync(ticket.Id);
        var review = sut.Child(await sut.ReloadAsync(ticket.Id), step: 2, "review");
        Assert.Equal("blog-reviewer", review.AssignedTo);
        Assert.Equal("review", walk.Open!.State);

        // ── gate: a real SHIP verdict on the ticket the reviewer worked ──
        await sut.PostVerdictAsync(review.Id, "blog-reviewer", "SHIP");
        await sut.ResolveAsync(review.Id);
        walk = await sut.AdvanceAsync(ticket.Id);

        Assert.Contains(walk.Steps, step =>
            step.Event == WorkflowWalkEvent.Left && step.State == "verdict" && step.Outcome == "SHIP" && step.To == "split");

        // ── fan-out: one sub-ticket per branch, run by the C4/C5 team machinery ──
        Assert.Equal("split", walk.Open!.State);
        var runId = walk.Open.RunId;
        Assert.NotNull(runId);
        var seo = sut.Child(await sut.ReloadAsync(ticket.Id), walk.Open.Step, "seo");
        var social = sut.Child(await sut.ReloadAsync(ticket.Id), walk.Open.Step, "social");
        Assert.Equal("blog-seo", seo.AssignedTo);
        Assert.Equal("blog-repurpose", social.AssignedTo);
        Assert.Equal(["seo", "social"], walk.Open.Branches);

        // The walk waits while the branches are open — no receipt, because "still working" is not
        // an event, and no second fan-out either.
        var branchesOpen = await sut.AdvanceAsync(ticket.Id);
        Assert.Equal("split", branchesOpen.Open!.State);
        Assert.Equal(walk.Steps.Count, branchesOpen.Steps.Count);

        // ── join → terminal ──
        await sut.ResolveAsync(seo.Id);
        await sut.ResolveAsync(social.Id);
        await sut.TeamRuns.ReconcileProjectAsync(sut.Slug);
        Assert.Equal(TeamRunStatus.Completed, (await sut.TeamRuns.GetRunAsync(sut.Slug, runId!.Value))!.Status);

        walk = await sut.AdvanceAsync(ticket.Id);

        Assert.Equal(WorkflowWalkStatus.Finished, walk.Status);
        Assert.Contains(walk.Steps, step => step.Event == WorkflowWalkEvent.Left && step.State == "split" && step.To == "gather");
        Assert.Contains(walk.Steps, step => step.Event == WorkflowWalkEvent.Left && step.State == "gather" && step.To == "publish");
        Assert.Contains(walk.Steps, step => step.Event == WorkflowWalkEvent.Finished && step.State == "publish");

        // The walk is only ever what the ticket says it is: replaying the comments from scratch
        // has to produce the same thing the pass returned.
        Assert.Equal(
            walk.Steps.Select(step => (step.Step, step.Event, step.State)),
            WorkflowWalker.Replay(await sut.ReloadAsync(ticket.Id)).Steps.Select(step => (step.Step, step.Event, step.State)));
    }

    // ── Visited-role tracking ───────────────────────────────────────────────

    [Fact]
    public async Task Every_traversal_records_the_role_that_handled_it_on_the_ticket()
    {
        using var sut = await Sut.CreateAsync("walk-roles", Pipeline());
        var ticket = await sut.OpenWalkAsync("Ship the launch post");
        await sut.RunToVerdictAsync(ticket.Id, "SHIP");

        // Re-read from the board, not from the pass: visited roles have to be durable, or a gate
        // asking "who has already seen this" would answer differently after a restart.
        var replayed = WorkflowWalker.Replay(await sut.ReloadAsync(ticket.Id));
        Assert.Equal(["blog-writer", "blog-reviewer"], replayed.VisitedRoles);

        var entries = replayed.Steps.Where(step => step.Event == WorkflowWalkEvent.Entered).ToList();
        Assert.Equal("blog-writer", entries.Single(step => step.State == "draft").Role);
        Assert.Equal("blog-reviewer", entries.Single(step => step.State == "review").Role);
        // A gate has no role to record, and recording one would be a lie about who worked it.
        Assert.Null(entries.Single(step => step.State == "verdict").Role);
    }

    [Fact]
    public async Task Turning_visited_role_tracking_off_stops_the_walk_recording_roles()
    {
        var graph = Pipeline() with { TrackVisitedRoles = false };
        using var sut = await Sut.CreateAsync("walk-no-roles", graph);
        var ticket = await sut.OpenWalkAsync("Ship the launch post");
        await sut.AdvanceAsync(ticket.Id);

        var replayed = WorkflowWalker.Replay(await sut.ReloadAsync(ticket.Id));
        Assert.Empty(replayed.VisitedRoles);
        // The state is still dispatched to its role — the switch is about the record, not the work.
        Assert.Equal("blog-writer", sut.Child(await sut.ReloadAsync(ticket.Id), step: 1, "draft").AssignedTo);
    }

    // ── Gates ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_gate_with_no_valid_verdict_parks_the_ticket_instead_of_passing_it()
    {
        using var sut = await Sut.CreateAsync("walk-gate-missing", Pipeline());
        var ticket = await sut.OpenWalkAsync("Ship the launch post");

        await sut.AdvanceAsync(ticket.Id);
        await sut.ResolveAsync(sut.Child(await sut.ReloadAsync(ticket.Id), 1, "draft").Id);
        await sut.AdvanceAsync(ticket.Id);

        var review = sut.Child(await sut.ReloadAsync(ticket.Id), 2, "review");
        // Prose instead of a verdict: MISSING, and the graph declares no MISSING arm.
        await sut.Tickets.AddCommentAsync(sut.Slug, review.Id, "Looks great to me, 93/100.", "blog-reviewer");
        await sut.ResolveAsync(review.Id);

        var walk = await sut.AdvanceAsync(ticket.Id);

        Assert.Equal(WorkflowWalkStatus.Parked, walk.Status);
        var parked = walk.Steps.Single(step => step.Event == WorkflowWalkEvent.Parked);
        Assert.Equal("verdict", parked.State);
        Assert.Contains("gate-undecidable", parked.Reason!, StringComparison.Ordinal);
        Assert.Contains("MISSING", parked.Reason!, StringComparison.Ordinal);
        Assert.Equal("Blocked", (await sut.ReloadAsync(ticket.Id)).Status);
        // Nothing beyond the gate was entered: fail closed means the split never fanned out.
        Assert.Empty(await sut.Teams.ListRunsAsync(sut.Slug, ticket.Id));
    }

    [Fact]
    public async Task A_FIX_verdict_cycles_the_walk_back_within_max_cycles()
    {
        using var sut = await Sut.CreateAsync("walk-fix-cycle", Pipeline(maxCycles: 1));
        var ticket = await sut.OpenWalkAsync("Ship the launch post");

        var walk = await sut.RunToVerdictAsync(ticket.Id, "FIX");

        Assert.Equal(WorkflowWalkStatus.Running, walk.Status);
        Assert.Contains(walk.Steps, step =>
            step.Event == WorkflowWalkEvent.Left && step.State == "verdict" && step.Outcome == "FIX" && step.To == "draft");
        Assert.Equal("draft", walk.Open!.State);
        Assert.Equal(2, walk.EntryCount("draft"));

        // A second round really is a second dispatch: a fresh sub-ticket, not the spent one.
        var parent = await sut.ReloadAsync(ticket.Id);
        var drafts = parent.SubTickets.Where(child => child.Title.Contains(":draft]", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, drafts.Count);
        Assert.Equal("Todo", sut.Child(parent, walk.Open.Step, "draft").Status);
    }

    [Fact]
    public async Task Exhausting_max_cycles_escalates_with_the_walk_history_in_the_receipt()
    {
        using var sut = await Sut.CreateAsync("walk-cycle-cap", Pipeline(maxCycles: 1));
        var ticket = await sut.OpenWalkAsync("Ship the launch post");

        await sut.RunToVerdictAsync(ticket.Id, "FIX");   // round 1
        var walk = await sut.RunToVerdictAsync(ticket.Id, "FIX");   // round 2 — one too many

        Assert.Equal(WorkflowWalkStatus.Parked, walk.Status);
        var parked = walk.Steps.Single(step => step.Event == WorkflowWalkEvent.Parked);
        Assert.Equal("draft", parked.State);
        Assert.Contains("max-cycles", parked.Reason!, StringComparison.Ordinal);
        Assert.Equal("Blocked", (await sut.ReloadAsync(ticket.Id)).Status);

        // The whole argument is on the ticket, the way C3's repair escalation puts every round's
        // reasons there: an owner never has to open a run log to see why this stopped.
        var receipt = (await sut.ReloadAsync(ticket.Id)).Comments
            .Single(comment => WorkflowWalk.IsWalkReceipt(comment.Content) && comment.Content.Contains("parked", StringComparison.Ordinal));
        Assert.Contains("The walk so far:", receipt.Content, StringComparison.Ordinal);
        Assert.Contains("step 3: Left 'verdict' on FIX", receipt.Content, StringComparison.Ordinal);
        Assert.Contains("step 6: Left 'verdict' on FIX", receipt.Content, StringComparison.Ordinal);

        // And nothing was dispatched for the round that was refused.
        Assert.Equal(2, (await sut.ReloadAsync(ticket.Id)).SubTickets
            .Count(child => child.Title.Contains(":draft]", StringComparison.Ordinal)));
    }

    // ── Fail-closed dispatch ────────────────────────────────────────────────

    [Fact]
    public async Task A_role_no_member_fills_parks_the_walk_with_a_receipt()
    {
        using var sut = await Sut.CreateAsync("walk-no-agent", Pipeline(), members: ["blog-reviewer"]);
        var ticket = await sut.OpenWalkAsync("Ship the launch post");

        var walk = await sut.AdvanceAsync(ticket.Id);

        Assert.Equal(WorkflowWalkStatus.Parked, walk.Status);
        var parked = walk.Steps.Single(step => step.Event == WorkflowWalkEvent.Parked);
        Assert.Contains("role-not-dispatchable", parked.Reason!, StringComparison.Ordinal);
        Assert.Contains("blog-writer", parked.Reason!, StringComparison.Ordinal);
        Assert.Empty((await sut.ReloadAsync(ticket.Id)).SubTickets);
        Assert.Equal("Blocked", (await sut.ReloadAsync(ticket.Id)).Status);
    }

    // ── Opting in ───────────────────────────────────────────────────────────

    [Fact]
    public async Task startWorkflow_re_attaches_to_a_running_walk_instead_of_restarting_it()
    {
        using var sut = await Sut.CreateAsync("walk-idempotent", Pipeline());
        var ticket = await sut.OpenWalkAsync("Ship the launch post");
        await sut.AdvanceAsync(ticket.Id);

        // The repeating ticketInColumn trigger fires the same action again.
        await sut.OpenWalkAsync(ticket.Id);
        var walk = await sut.AdvanceAsync(ticket.Id);

        Assert.Equal("draft", walk.Open!.State);
        Assert.Equal(1, walk.EntryCount("draft"));
        Assert.Single((await sut.ReloadAsync(ticket.Id)).SubTickets);
    }

    [Fact]
    public async Task startWorkflow_refuses_a_state_the_graph_does_not_declare()
    {
        using var sut = await Sut.CreateAsync("walk-bad-entry", Pipeline());
        var ticket = await sut.Tickets.CreateTicketAsync(sut.Slug, "Ship the launch post", status: "Review");

        await sut.ExecuteAsync(ticket.Id, new StartWorkflowActionSpec { At = "publlish" });

        var reloaded = await sut.ReloadAsync(ticket.Id);
        Assert.DoesNotContain(reloaded.Comments, comment => WorkflowWalk.IsWalkReceipt(comment.Content));
        Assert.Contains(reloaded.Activities, entry => entry.Text.Contains("publlish", StringComparison.Ordinal));
    }

    // ── Restart ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_restart_mid_walk_resumes_without_repeating_a_state_or_dispatching_twice()
    {
        using var tmp = new TempDir();
        int ticketId;
        int reviewTicketId;

        using (var before = await Sut.AttachAsync(tmp, "walk-restart", Pipeline(), create: true))
        {
            var ticket = await before.OpenWalkAsync("Ship the launch post");
            ticketId = ticket.Id;
            await before.AdvanceAsync(ticketId);
            await before.ResolveAsync(before.Child(await before.ReloadAsync(ticketId), 1, "draft").Id);
            var walk = await before.AdvanceAsync(ticketId);

            // Undecided on purpose: the walk is inside 'review', whose sub-ticket has not reported.
            Assert.Equal("review", walk.Open!.State);
            reviewTicketId = walk.Open.Subject!.Value;
        }

        // A second, entirely independent set of services over the same data directory — which is
        // what "the engine restarted" means for state that lives on the board.
        using var after = await Sut.AttachAsync(tmp, "walk-restart", Pipeline(), create: false);

        var resumed = await after.AdvanceAsync(ticketId);
        Assert.Equal("review", resumed.Open!.State);
        Assert.Equal(reviewTicketId, resumed.Open.Subject);           // adopted, not re-created
        Assert.Equal(1, resumed.EntryCount("draft"));                  // the completed state is not redone
        Assert.Equal(2, (await after.ReloadAsync(ticketId)).SubTickets.Count);

        // …and the resumed walk still finishes the round it was in.
        await after.PostVerdictAsync(reviewTicketId, "blog-reviewer", "SHIP");
        await after.ResolveAsync(reviewTicketId);
        var walked = await after.AdvanceAsync(ticketId);
        Assert.Equal("split", walked.Open!.State);
        Assert.Equal(1, walked.EntryCount("review"));
    }

    [Fact]
    public async Task A_second_pass_over_an_unchanged_board_writes_no_receipt_and_dispatches_nothing()
    {
        using var sut = await Sut.CreateAsync("walk-idempotent-pass", Pipeline());
        var ticket = await sut.OpenWalkAsync("Ship the launch post");

        var first = await sut.AdvanceAsync(ticket.Id);
        var second = await sut.AdvanceAsync(ticket.Id);
        var third = await sut.AdvanceAsync(ticket.Id);

        Assert.Equal(first.Steps.Count, second.Steps.Count);
        Assert.Equal(first.Steps.Count, third.Steps.Count);
        Assert.Single((await sut.ReloadAsync(ticket.Id)).SubTickets);
    }

    // ── The receipt format itself ───────────────────────────────────────────

    [Fact]
    public void A_receipt_round_trips_through_the_comment_it_is_written_as()
    {
        var step = new WorkflowWalkStep(3, WorkflowWalkEvent.Left, "verdict")
        {
            Kind = WorkflowStateKind.Gate,
            Outcome = "FIX",
            To = "draft",
            Subject = 42,
            At = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
        };

        var body = WorkflowWalk.Render(7, step);

        Assert.True(WorkflowWalk.IsWalkReceipt(body));
        Assert.True(WorkflowWalk.TryRead(body, out var read, out var error), error);
        // Field-by-field rather than record equality: the payload deserializes Branches into a
        // List, and a reference-equality mismatch on an empty collection would say nothing.
        Assert.Equal(step with { Branches = [] }, read! with { Branches = [] });
        Assert.Equal(step.Branches, read.Branches);
    }

    [Fact]
    public void A_receipt_whose_marker_contradicts_its_payload_is_refused_rather_than_half_believed()
    {
        var body = WorkflowWalk
            .Render(7, new WorkflowWalkStep(3, WorkflowWalkEvent.Left, "verdict") { To = "draft" })
            .Replace("step-3", "step-4", StringComparison.Ordinal);

        Assert.False(WorkflowWalk.TryRead(body, out _, out var error));
        Assert.Contains("step-4", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_started_receipt_opens_a_new_walk_and_a_receipt_after_a_park_belongs_to_none()
    {
        var bodies = new List<string>
        {
            WorkflowWalk.Render(7, new WorkflowWalkStep(0, WorkflowWalkEvent.Started, "draft")),
            WorkflowWalk.Render(7, new WorkflowWalkStep(1, WorkflowWalkEvent.Entered, "draft") { Role = "blog-writer" }),
            WorkflowWalk.Render(7, new WorkflowWalkStep(1, WorkflowWalkEvent.Parked, "draft") { Reason = "gate-undecidable: no arm" }),
            // Written by nothing the walker does — a stray receipt must not resurrect a parked walk.
            WorkflowWalk.Render(7, new WorkflowWalkStep(2, WorkflowWalkEvent.Entered, "review") { Role = "blog-reviewer" }),
        };

        var parked = WorkflowWalk.Replay(bodies);
        Assert.Equal(WorkflowWalkStatus.Parked, parked.Status);
        Assert.Equal(["blog-writer"], parked.VisitedRoles);

        // The owner re-runs it: a fresh `started` resets the walk, budget included.
        bodies.Add(WorkflowWalk.Render(7, new WorkflowWalkStep(0, WorkflowWalkEvent.Started, "draft")));
        var reopened = WorkflowWalk.Replay(bodies);
        Assert.Equal(WorkflowWalkStatus.Running, reopened.Status);
        Assert.Empty(reopened.Steps);
        Assert.Equal(0, reopened.EntryCount("draft"));
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One full set of the real services over one temp data directory, the idiom
    /// <c>TeamRunLifecycleTests.Sut</c> and <c>Sp3Harness</c> established.
    /// <see cref="AttachAsync"/> builds a second, independent set over the same directory — which is
    /// what a restart means for a walk whose only state is on the board.
    /// </summary>
    private sealed class Sut : IDisposable
    {
        private readonly TempDir? _owned;
        private int _automationSeq;

        private Sut(TempDir? owned, ProjectService projects, TicketService tickets, string slug, string workspace, WorkflowGraph graph)
        {
            _owned = owned;
            Projects = projects;
            Tickets = tickets;
            Slug = slug;
            Workspace = workspace;

            var members = new MemberService(projects);
            Teams = new TeamStore(projects, tickets);
            TeamRuns = new TeamRunService(Teams, tickets, members, new AgentTeamService(), NullLogger<TeamRunService>.Instance);

            var runs = new AgentRunRegistry();
            var sessions = new SessionRegistry();
            var cost = new CostTracker();
            Executor = new ActionExecutor(
                tickets, members, new LabelService(projects), sessions, runs,
                new ClaudeRunner(sessions, runs, new RunConcurrencyGate(1), NullLogger<ClaudeRunner>.Instance),
                cost, new LocalizationService(new AppSettingsService(projects.DataDir)), projects,
                new RunStateManager(runs, cost, tickets, NullLogger.Instance),
                FakeHttpClientFactory.Unused, TeamRuns, NullLogger.Instance);

            Runtime = new ProjectRuntime(slug) { Workspace = workspace, Config = new AutomationConfig(), Workflow = graph };
            Walker = TestWorkflowWalkers.For(projects, tickets, TeamRuns, Executor);
        }

        public ProjectService Projects { get; }
        public TicketService Tickets { get; }
        public TeamStore Teams { get; }
        public TeamRunService TeamRuns { get; }
        public ActionExecutor Executor { get; }
        public WorkflowWalker Walker { get; }
        public ProjectRuntime Runtime { get; }
        public string Slug { get; }
        public string Workspace { get; }

        /// <summary>Digest of the artifact every verdict in these tests claims to have reviewed.</summary>
        public string ArtifactDigest { get; private set; } = "";

        public static Task<Sut> CreateAsync(string name, WorkflowGraph graph, string[]? members = null)
        {
            var tmp = new TempDir();
            return AttachAsync(tmp, name, graph, create: true, members, owned: tmp);
        }

        public static async Task<Sut> AttachAsync(
            TempDir tmp, string name, WorkflowGraph graph, bool create, string[]? members = null, TempDir? owned = null)
        {
            var projects = new ProjectService(tmp.Path);
            var project = create
                ? await projects.CreateProjectAsync(name)
                : (await projects.ListProjectsAsync()).Single(candidate => candidate.Name == name);
            var workspace = projects.ResolveWorkspacePath(project);
            Directory.CreateDirectory(workspace);
            var memberService = new MemberService(projects);
            var tickets = new TicketService(projects, memberService);

            if (create)
            {
                var existing = (await memberService.ListMembersAsync(project.Slug))
                    .Select(member => member.Slug)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var agent in (members ?? Agents).Where(agent => !existing.Contains(agent)))
                    await memberService.CreateMemberAsync(project.Slug, agent);
            }

            var sut = new Sut(owned, projects, tickets, project.Slug, workspace, graph);

            // A verdict is only fresh while the artifact it names still hashes to its inputDigest,
            // so the reviewed file has to genuinely exist in the workspace.
            var artifact = Path.Combine(workspace, "artifact.md");
            if (!File.Exists(artifact)) await File.WriteAllTextAsync(artifact, "# The launch post\n");
            await using (var stream = File.OpenRead(artifact))
                sut.ArtifactDigest = "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));

            return sut;
        }

        public void Dispose() => _owned?.Dispose();

        // ── Driving ─────────────────────────────────────────────────────────

        /// <summary>Creates a ticket and opts it in through the ordinary <c>startWorkflow</c> action.</summary>
        public async Task<Ticket> OpenWalkAsync(string title)
        {
            var ticket = await Tickets.CreateTicketAsync(Slug, title, status: "Review");
            await OpenWalkAsync(ticket.Id);
            return ticket;
        }

        public Task OpenWalkAsync(int ticketId) => ExecuteAsync(ticketId, new StartWorkflowActionSpec());

        public async Task ExecuteAsync(int ticketId, ActionSpec action)
        {
            var ticket = await Tickets.GetTicketAsync(Slug, ticketId);
            await Executor.ExecuteAutomationAsync(
                Runtime,
                new AutomationRule
                {
                    Id = $"walk-{++_automationSeq}",
                    Trigger = new TicketInColumnTriggerSpec { Columns = ["Review"] },
                    Actions = [action],
                },
                new TriggerFiring(ticketId, ticket?.Title, ticket?.Status),
                CancellationToken.None);
        }

        public Task<WorkflowWalkState> AdvanceAsync(int ticketId) => Walker.AdvanceAsync(Runtime, ticketId);

        public async Task<Ticket> ReloadAsync(int ticketId) =>
            await Tickets.GetTicketAsync(Slug, ticketId) ?? throw new InvalidOperationException($"Ticket #{ticketId} vanished.");

        /// <summary>Reports a state's sub-ticket the way its agent's automation chain would.</summary>
        public Task ResolveAsync(int ticketId) => Tickets.MoveTicketAsync(Slug, ticketId, "Done", "automation");

        public SubTicketInfo Child(Ticket parent, int step, string state) =>
            parent.SubTickets.Single(child =>
                child.Title.StartsWith(WorkflowWalker.TicketKey(step, state), StringComparison.Ordinal));

        /// <summary>Walks entry → task → task and posts <paramref name="decision"/> at the gate.</summary>
        public async Task<WorkflowWalkState> RunToVerdictAsync(int ticketId, string decision)
        {
            var walk = await AdvanceAsync(ticketId);
            await ResolveAsync(walk.Open!.Subject!.Value);          // the draft state's sub-ticket
            walk = await AdvanceAsync(ticketId);

            var review = walk.Open!.Subject!.Value;
            await PostVerdictAsync(review, "blog-reviewer", decision);
            await ResolveAsync(review);
            return await AdvanceAsync(ticketId);
        }

        public Task PostVerdictAsync(int ticketId, string agent, string decision) =>
            Tickets.AddCommentAsync(Slug, ticketId, VerdictComment(agent, decision, ticketId, ArtifactDigest), agent);

        private static string VerdictComment(string agent, string decision, int ticketId, string digest) =>
            $$"""
            ## Review

            GIGACLAW-VERDICT v1 {{agent}} {{decision}} artifact-{{digest}}

            ```json
            {
              "schemaVersion": 1,
              "agent": "{{agent}}",
              "ticketId": {{ticketId}},
              "verdict": "{{decision}}",
              "categories": [{ "name": "Acceptance criteria", "score": {{(decision == "SHIP" ? 10 : 4)}}, "max": 10 }],
              "vetoItems": [],
              "evidence": [{ "kind": "path", "ref": "artifact.md" }],
              "reviewedAtUtc": "2026-08-01T12:00:00Z",
              "inputDigest": "{{digest}}"
            }
            ```
            """;
    }
}
