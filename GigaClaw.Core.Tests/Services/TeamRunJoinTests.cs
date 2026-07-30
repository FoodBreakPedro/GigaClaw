using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// C4 part 3: the join and the synthesizer. A run stops waiting when its policy says so, the lanes
/// it did not wait for are cancelled rather than left spending tokens, the synthesizer is handed the
/// lanes that reported <b>and</b> the ones that did not, and a run that reached a terminal state
/// stays there however many times the engine reconciles it.
/// </summary>
public sealed class TeamRunJoinTests
{
    // ── Definitions under test ──────────────────────────────────────────────

    private static TeamDefinition Team(
        string slug,
        TeamJoinPolicy policy,
        string? synthesizer = "lead",
        IReadOnlyList<TeamTaskTemplate>? graph = null) =>
        new(slug, "Parallel review", "Independent lanes then a synthesis.", "🔍")
        {
            Roles =
            [
                new TeamRole("security", "programmer"),
                new TeamRole("performance", "qa-tester"),
                new TeamRole("docs", "content-writer"),
                new TeamRole("lead", "producer")
            ],
            TaskGraph = graph ??
            [
                new TeamTaskTemplate("security-lane", "security", "Security review"),
                new TeamTaskTemplate("performance-lane", "performance", "Performance review"),
                new TeamTaskTemplate("docs-lane", "docs", "Docs review")
            ],
            JoinPolicy = policy,
            SynthesizerRole = synthesizer
        };

    private static TeamTask Task(IEnumerable<TeamTask> tasks, string key) =>
        tasks.Single(task => task.TemplateKey == key);

    /// <summary>A handoff comment the reader accepts, as the lane's agent would post it.</summary>
    private static string Handoff(string agent, int ticketId, string runId, string summary) => $$"""
        Lane finished.

        GIGACLAW-HANDOFF v1 {{agent}} ticket-{{ticketId}} run-{{runId}}

        ```json
        {
          "schemaVersion": 1,
          "agent": "{{agent}}",
          "ticketId": {{ticketId}},
          "runId": "{{runId}}",
          "summary": "{{summary}}",
          "outputs": [{ "kind": "path", "ref": "doc/finding.md", "note": "what this lane found" }],
          "ownedFiles": ["doc/finding.md"],
          "producedAtUtc": "2026-07-30T12:00:00Z"
        }
        ```
        """;

    /// <summary>Finishes a lane the way an agent does: post the handoff, move the ticket to Done.</summary>
    private static async Task FinishLaneAsync(Sut sut, TeamTask task, string runId, string summary)
    {
        await sut.Tickets.AddCommentAsync(
            sut.Slug, task.TicketId, Handoff(task.AgentSlug, task.TicketId, runId, summary), task.AgentSlug);
        await sut.Tickets.MoveTicketAsync(sut.Slug, task.TicketId, "Done");
    }

    private static async Task<Ticket> SynthesisTicketAsync(Sut sut, TeamRun run)
    {
        var refreshed = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.NotNull(refreshed!.SynthesisTicketId);
        return (await sut.Tickets.GetTicketAsync(sut.Slug, refreshed.SynthesisTicketId!.Value))!;
    }

    // ── All-done ────────────────────────────────────────────────────────────

    [Fact]
    public async Task All_done_join_hands_every_lane_handoff_to_the_synthesizer_and_completes()
    {
        using var sut = await Sut.CreateAsync("join-all-done", Team("all-done", TeamJoinPolicy.AllDone));
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var run = await sut.Runs.StartRunAsync(sut.Slug, "all-done", parent.Id);
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);

