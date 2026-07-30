using System.Text.Json;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// C4 part 2: the run lifecycle. Fan-out maps a definition's task graph onto sub-tickets, ordering
/// is the board's own dependency edges read through the <c>dependenciesResolved</c> evaluator,
/// cancellation propagates to every open task, and a run survives losing every service instance —
/// because none of its state ever lived in one.
/// <para>
/// The join policy and the synthesizer are part 3, covered by <see cref="TeamRunJoinTests"/>.
/// </para>
/// </summary>
public sealed class TeamRunLifecycleTests
{
    private const string TeamSlug = "parallel-review";

    /// <summary>Two independent lanes plus a dedup task that waits for both.</summary>
    private static TeamDefinition ReviewTeam() =>
        new(TeamSlug, "Parallel review", "Two lanes then a synthesis.", "🔍")
        {
            Roles =
            [
                new TeamRole("security", "programmer"),
                new TeamRole("performance", "qa-tester"),
                new TeamRole("lead", "producer")
            ],
            TaskGraph =
            [
                // Declared out of dependency order on purpose: fan-out must sort the graph itself,
                // because an edge can only be drawn once the sibling it points at exists.
                new TeamTaskTemplate("dedup", "lead", "Deduplicate findings")
                {
                    DependsOn = ["security-lane", "performance-lane"]
                },
                new TeamTaskTemplate("security-lane", "security", "Security review") { Prompt = "Audit the diff." },
                new TeamTaskTemplate("performance-lane", "performance", "Performance review")
            ],
            SynthesizerRole = "lead"
        };

    private static TeamTask Task(IEnumerable<TeamTask> tasks, string key) =>
        tasks.Single(task => task.TemplateKey == key);

    // ── Fan-out ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fan_out_turns_every_template_into_a_sub_ticket_of_the_parent()
    {
        using var sut = await Sut.CreateAsync("team-fanout");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");

        var run = await sut.Runs.StartRunAsync(sut.Slug, TeamSlug, parent.Id);

        Assert.Equal(TeamRunStatus.Running, run.Status);
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        Assert.Equal(3, tasks.Count);

        var security = Task(tasks, "security-lane");
        var securityTicket = await sut.Tickets.GetTicketAsync(sut.Slug, security.TicketId);
        Assert.Equal("Security review", securityTicket!.Title);
        Assert.Equal("Audit the diff.", securityTicket.Description);
        Assert.Equal("programmer", securityTicket.AssignedTo);
        Assert.Equal(parent.Id, securityTicket.ParentId);
        // Nothing blocks it, so it is born in the dispatch column the agent automations watch.
        Assert.Equal(TeamRunService.ReadyStatus, securityTicket.Status);
        Assert.Equal(TeamTaskStatus.Dispatched, security.Status);

        var dedup = Task(tasks, "dedup");
        var dedupTicket = await sut.Tickets.GetTicketAsync(sut.Slug, dedup.TicketId);
        Assert.Equal("producer", dedupTicket!.AssignedTo);
        Assert.Equal(TeamRunService.HoldStatus, dedupTicket.Status);
        Assert.Equal(TeamTaskStatus.Pending, dedup.Status);

        // The blocking truth is the ordinary edge table, not the template's DependsOn.
        Assert.Equal(["security-lane", "performance-lane"], dedup.DependsOn);
        Assert.Equal(
            new[] { security.TicketId, Task(tasks, "performance-lane").TicketId }.OrderBy(id => id),
            dedup.BlockedByTicketIds.OrderBy(id => id));
    }

    [Fact]
    public async Task Starting_the_same_team_twice_on_a_ticket_re_attaches_instead_of_fanning_out_again()
    {
        using var sut = await Sut.CreateAsync("team-idempotent");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");

        var first = await sut.Runs.StartRunAsync(sut.Slug, TeamSlug, parent.Id);
        var second = await sut.Runs.StartRunAsync(sut.Slug, TeamSlug, parent.Id);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(3, (await sut.Teams.ListTasksAsync(sut.Slug, first.Id)).Count);
        Assert.Single(await sut.Teams.ListRunsAsync(sut.Slug, parent.Id));
        Assert.Equal(3, (await sut.Tickets.ListTicketsAsync(sut.Slug, parentId: parent.Id)).Count);
    }