        // Two lanes in: the join must not fire while one is still open.
        await FinishLaneAsync(sut, Task(tasks, "security-lane"), "sec1", "No injection paths left.");
        await FinishLaneAsync(sut, Task(tasks, "performance-lane"), "perf1", "Two hot loops fixed.");
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Running, (await sut.Teams.GetRunAsync(sut.Slug, run.Id))!.Status);

        await FinishLaneAsync(sut, Task(tasks, "docs-lane"), "docs1", "Docs match the new flags.");
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);

        var joined = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Joining, joined!.Status);

        // Each lane's handoff is recorded on its task, pointing at the authoritative comment.
        var after = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        Assert.All(after, task => Assert.Equal(TeamTaskStatus.Done, task.Status));
        Assert.Equal(
            $"ticket-{Task(after, "security-lane").TicketId}/run-sec1",
            Task(after, "security-lane").ResultHandoffRef);

        var synthesis = await SynthesisTicketAsync(sut, run);
        Assert.Equal("producer", synthesis.AssignedTo);
        Assert.Equal(parent.Id, synthesis.ParentId);
        // Born in the dispatch column: the board is what actually starts the synthesizer.
        Assert.Equal(TeamRunService.ReadyStatus, synthesis.Status);
        Assert.Contains("Lanes that reported (3 of 3)", synthesis.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("Lanes missing", synthesis.Description, StringComparison.Ordinal);
        // The brief is the ordinary handoff rendering, not a second summary format.
        Assert.Contains("Handoff from programmer", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("Two hot loops fixed.", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("doc/finding.md", synthesis.Description, StringComparison.Ordinal);

        // The run finishes when the synthesizer's own sub-ticket resolves.
        await sut.Tickets.MoveTicketAsync(sut.Slug, synthesis.Id, "Done");
        await sut.Runs.ReconcileProjectAsync(sut.Slug);

        var completed = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Completed, completed!.Status);
        Assert.Null(completed.FailureReason);
        Assert.NotNull(completed.CompletedAt);
    }

    [Fact]
    public async Task A_team_without_a_synthesizer_completes_at_the_join_itself()
    {
        using var sut = await Sut.CreateAsync(
            "join-no-synth", Team("no-synth", TeamJoinPolicy.AllDone, synthesizer: null));
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var run = await sut.Runs.StartRunAsync(sut.Slug, "no-synth", parent.Id);

        foreach (var task in await sut.Teams.ListTasksAsync(sut.Slug, run.Id))
            await sut.Tickets.MoveTicketAsync(sut.Slug, task.TicketId, "Done");
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);

        var completed = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Completed, completed!.Status);
        Assert.Null(completed.SynthesisTicketId);
        // Three lanes and nothing else: no synthesizer means no extra sub-ticket.
        Assert.Equal(3, (await sut.Tickets.ListTicketsAsync(sut.Slug, parentId: parent.Id)).Count);
    }

    // ── Quorum ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Quorum_fires_early_and_cancels_the_lane_it_did_not_wait_for()
    {
        using var sut = await Sut.CreateAsync("join-quorum", Team("quorum", TeamJoinPolicy.OfQuorum(2)));
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var run = await sut.Runs.StartRunAsync(sut.Slug, "quorum", parent.Id);
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        var straggler = Task(tasks, "docs-lane");

        await FinishLaneAsync(sut, Task(tasks, "security-lane"), "sec1", "Clean.");
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Running, (await sut.Teams.GetRunAsync(sut.Slug, run.Id))!.Status);

        await FinishLaneAsync(sut, Task(tasks, "performance-lane"), "perf1", "Clean.");
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);

        Assert.Equal(TeamRunStatus.Joining, (await sut.Teams.GetRunAsync(sut.Slug, run.Id))!.Status);

        // The lane the join did not wait for is cancelled and parked out of the dispatch column, so
        // the next engine tick cannot start an agent on an answer nobody will read.
        var cancelled = await sut.Teams.GetTaskByTicketAsync(sut.Slug, straggler.TicketId);
        Assert.Equal(TeamTaskStatus.Cancelled, cancelled!.Status);
        Assert.Contains("the join fired", cancelled.FailureReason!, StringComparison.Ordinal);
        Assert.Equal(
            TeamRunService.ParkedStatus,
            (await sut.Tickets.GetTicketAsync(sut.Slug, straggler.TicketId))!.Status);

        // The synthesizer is told the third lane is missing — and told why.
        var synthesis = await SynthesisTicketAsync(sut, run);
        Assert.Contains("Lanes that reported (2 of 3)", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("Lanes missing (1 of 3)", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("docs-lane — content-writer", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("cancelled — the join fired", synthesis.Description, StringComparison.Ordinal);

        // Quorum met is a success even though a lane is missing; the gap is still on the receipt.
        await sut.Tickets.MoveTicketAsync(sut.Slug, synthesis.Id, "Done");
        await sut.Runs.ReconcileProjectAsync(sut.Slug);
        var completed = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Completed, completed!.Status);
        Assert.Contains(
            (await sut.Tickets.GetTicketAsync(sut.Slug, parent.Id))!.Activities,
            entry => entry.Text.Contains("completed with gaps", StringComparison.Ordinal)
                && entry.Text.Contains("docs-lane", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Quorum_that_can_no_longer_be_reached_fails_instead_of_waiting()
    {
        using var sut = await Sut.CreateAsync("join-quorum-lost", Team("quorum-lost", TeamJoinPolicy.OfQuorum(3)));
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var run = await sut.Runs.StartRunAsync(sut.Slug, "quorum-lost", parent.Id);
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);

        await FinishLaneAsync(sut, Task(tasks, "security-lane"), "sec1", "Clean.");
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);
        await sut.Runs.FailTaskAsync(sut.Slug, Task(tasks, "docs-lane").TicketId, "Agent exited 1.");

        // All three were needed and one is gone: waiting on the last lane would only delay the news.
        var joined = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Joining, joined!.Status);
        Assert.Equal(
            TeamTaskStatus.Cancelled,
            (await sut.Teams.GetTaskByTicketAsync(sut.Slug, Task(tasks, "performance-lane").TicketId))!.Status);

        await sut.Tickets.MoveTicketAsync(sut.Slug, (await SynthesisTicketAsync(sut, run)).Id, "Done");
        await sut.Runs.ReconcileProjectAsync(sut.Slug);

        var failed = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Failed, failed!.Status);
        Assert.Contains("docs-lane", failed.FailureReason!, StringComparison.Ordinal);
    }

    // ── First failure ───────────────────────────────────────────────────────

    [Fact]
    public async Task First_failure_stops_the_run_the_moment_a_lane_fails()
    {
        using var sut = await Sut.CreateAsync("join-first-failure", Team("first-failure", TeamJoinPolicy.FirstFailure));
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var run = await sut.Runs.StartRunAsync(sut.Slug, "first-failure", parent.Id);
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);

        await FinishLaneAsync(sut, Task(tasks, "security-lane"), "sec1", "Clean.");
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);

        // Reporting the failure joins the run in the same call — no extra tick in between.
        await sut.Runs.FailTaskAsync(sut.Slug, Task(tasks, "performance-lane").TicketId, "Benchmark harness crashed.");

        Assert.Equal(TeamRunStatus.Joining, (await sut.Teams.GetRunAsync(sut.Slug, run.Id))!.Status);
        Assert.Equal(
            TeamTaskStatus.Cancelled,
            (await sut.Teams.GetTaskByTicketAsync(sut.Slug, Task(tasks, "docs-lane").TicketId))!.Status);
        // The failed lane is parked too: a failed sub-ticket left in Todo is re-dispatched.
        Assert.Equal(
            TeamRunService.ParkedStatus,
            (await sut.Tickets.GetTicketAsync(sut.Slug, Task(tasks, "performance-lane").TicketId))!.Status);

        var synthesis = await SynthesisTicketAsync(sut, run);
        Assert.Contains("Join: FirstFailure", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("performance-lane — qa-tester", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("failed — Benchmark harness crashed.", synthesis.Description, StringComparison.Ordinal);

        await sut.Tickets.MoveTicketAsync(sut.Slug, synthesis.Id, "Done");
        await sut.Runs.ReconcileProjectAsync(sut.Slug);

        var failed = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Failed, failed!.Status);
        Assert.Contains("performance-lane", failed.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("Benchmark harness crashed.", failed.FailureReason!, StringComparison.Ordinal);
    }

    // ── Partial failure is an outcome, not an error ─────────────────────────

    [Fact]
    public async Task Synthesize_with_gaps_names_the_missing_lane_and_still_fails_the_run()
    {
        using var sut = await Sut.CreateAsync("join-with-gaps", Team("with-gaps", TeamJoinPolicy.AllDone));
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var run = await sut.Runs.StartRunAsync(sut.Slug, "with-gaps", parent.Id);
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);

        await FinishLaneAsync(sut, Task(tasks, "security-lane"), "sec1", "No injection paths left.");
        await FinishLaneAsync(sut, Task(tasks, "docs-lane"), "docs1", "Docs match the new flags.");
        // All-done waits for everyone, so the failed lane does not short-circuit it — but it is
        // still the thing the synthesis must not pretend to cover.
        await sut.Runs.FailTaskAsync(sut.Slug, Task(tasks, "performance-lane").TicketId, "Agent exited 1.");

        var synthesis = await SynthesisTicketAsync(sut, run);
        Assert.Contains("Lanes that reported (2 of 3)", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("Lanes missing (1 of 3)", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains(
            "performance-lane — qa-tester (ticket #", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("failed — Agent exited 1.", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("Do not present their subject matter as covered", synthesis.Description, StringComparison.Ordinal);

        await sut.Tickets.MoveTicketAsync(sut.Slug, synthesis.Id, "Done");
        await sut.Runs.ReconcileProjectAsync(sut.Slug);

        // A synthesis that covered two of three lanes is not a green run — the receipt says so.
        var failed = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Failed, failed!.Status);
        Assert.Contains("performance-lane", failed.FailureReason!, StringComparison.Ordinal);
        Assert.Contains(
            (await sut.Tickets.GetTicketAsync(sut.Slug, parent.Id))!.Activities,
            entry => entry.Text.Contains("failed", StringComparison.Ordinal)
                && entry.Text.Contains("Agent exited 1.", StringComparison.Ordinal));
    }

    // ── Terminal is terminal ────────────────────────────────────────────────

    [Fact]
    public async Task Reconciling_twice_never_dispatches_the_synthesizer_twice()
    {
        using var sut = await Sut.CreateAsync("join-idempotent", Team("idempotent", TeamJoinPolicy.AllDone));
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var run = await sut.Runs.StartRunAsync(sut.Slug, "idempotent", parent.Id);
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        foreach (var task in tasks)
            await FinishLaneAsync(sut, task, $"run-{task.TemplateKey}", "Clean.");

        // Two reconciles back to back — the second is what an engine tick looks like.
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);
        await sut.Runs.ReconcileProjectAsync(sut.Slug);

        var joining = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Joining, joining!.Status);
        // Three lanes plus exactly one synthesis sub-ticket.
        var children = await sut.Tickets.ListTicketsAsync(sut.Slug, parentId: parent.Id);
        Assert.Equal(4, children.Count);
        Assert.Single(children, ticket => ticket.Id == joining.SynthesisTicketId);

        await sut.Tickets.MoveTicketAsync(sut.Slug, joining.SynthesisTicketId!.Value, "Done");
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);
        var completed = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Completed, completed!.Status);

        // …and a completed run cannot be reopened, re-synthesized or re-failed.
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);
        await sut.Runs.ReconcileProjectAsync(sut.Slug);
        var again = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Completed, again!.Status);
        Assert.Equal(completed.CompletedAt, again.CompletedAt);
        Assert.Equal(4, (await sut.Tickets.ListTicketsAsync(sut.Slug, parentId: parent.Id)).Count);

        // A late failure report against a closed run is ignored rather than rewriting history.
        var lane = Task(tasks, "security-lane");
        Assert.Equal(
            TeamTaskStatus.Done,
            (await sut.Runs.FailTaskAsync(sut.Slug, lane.TicketId, "too late"))!.Status);
        Assert.Equal(TeamRunStatus.Completed, (await sut.Teams.GetRunAsync(sut.Slug, run.Id))!.Status);
        Assert.Equal(TeamRunStatus.Completed, (await sut.Runs.CancelRunAsync(sut.Slug, run.Id, "too late"))!.Status);
    }

    [Fact]
    public async Task A_run_joined_before_a_restart_is_finished_by_the_new_process()
    {
        using var tmp = new TempDir();
        var definition = Team("resume-join", TeamJoinPolicy.AllDone);
        long runId;
        int synthesisTicketId;

        {
            var sut = await Sut.AttachAsync(tmp, "join-resume", definition, create: true);
            var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
            var run = await sut.Runs.StartRunAsync(sut.Slug, definition.Slug, parent.Id);
            foreach (var task in await sut.Teams.ListTasksAsync(sut.Slug, run.Id))
                await FinishLaneAsync(sut, task, $"run-{task.TemplateKey}", "Clean.");
            await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);

            runId = run.Id;
            synthesisTicketId = (await sut.Teams.GetRunAsync(sut.Slug, run.Id))!.SynthesisTicketId!.Value;
            await sut.Tickets.MoveTicketAsync(sut.Slug, synthesisTicketId, "Done");
        }

        // Brand-new services over the same directory: the Joining state and the synthesis ticket id
        // are rows, so the join survives losing every object that produced it.
        var resumed = await Sut.AttachAsync(tmp, "join-resume", definition, create: false);
        Assert.Equal(TeamRunStatus.Joining, (await resumed.Teams.GetRunAsync(resumed.Slug, runId))!.Status);

        await resumed.Runs.ReconcileProjectAsync(resumed.Slug);

        var completed = await resumed.Teams.GetRunAsync(resumed.Slug, runId);
        Assert.Equal(TeamRunStatus.Completed, completed!.Status);
        Assert.Equal(synthesisTicketId, completed.SynthesisTicketId);
    }

    [Fact]
    public async Task A_synthesizer_who_is_not_a_project_member_is_refused_before_any_lane_runs()
    {
        var definition = Team("ghost-synth", TeamJoinPolicy.AllDone) with { Slug = "ghost-synth" };
        definition = definition with
        {
            Roles = [.. definition.Roles.Select(role =>
                role.RoleId == "lead" ? role with { AgentSlug = "nobody-here" } : role)]
        };
        using var sut = await Sut.CreateAsync("join-ghost-synth", definition, addMembers: false);
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");

        var exception = await Assert.ThrowsAsync<TeamStoreException>(
            () => sut.Runs.StartRunAsync(sut.Slug, "ghost-synth", parent.Id));

        Assert.Equal("team_member_missing", exception.Code);
        Assert.Empty(await sut.Teams.ListRunsAsync(sut.Slug, parent.Id));
        Assert.Empty(await sut.Tickets.ListTicketsAsync(sut.Slug, parentId: parent.Id));
    }

    // ── The pure verdict ────────────────────────────────────────────────────

    [Fact]
    public void The_join_verdict_is_a_pure_function_of_the_policy_and_the_rows()
    {
        var done = Row(1, TeamTaskStatus.Done);
        var open = Row(2, TeamTaskStatus.Dispatched);
        var failed = Row(3, TeamTaskStatus.Failed);

        // A run that has not fanned out yet has nothing to join — an empty list is not "all done".
        Assert.False(TeamJoinEvaluator.Evaluate(TeamJoinPolicy.AllDone, []).Fires);

        Assert.False(TeamJoinEvaluator.Evaluate(TeamJoinPolicy.AllDone, [done, open]).Fires);
        Assert.False(TeamJoinEvaluator.Evaluate(TeamJoinPolicy.AllDone, [done, failed]).Success);
        Assert.True(TeamJoinEvaluator.Evaluate(TeamJoinPolicy.AllDone, [done, failed]).Fires);
        Assert.True(TeamJoinEvaluator.Evaluate(TeamJoinPolicy.AllDone, [done]).Success);

        // First-failure does not wait for the open lane once one has failed.
        var firstFailure = TeamJoinEvaluator.Evaluate(TeamJoinPolicy.FirstFailure, [open, failed]);
        Assert.True(firstFailure.Fires);
        Assert.False(firstFailure.Success);
        Assert.Equal([open], firstFailure.StillOpen);
        Assert.Equal(2, firstFailure.Missing.Count);
        // …and behaves like all-done while nothing has failed.
        Assert.False(TeamJoinEvaluator.Evaluate(TeamJoinPolicy.FirstFailure, [done, open]).Fires);

        // Quorum: reached, still reachable, and out of reach.
        Assert.True(TeamJoinEvaluator.Evaluate(TeamJoinPolicy.OfQuorum(1), [done, open]).Success);
        Assert.False(TeamJoinEvaluator.Evaluate(TeamJoinPolicy.OfQuorum(2), [done, open]).Fires);
        var lost = TeamJoinEvaluator.Evaluate(TeamJoinPolicy.OfQuorum(2), [done, failed]);
        Assert.True(lost.Fires);
        Assert.False(lost.Success);
        Assert.Contains("out of reach", lost.Reason, StringComparison.Ordinal);

        // Success is recomputed from the rows the same way at finalize time.
        Assert.True(TeamJoinEvaluator.Succeeded(TeamJoinPolicy.OfQuorum(1), [done, failed]));
        Assert.False(TeamJoinEvaluator.Succeeded(TeamJoinPolicy.AllDone, [done, failed]));

        static TeamTask Row(long id, TeamTaskStatus status) => new(
            id, 1, $"lane-{id}", "role", "agent", (int)id, status, DateTime.UtcNow, DateTime.UtcNow);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One full set of services over a data directory, parameterized by the definition under test.
    /// <see cref="AttachAsync"/> builds a second, independent set over the same directory — which is
    /// what "the engine restarted" means for a feature whose state lives in the project database.
    /// </summary>
    private sealed class Sut : IDisposable
    {
        private readonly TempDir? _owned;

        private Sut(TempDir? owned, ProjectService projects, TicketService tickets, string slug)
        {
            _owned = owned;
            Projects = projects;
            Tickets = tickets;
            Slug = slug;
            Teams = new TeamStore(projects, tickets);
            Runs = new TeamRunService(
                Teams, tickets, new MemberService(projects), new AgentTeamService(),
                NullLogger<TeamRunService>.Instance);
        }

        public ProjectService Projects { get; }
        public TicketService Tickets { get; }
        public TeamStore Teams { get; }
        public TeamRunService Runs { get; }
        public string Slug { get; }

        public static Task<Sut> CreateAsync(string name, TeamDefinition definition, bool addMembers = true)
        {
            var tmp = new TempDir();
            return AttachAsync(tmp, name, definition, create: true, owned: tmp, addMembers: addMembers);
        }

        public static async Task<Sut> AttachAsync(
            TempDir tmp,
            string name,
            TeamDefinition definition,
            bool create,
            TempDir? owned = null,
            bool addMembers = true)
        {
            var projects = new ProjectService(tmp.Path);
            var project = create
                ? await projects.CreateProjectAsync(name)
                : (await projects.ListProjectsAsync()).Single(p => p.Name == name);
            var members = new MemberService(projects);
            var tickets = new TicketService(projects, members);
            var sut = new Sut(owned, projects, tickets, project.Slug);
            if (create && addMembers)
            {
                var existing = (await members.ListMembersAsync(project.Slug))
                    .Select(member => member.Slug)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var agentSlug in definition.AgentSlugs.Where(slug => !existing.Contains(slug)))
                    await members.CreateMemberAsync(project.Slug, agentSlug);
            }
            await sut.Teams.SaveDefinitionAsync(project.Slug, definition);
            return sut;
        }

        public void Dispose() => _owned?.Dispose();
    }
}