    [Fact]
    public async Task A_filter_only_team_has_nothing_to_run()
    {
        using var sut = await Sut.CreateAsync("team-filter-only");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Groom the backlog");

        var exception = await Assert.ThrowsAsync<TeamStoreException>(
            () => sut.Runs.StartRunAsync(sut.Slug, AgentTeamService.SoftwareEngineeringSlug, parent.Id));
        Assert.Equal("definition_not_executable", exception.Code);

        var unknown = await Assert.ThrowsAsync<TeamStoreException>(
            () => sut.Runs.StartRunAsync(sut.Slug, "no-such-team", parent.Id));
        Assert.Equal("team_not_found", unknown.Code);
    }

    [Fact]
    public async Task A_role_bound_to_a_non_member_is_refused_before_anything_is_created()
    {
        using var sut = await Sut.CreateAsync("team-missing-member");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var definition = ReviewTeam() with { Slug = "ghost-team" };
        await sut.Teams.SaveDefinitionAsync(sut.Slug, definition with
        {
            Roles = [.. definition.Roles.Select(role =>
                role.RoleId == "lead" ? role with { AgentSlug = "nobody-here" } : role)]
        });

        var exception = await Assert.ThrowsAsync<TeamStoreException>(
            () => sut.Runs.StartRunAsync(sut.Slug, "ghost-team", parent.Id));

        Assert.Equal("team_member_missing", exception.Code);
        // Nothing half-built: no run row, and no orphan sub-ticket under the parent.
        Assert.Empty(await sut.Teams.ListRunsAsync(sut.Slug, parent.Id));
        Assert.Empty(await sut.Tickets.ListTicketsAsync(sut.Slug, parentId: parent.Id));
    }

    // ── Dispatch ordering ───────────────────────────────────────────────────

    [Fact]
    public async Task A_task_is_released_only_once_every_blocker_is_resolved()
    {
        using var sut = await Sut.CreateAsync("team-ordering");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var run = await sut.Runs.StartRunAsync(sut.Slug, TeamSlug, parent.Id);
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        var dedup = Task(tasks, "dedup");

        // One lane done is not enough: the dedup task waits on both.
        await sut.Tickets.MoveTicketAsync(sut.Slug, Task(tasks, "security-lane").TicketId, "Done");
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);

        Assert.Equal(TeamTaskStatus.Done, (await sut.Teams.GetTaskByTicketAsync(sut.Slug, Task(tasks, "security-lane").TicketId))!.Status);
        var held = await sut.Tickets.GetTicketAsync(sut.Slug, dedup.TicketId);
        Assert.Equal(TeamRunService.HoldStatus, held!.Status);
        Assert.Equal(TeamTaskStatus.Pending, (await sut.Teams.GetTaskByTicketAsync(sut.Slug, dedup.TicketId))!.Status);

        await sut.Tickets.MoveTicketAsync(sut.Slug, Task(tasks, "performance-lane").TicketId, "Done");
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);

        var released = await sut.Tickets.GetTicketAsync(sut.Slug, dedup.TicketId);
        Assert.Equal(TeamRunService.ReadyStatus, released!.Status);
        Assert.Equal(TeamTaskStatus.Dispatched, (await sut.Teams.GetTaskByTicketAsync(sut.Slug, dedup.TicketId))!.Status);

        // The dedup task was only just released, so the all-done join has something left to wait on.
        Assert.Equal(TeamRunStatus.Running, (await sut.Teams.GetRunAsync(sut.Slug, run.Id))!.Status);
    }

    [Fact]
    public async Task Dropping_the_edge_on_the_board_unblocks_the_task()
    {
        using var sut = await Sut.CreateAsync("team-edge-removed");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var run = await sut.Runs.StartRunAsync(sut.Slug, TeamSlug, parent.Id);
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        var dedup = Task(tasks, "dedup");

        // The run reads the live edges, so a human editing dependencies really does change what a
        // task waits on — there is no second copy of the graph to disagree with the board.
        foreach (var blocker in dedup.BlockedByTicketIds)
            await sut.Tickets.RemoveTicketDependencyAsync(sut.Slug, dedup.TicketId, blocker);

        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);

        Assert.Equal(
            TeamRunService.ReadyStatus,
            (await sut.Tickets.GetTicketAsync(sut.Slug, dedup.TicketId))!.Status);
    }

    // ── Cancellation ────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_a_run_cancels_every_open_task_and_leaves_the_finished_ones_alone()
    {
        using var sut = await Sut.CreateAsync("team-cancel");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var run = await sut.Runs.StartRunAsync(sut.Slug, TeamSlug, parent.Id);
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        var finished = Task(tasks, "security-lane");
        await sut.Tickets.MoveTicketAsync(sut.Slug, finished.TicketId, "Done");
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);

        var cancelled = await sut.Runs.CancelRunAsync(sut.Slug, run.Id, "Owner pulled the release.");

        Assert.Equal(TeamRunStatus.Cancelled, cancelled!.Status);
        Assert.Equal("Owner pulled the release.", cancelled.FailureReason);

        var after = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        Assert.Equal(TeamTaskStatus.Done, Task(after, "security-lane").Status);
        Assert.Equal("Done", (await sut.Tickets.GetTicketAsync(sut.Slug, finished.TicketId))!.Status);

        foreach (var key in new[] { "performance-lane", "dedup" })
        {
            var task = Task(after, key);
            Assert.Equal(TeamTaskStatus.Cancelled, task.Status);
            // Parked out of the dispatch column: the board is what starts agents, so a cancelled
            // task left in Todo would be picked up on the very next tick regardless of its row.
            Assert.Equal(
                TeamRunService.ParkedStatus,
                (await sut.Tickets.GetTicketAsync(sut.Slug, task.TicketId))!.Status);
        }
    }

    [Fact]
    public async Task Closing_the_parent_cancels_the_run_and_terminal_states_stay_terminal()
    {
        using var sut = await Sut.CreateAsync("team-parent-closed");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var run = await sut.Runs.StartRunAsync(sut.Slug, TeamSlug, parent.Id);

        await sut.Tickets.MoveTicketAsync(sut.Slug, parent.Id, "Done");
        await sut.Runs.ReconcileProjectAsync(sut.Slug);

        var closed = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Cancelled, closed!.Status);
        Assert.All(await sut.Teams.ListTasksAsync(sut.Slug, run.Id), task => Assert.False(task.IsOpen));

        // A cancelled run is final: reconciling it again, or cancelling it a second time with a
        // different reason, must not revive it or rewrite why it ended.
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);
        var recancelled = await sut.Runs.CancelRunAsync(sut.Slug, run.Id, "something else");
        Assert.Equal(TeamRunStatus.Cancelled, recancelled!.Status);
        Assert.Equal(closed.FailureReason, recancelled.FailureReason);
        Assert.Equal(closed.CompletedAt, recancelled.CompletedAt);

        // And no cancelled sub-ticket drifted back into the dispatch column.
        foreach (var task in await sut.Teams.ListTasksAsync(sut.Slug, run.Id))
            Assert.NotEqual(
                TeamRunService.ReadyStatus,
                (await sut.Tickets.GetTicketAsync(sut.Slug, task.TicketId))!.Status);
    }

    // ── Resumability ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_run_interrupted_by_a_restart_continues_from_the_board()
    {
        using var tmp = new TempDir();
        int parentId, dedupTicketId, securityTicketId, performanceTicketId;
        long runId;

        // ── first "process": start the run and finish one lane, then drop every service.
        {
            var sut = await Sut.AttachAsync(tmp, "team-resume", create: true);
            var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
            var run = await sut.Runs.StartRunAsync(sut.Slug, TeamSlug, parent.Id);
            var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);

            parentId = parent.Id;
            runId = run.Id;
            securityTicketId = Task(tasks, "security-lane").TicketId;
            performanceTicketId = Task(tasks, "performance-lane").TicketId;
            dedupTicketId = Task(tasks, "dedup").TicketId;

            await sut.Tickets.MoveTicketAsync(sut.Slug, securityTicketId, "Done");
        }

        // ── restart: brand-new ProjectService/TicketService/TeamStore/TeamRunService over the same
        // data directory. Nothing is handed across the boundary but the project slug.
        var resumed = await Sut.AttachAsync(tmp, "team-resume", create: false);

        var open = Assert.Single(await resumed.Teams.ListRunsAsync(resumed.Slug, openOnly: true));
        Assert.Equal(runId, open.Id);
        Assert.Equal(parentId, open.ParentTicketId);
        // The definition came back from the run's own snapshot, not from any live registry.
        Assert.Equal(3, open.Definition.TaskGraph.Count);

        await resumed.Runs.ReconcileProjectAsync(resumed.Slug);

        // The lane finished before the restart is recorded, and the dedup task is still held.
        Assert.Equal(TeamTaskStatus.Done, (await resumed.Teams.GetTaskByTicketAsync(resumed.Slug, securityTicketId))!.Status);
        Assert.Equal(TeamRunService.HoldStatus, (await resumed.Tickets.GetTicketAsync(resumed.Slug, dedupTicketId))!.Status);

        // …and the run keeps going: finishing the second lane releases the dedup task as usual.
        await resumed.Tickets.MoveTicketAsync(resumed.Slug, performanceTicketId, "Done");
        await resumed.Runs.ReconcileProjectAsync(resumed.Slug);

        Assert.Equal(TeamRunService.ReadyStatus, (await resumed.Tickets.GetTicketAsync(resumed.Slug, dedupTicketId))!.Status);
        Assert.Equal(TeamTaskStatus.Dispatched, (await resumed.Teams.GetTaskByTicketAsync(resumed.Slug, dedupTicketId))!.Status);
    }

    [Fact]
    public async Task A_fan_out_interrupted_halfway_is_finished_on_the_next_reconcile()
    {
        using var sut = await Sut.CreateAsync("team-partial-fanout");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");

        // Stands in for a process killed between two sub-tickets: the run row exists, the graph
        // does not. Nothing else knows about the run, so only the board can complete it.
        var run = await sut.Teams.CreateRunAsync(sut.Slug, ReviewTeam(), parent.Id);
        Assert.Empty(await sut.Teams.ListTasksAsync(sut.Slug, run.Id));

        await sut.Runs.ReconcileProjectAsync(sut.Slug);

        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);
        Assert.Equal(3, tasks.Count);
        Assert.Equal(TeamRunStatus.Running, (await sut.Teams.GetRunAsync(sut.Slug, run.Id))!.Status);
    }

    // ── The automation action ───────────────────────────────────────────────

    [Fact]
    public void The_action_round_trips_through_the_automation_config_serializer()
    {
        var json = """
        {
          "automations": [{
            "id": "review-team",
            "trigger": { "type": "ticketInColumn", "columns": ["Review"] },
            "actions": [{ "type": "startTeamRun", "team": "parallel-review" }]
          }]
        }
        """;

        var config = JsonSerializer.Deserialize<AutomationConfig>(json, AutomationStore.JsonOptions)!;
        var action = Assert.IsType<StartTeamRunActionSpec>(Assert.Single(config.Automations[0].Actions));
        Assert.Equal("parallel-review", action.Team);

        var reloaded = JsonSerializer.Deserialize<AutomationConfig>(
            JsonSerializer.Serialize(config, AutomationStore.JsonOptions), AutomationStore.JsonOptions)!;
        Assert.Equal(
            "parallel-review",
            Assert.IsType<StartTeamRunActionSpec>(Assert.Single(reloaded.Automations[0].Actions)).Team);
    }

    [Fact]
    public async Task The_action_starts_the_run_from_the_firing_ticket()
    {
        using var sut = await Sut.CreateAsync("team-action");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var runtime = new ProjectRuntime(sut.Slug)
        {
            Workspace = sut.Projects.ResolveWorkspacePath((await sut.Projects.GetProjectAsync(sut.Slug))!),
            Config = new AutomationConfig(),
        };
        var automation = new AutomationRule
        {
            Id = "review-team",
            Trigger = new TicketInColumnTriggerSpec { Columns = ["Review"] },
            Actions = [new StartTeamRunActionSpec { Team = TeamSlug }],
        };

        await sut.Executor.ExecuteAutomationAsync(
            runtime, automation, new TriggerFiring(parent.Id, parent.Title, "Review"), CancellationToken.None);

        var run = Assert.Single(await sut.Teams.ListRunsAsync(sut.Slug, parent.Id));
        Assert.Equal(TeamRunStatus.Running, run.Status);
        Assert.Equal(3, (await sut.Teams.ListTasksAsync(sut.Slug, run.Id)).Count);

        // Firing again while the run is open must not fan out a second set of sub-tickets.
        await sut.Executor.ExecuteAutomationAsync(
            runtime, automation, new TriggerFiring(parent.Id, parent.Title, "Review"), CancellationToken.None);
        Assert.Single(await sut.Teams.ListRunsAsync(sut.Slug, parent.Id));
        Assert.Equal(3, (await sut.Tickets.ListTicketsAsync(sut.Slug, parentId: parent.Id)).Count);
    }

    [Fact]
    public async Task An_unknown_team_is_reported_on_the_ticket_instead_of_breaking_the_chain()
    {
        using var sut = await Sut.CreateAsync("team-action-unknown");
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var runtime = new ProjectRuntime(sut.Slug)
        {
            Workspace = sut.Projects.ResolveWorkspacePath((await sut.Projects.GetProjectAsync(sut.Slug))!),
            Config = new AutomationConfig(),
        };
        var automation = new AutomationRule
        {
            Id = "review-team",
            Trigger = new TicketInColumnTriggerSpec { Columns = ["Review"] },
            Actions =
            [
                new StartTeamRunActionSpec { Team = "no-such-team" },
                new AddCommentActionSpec { Content = "chain kept going", Author = "producer" }
            ],
        };

        await sut.Executor.ExecuteAutomationAsync(
            runtime, automation, new TriggerFiring(parent.Id, parent.Title, "Review"), CancellationToken.None);

        Assert.Empty(await sut.Teams.ListRunsAsync(sut.Slug, parent.Id));
        var ticket = await sut.Tickets.GetTicketAsync(sut.Slug, parent.Id);
        Assert.Contains(ticket!.Activities, entry => entry.Text.Contains("no-such-team", StringComparison.Ordinal));
        Assert.Contains(ticket.Comments, comment => comment.Content == "chain kept going");
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One full set of services over a data directory. <see cref="AttachAsync"/> can build a second,
    /// entirely independent set over the same directory, which is what "the engine restarted" means
    /// for a feature whose state lives in the project database.
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
            var members = new MemberService(projects);
            Runs = new TeamRunService(Teams, tickets, members, new AgentTeamService(), NullLogger<TeamRunService>.Instance);

            var runs = new AgentRunRegistry();
            var sessions = new SessionRegistry();
            var cost = new CostTracker();
            Executor = new ActionExecutor(
                tickets, members, new LabelService(projects), sessions, runs,
                new ClaudeRunner(sessions, runs, new RunConcurrencyGate(1), NullLogger<ClaudeRunner>.Instance),
                cost, new LocalizationService(new AppSettingsService(projects.DataDir)), projects,
                new RunStateManager(runs, cost, tickets, NullLogger.Instance),
                FakeHttpClientFactory.Unused, Runs, NullLogger.Instance);
        }

        public ProjectService Projects { get; }
        public TicketService Tickets { get; }
        public TeamStore Teams { get; }
        public TeamRunService Runs { get; }
        public ActionExecutor Executor { get; }
        public string Slug { get; }

        public static async Task<Sut> CreateAsync(string name)
        {
            var tmp = new TempDir();
            var sut = await AttachAsync(tmp, name, create: true, owned: tmp);
            return sut;
        }

        public static async Task<Sut> AttachAsync(TempDir tmp, string name, bool create, TempDir? owned = null)
        {
            var projects = new ProjectService(tmp.Path);
            var project = create
                ? await projects.CreateProjectAsync(name)
                : (await projects.ListProjectsAsync()).Single(p => p.Name == name);
            var members = new MemberService(projects);
            var tickets = new TicketService(projects, members);
            var sut = new Sut(owned, projects, tickets, project.Slug);
            if (create)
            {
                // Every role's agent has to be a member: a team task is a sub-ticket assigned to it.
                var existing = (await members.ListMembersAsync(project.Slug))
                    .Select(member => member.Slug)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var agentSlug in ReviewTeam().AgentSlugs.Where(slug => !existing.Contains(slug)))
                    await members.CreateMemberAsync(project.Slug, agentSlug);
            }
            await sut.Teams.SaveDefinitionAsync(project.Slug, ReviewTeam());
            return sut;
        }

        public void Dispose() => _owned?.Dispose();
    }
}
