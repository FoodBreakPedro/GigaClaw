using System.Text.Json;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Integration;

/// <summary>
/// The SP-3 gate (doc/roadmap/PLAN-remaining.md §1). Every feature this suite composes already has a
/// unit suite of its own — P4 cycle validation (<c>TicketDependencyTests</c>), R4 leases
/// (<c>FileLeaseStoreTests</c> / <c>ActionExecutorFileLeaseTests</c>), R5 worktrees
/// (<c>ActionExecutorWorktreeTests</c>), R6 the merge queue (<c>MergeQueueTests</c>), C4/C5 joins
/// (<c>TeamRunJoinTests</c> / <c>ParallelRunAgentsTests</c>). What none of them proves is what
/// happens when those semantics meet: a lease denial inside a team run whose join is still
/// undecided, a cycle refused between sub-tickets of a live run, a merge queue draining behind two
/// worktree-isolated dispatches, all of it across a process restart.
/// <para>
/// Every scenario here runs the <b>real</b> services over a temp data directory — a real
/// <see cref="ActionExecutor"/> with a real <see cref="FileLeaseStore"/>, <see cref="MergeQueueStore"/>
/// and <see cref="TeamRunService"/>, real git repositories, and either the hermetic mock claude CLI
/// or <see cref="ClaudeRunner"/>'s <c>OllamaValidationError</c> fast-fail. No real claude CLI is ever
/// spawned and nothing reaches the network.
/// </para>
/// <para>
/// The load-bearing claim of the gate is <b>fail-closed with a receipt</b>: every refusal below is
/// asserted twice — once on what did not happen (no run registered, no edge inserted, no file
/// overwritten) and once on the durable receipt that says why (<c>file-lease-denial/v1</c>,
/// <c>merge-bounced/v1</c>, <c>merge-held/v1</c>, the join's "Lanes missing" brief, the
/// <c>dependency_cycle</c> refusal code).
/// </para>
/// </summary>
[Collection("MockClaude")]
public sealed class Sp3GateTests
{
    private const string SrcGlob = "src/**";
    private const string DocsGlob = "docs/**";

    /// <summary>Every agent writes <see cref="SrcGlob"/> — the contending configuration.</summary>
    private static readonly Dictionary<string, string> AllContend =
        Sp3Harness.Agents.ToDictionary(agent => agent, _ => SrcGlob);

    /// <summary>programmer owns src/**, everyone else owns docs/** — the disjoint configuration.</summary>
    private static readonly Dictionary<string, string> ProgrammerVsDocs =
        Sp3Harness.Agents.ToDictionary(agent => agent, agent => agent == "programmer" ? SrcGlob : DocsGlob);

    // ══════════════════════════════════════════════════════════════════════════
    // Scenario 1 — ownership conflict inside a parallel team run
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Two branches of one <c>parallelRunAgents</c> fan-out declare the same
    /// <c>allowedWriteGlobs</c>. Exactly one dispatches; the other is refused before
    /// <see cref="ClaudeRunner.RunAsync"/> with a <c>file-lease-denial/v1</c> receipt that names the
    /// <b>sibling lane's</b> ticket and agent — proving the lease is project-wide, not per-ticket,
    /// which is the only reason a fan-out is safe to declare at all. The refusal is a
    /// serialization, not a loss: once the winner's lease is released the loser dispatches, and the
    /// all-done join completes normally.
    /// </summary>
    [Fact]
    public async Task Contending_branches_serialize_exactly_one_dispatches_and_the_loser_gets_the_denial_receipt()
    {
        using var h = await Sp3Harness.CreateAsync("sp3-contend", AllContend);
        var parent = await h.Tickets.CreateTicketAsync(h.Slug, "Review the release", status: "Review");
        await h.FireParallelRunAsync(parent, synthesizer: "producer", join: "allDone");

        var run = Assert.Single(await h.Teams.ListRunsAsync(h.Slug, parent.Id));
        var lanes = await h.Teams.ListTasksAsync(h.Slug, run.Id);
        Assert.Equal(3, lanes.Count);
        var winner = lanes[0];
        var loser = lanes[1];

        // Pin the runner's only slot from outside so the winner's dispatch parks on the gate while
        // still holding the lease it took at the dispatch gate. This is the real ordering — the
        // lease is acquired before ClaudeRunner is called — not a hand-planted holder row.
        using var pinned = await h.Gate.AcquireAsync(isChat: false, agentName: "someone-else", CancellationToken.None);
        await h.DispatchAsync(winner.TicketId, winner.AgentSlug);
        Assert.True(
            await Sp3Harness.WaitUntilAsync(async () => (await h.Leases.ListActiveAsync(h.Slug)).Count == 1),
            "the winning branch never took its file lease");
        var held = Assert.Single(await h.Leases.ListActiveAsync(h.Slug));
        Assert.Equal(winner.TicketId, held.TicketId);

        // The loser's dispatch: same scope, different ticket, different agent.
        await h.DispatchAsync(loser.TicketId, loser.AgentSlug);

        Assert.Single(h.AgentRuns.AllForProject(h.Slug));                  // exactly one dispatch
        Assert.Empty(h.AgentRuns.AllForTicket(h.Slug, loser.TicketId));    // and it is not the loser's
        var receipt = Assert.Single(await h.CommentsAsync(loser.TicketId), c => c.Contains("file-lease-denial/v1"));
        using (var doc = JsonDocument.Parse(receipt))
        {
            var root = doc.RootElement;
            Assert.Equal("file-lease-denial/v1", root.GetProperty("schema").GetString());
            Assert.Equal(loser.AgentSlug, root.GetProperty("agent").GetString());
            Assert.Equal("block", root.GetProperty("enforcementMode").GetString());
            // Cross-lane: the conflict names the sibling branch, not this ticket's own history.
            Assert.Equal(winner.AgentSlug, root.GetProperty("conflictingAgent").GetString());
            Assert.Equal(winner.TicketId, root.GetProperty("conflictingTicketId").GetInt32());
            Assert.Contains(SrcGlob, root.GetProperty("scope").EnumerateArray().Select(e => e.GetString()));
        }
        Assert.Single(await h.Leases.ListActiveAsync(h.Slug)); // the refused branch claimed nothing

        // Hand the slot back: the winner runs, completes, and releases its lease.
        pinned.Dispose();
        Assert.True(
            await Sp3Harness.WaitUntilAsync(async () => (await h.Leases.ListActiveAsync(h.Slug)).Count == 0),
            "the winning branch never released its lease");

        // The loser was serialized, not dropped — the very same dispatch now goes through.
        await h.DispatchAsync(loser.TicketId, loser.AgentSlug);
        Assert.True(
            await Sp3Harness.WaitUntilAsync(() =>
                Task.FromResult(h.AgentRuns.AllForTicket(h.Slug, loser.TicketId).Any())),
            "the loser never dispatched after the lease was released");
        Assert.Single(await h.CommentsAsync(loser.TicketId), c => c.Contains("file-lease-denial/v1")); // no second denial

        // …and the join still decides: every lane reports, all-done fires, the run completes.
        foreach (var lane in lanes)
            await h.Tickets.MoveTicketAsync(h.Slug, lane.TicketId, "Done", "automation");
        await h.Runs.ReconcileRunAsync(h.Slug, run.Id);

        var joined = await h.Teams.GetRunAsync(h.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Joining, joined!.Status);
        await h.Tickets.MoveTicketAsync(h.Slug, joined.SynthesisTicketId!.Value, "Done", "automation");
        await h.Runs.ReconcileProjectAsync(h.Slug);
        Assert.Equal(TeamRunStatus.Completed, (await h.Teams.GetRunAsync(h.Slug, run.Id))!.Status);
    }

    /// <summary>
    /// The other half of scenario 1: when the refused lane is <i>reported</i> as failed rather than
    /// retried, the join must not pretend the ownership conflict never happened. The synthesis brief
    /// names the refused lane and carries its denial reason verbatim, and the all-done run ends
    /// Failed — a synthesis that covered two of three lanes is not a green run.
    /// </summary>
    [Fact]
    public async Task A_lane_refused_for_ownership_is_named_in_the_synthesis_and_the_run_fails_with_gaps()
    {
        using var h = await Sp3Harness.CreateAsync("sp3-contend-gap", AllContend);
        var parent = await h.Tickets.CreateTicketAsync(h.Slug, "Review the release", status: "Review");
        await h.FireParallelRunAsync(parent, synthesizer: "producer", join: "allDone");

        var run = Assert.Single(await h.Teams.ListRunsAsync(h.Slug, parent.Id));
        var lanes = await h.Teams.ListTasksAsync(h.Slug, run.Id);
        var refused = lanes[1];

        using (var pinned = await h.Gate.AcquireAsync(isChat: false, agentName: "someone-else", CancellationToken.None))
        {
            await h.DispatchAsync(lanes[0].TicketId, lanes[0].AgentSlug);
            Assert.True(await Sp3Harness.WaitUntilAsync(async () => (await h.Leases.ListActiveAsync(h.Slug)).Count == 1));
            await h.DispatchAsync(refused.TicketId, refused.AgentSlug);
        }
        Assert.Contains(await h.CommentsAsync(refused.TicketId), c => c.Contains("file-lease-denial/v1"));

        // The lanes that could run report; the refused one is reported as failed with the denial as
        // its reason — the shape an operator (or a future auto-escalation) would use.
        foreach (var lane in lanes.Where(l => l.Id != refused.Id))
            await h.Tickets.MoveTicketAsync(h.Slug, lane.TicketId, "Done", "automation");
        await h.Runs.FailTaskAsync(h.Slug, refused.TicketId, "file-lease-denial/v1: scope src/** is owned by another lane.");

        var joined = await h.Teams.GetRunAsync(h.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Joining, joined!.Status);
        var synthesis = await h.Tickets.GetTicketAsync(h.Slug, joined.SynthesisTicketId!.Value);
        Assert.Contains("Lanes that reported (2 of 3)", synthesis!.Description, StringComparison.Ordinal);
        Assert.Contains("Lanes missing (1 of 3)", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains(refused.AgentSlug, synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("file-lease-denial/v1", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("Do not present their subject matter as covered", synthesis.Description, StringComparison.Ordinal);

        await h.Tickets.MoveTicketAsync(h.Slug, synthesis.Id, "Done", "automation");
        await h.Runs.ReconcileProjectAsync(h.Slug);
        var failed = await h.Teams.GetRunAsync(h.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Failed, failed!.Status);
        Assert.Contains("file-lease-denial/v1", failed.FailureReason!, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Scenario 2 — lease expiry mid-run, conservatively
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A dispatch blocked by a live lease proceeds once — and only once — that lease has actually
    /// expired and been reaped, while a second lease whose TTL has <b>not</b> elapsed is left
    /// untouched by the same sweep and still refuses its contender. The reaper's <c>now</c> is
    /// injected (<see cref="FileLeaseReaper.ReapAllAsync"/> documents this), so "expired" is a fact
    /// about the clock the sweep was handed, not a sleep this test has to race.
    /// </summary>
    [Fact]
    public async Task An_expired_lease_is_reaped_and_the_waiting_branch_proceeds_while_a_live_lease_is_never_stolen()
    {
        using var h = await Sp3Harness.CreateAsync("sp3-expiry", ProgrammerVsDocs);
        var srcTicket = await h.Tickets.CreateTicketAsync(h.Slug, "Touches src", status: "Doing");
        var docsTicket = await h.Tickets.CreateTicketAsync(h.Slug, "Touches docs", status: "Doing");

        var now = DateTime.UtcNow;
        var shortLived = await h.Leases.AcquireAsync(
            h.Slug, srcTicket.Id, "run-src-holder", "code-janitor", [SrcGlob], now, TimeSpan.FromMinutes(30));
        var longLived = await h.Leases.AcquireAsync(
            h.Slug, docsTicket.Id, "run-docs-holder", "doc-janitor", [DocsGlob], now, TimeSpan.FromMinutes(90));
        Assert.True(shortLived.IsAcquired);
        Assert.True(longLived.IsAcquired);

        // Both dispatches are refused while both leases are live.
        await h.DispatchAsync(srcTicket.Id, "programmer");
        await h.DispatchAsync(docsTicket.Id, "qa-tester");
        Assert.Empty(h.AgentRuns.AllForProject(h.Slug));
        Assert.Contains(await h.CommentsAsync(srcTicket.Id), c => c.Contains("run-src-holder"));
        Assert.Contains(await h.CommentsAsync(docsTicket.Id), c => c.Contains("run-docs-holder"));

        // A sweep 45 minutes later: the 30-minute lease is past its TTL, the 90-minute one is not.
        var reaped = await h.Reaper.ReapAllAsync(now.AddMinutes(45), CancellationToken.None);
        var reapedOne = Assert.Single(reaped);
        Assert.Equal("run-src-holder", reapedOne.RunId);
        Assert.Equal("run-docs-holder", Assert.Single(await h.Leases.ListActiveAsync(h.Slug)).RunId);

        // The branch that was waiting on the expired lease now proceeds and takes a lease of its own.
        await h.DispatchAsync(srcTicket.Id, "programmer");
        Assert.True(
            await Sp3Harness.WaitUntilAsync(() =>
                Task.FromResult(h.AgentRuns.AllForTicket(h.Slug, srcTicket.Id).Any())),
            "the branch waiting on the expired lease never dispatched");
        Assert.Single(await h.CommentsAsync(srcTicket.Id), c => c.Contains("file-lease-denial/v1"));

        // …and the lease that had NOT expired is still enforcing: nothing was stolen to make room.
        await h.DispatchAsync(docsTicket.Id, "qa-tester");
        Assert.Empty(h.AgentRuns.AllForTicket(h.Slug, docsTicket.Id));
        // Characterization: a lease denial is written on EVERY refused attempt. A blocked dispatch
        // never commits its trigger firing (ExecuteRunAgentActionAsync returns before FinalizeAsync),
        // so a repeating ticketInColumn trigger retries it each poll and accumulates one receipt per
        // poll for as long as the conflicting lease lives. R6's enqueueMerge deliberately writes
        // merge-held/v1 only on the FIRST hold for exactly this reason; R4 has no such guard. Noise,
        // not a correctness break — recorded in doc/roadmap/SP3-EVIDENCE.md as an open finding.
        Assert.Equal(2, (await h.CommentsAsync(docsTicket.Id)).Count(c => c.Contains("file-lease-denial/v1")));
        Assert.Contains(
            await h.Leases.ListActiveAsync(h.Slug), lease => lease.RunId == "run-docs-holder");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Scenario 3 — a dependency cycle across team-run sub-tickets
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// P4's cycle validation is the board's, and a team run's ordering is nothing but the board's
    /// edges — so an edge that would close a loop between two sub-tickets of a live run is refused
    /// at insert time, the existing edges are untouched, and the run goes on to a decided outcome
    /// instead of deadlocking on a graph that can never resolve. Both directions are exercised: a
    /// hand-drawn back-edge, and a whole team task whose declared <c>DependsOn</c> would close the
    /// loop (which must roll the half-built task back rather than leave a node nothing waits on).
    /// </summary>
    [Fact]
    public async Task A_cycle_between_team_sub_tickets_is_refused_at_edge_creation_and_the_run_still_decides()
    {
        using var h = await Sp3Harness.CreateAsync("sp3-cycle", ProgrammerVsDocs);
        await h.Teams.SaveDefinitionAsync(h.Slug, Sp3Harness.DedupTeam());
        var parent = await h.Tickets.CreateTicketAsync(h.Slug, "Review the release", status: "Review");
        var run = await h.Runs.StartRunAsync(h.Slug, Sp3Harness.DedupTeamSlug, parent.Id);
        var tasks = await h.Teams.ListTasksAsync(h.Slug, run.Id);
        var security = tasks.Single(t => t.TemplateKey == "security-lane");
        var performance = tasks.Single(t => t.TemplateKey == "performance-lane");
        var dedup = tasks.Single(t => t.TemplateKey == "dedup");

        // dedup is already blocked by security; making security wait on dedup would close the loop.
        var cycle = await Assert.ThrowsAsync<TicketDependencyException>(
            () => h.Tickets.AddTicketDependencyAsync(h.Slug, security.TicketId, dedup.TicketId));
        Assert.Equal("dependency_cycle", cycle.Code);

        // Nothing was half-written: the refused edge is absent and the run's real edges survive.
        Assert.Empty((await h.Tickets.ListBlockingTicketsAsync(h.Slug, security.TicketId))!);
        Assert.Equal(
            new[] { security.TicketId, performance.TicketId }.OrderBy(id => id),
            (await h.Teams.GetTaskByTicketAsync(h.Slug, dedup.TicketId))!.BlockedByTicketIds.OrderBy(id => id));

        // The same refusal through the run's own graph-building path: a new task whose DependsOn
        // would close a loop leaves no orphan row behind.
        var loopTicket = await h.Tickets.CreateTicketAsync(h.Slug, "Loop lane", status: "Backlog");
        await h.Tickets.AddTicketDependencyAsync(h.Slug, security.TicketId, loopTicket.Id);
        var refusedDraft = new TeamTaskDraft("loop-lane", "security", "programmer", loopTicket.Id)
        {
            DependsOn = ["security-lane"]
        };
        var draftCycle = await Assert.ThrowsAsync<TicketDependencyException>(
            () => h.Teams.AddTaskAsync(h.Slug, run.Id, refusedDraft));
        Assert.Equal("dependency_cycle", draftCycle.Code);
        Assert.DoesNotContain(
            await h.Teams.ListTasksAsync(h.Slug, run.Id), task => task.TemplateKey == "loop-lane");
        await h.Tickets.RemoveTicketDependencyAsync(h.Slug, security.TicketId, loopTicket.Id);

        // And the run is not deadlocked: the ordering the cycle attacked still resolves.
        Assert.Equal(TeamRunService.HoldStatus, (await h.Tickets.GetTicketAsync(h.Slug, dedup.TicketId))!.Status);
        await h.Tickets.MoveTicketAsync(h.Slug, security.TicketId, "Done", "automation");
        await h.Tickets.MoveTicketAsync(h.Slug, performance.TicketId, "Done", "automation");
        await h.Runs.ReconcileRunAsync(h.Slug, run.Id);
        Assert.Equal(TeamRunService.ReadyStatus, (await h.Tickets.GetTicketAsync(h.Slug, dedup.TicketId))!.Status);

        await h.Tickets.MoveTicketAsync(h.Slug, dedup.TicketId, "Done", "automation");
        await h.Runs.ReconcileRunAsync(h.Slug, run.Id);
        await h.Runs.ReconcileProjectAsync(h.Slug);
        var decided = await h.Teams.GetRunAsync(h.Slug, run.Id);
        Assert.True(
            decided!.Status is TeamRunStatus.Joining or TeamRunStatus.Completed,
            $"the run never reached a decided outcome (status {decided.Status})");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Scenario 4 — merge-queue composition behind two worktree-isolated runs
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Two tickets dispatched with <c>isolation: "worktree"</c> commit to their own branches and
    /// both reach the queue through the production <c>enqueueMerge</c> action. Without the owner's
    /// approval nothing merges — each candidate is <c>Held</c> with a <c>merge-held/v1</c> receipt
    /// and the workspace file is byte-for-byte unchanged. Once approved they land one at a time,
    /// the second rebased onto the first, history linear, both carrying <c>merge-completed/v1</c>.
    /// </summary>
    [Fact]
    public async Task Two_worktree_isolated_runs_enqueue_and_land_one_at_a_time_behind_the_owner_gate()
    {
        using var h = await Sp3Harness.CreateAsync("sp3-merge-ok", ProgrammerVsDocs);
        var (t1, wt1) = await h.WorktreeDispatchAsync("Ticket one", "programmer");
        var (t2, wt2) = await h.WorktreeDispatchAsync("Ticket two", "qa-tester");

        await Sp3Harness.CommitAsync(wt1, "shared.txt", "base\nfrom-t1\n", "t1-change");
        await Sp3Harness.CommitAsync(wt2, "README.md", "hello\nfrom-t2\n", "t2-change");

        // Unapproved: enqueueMerge records intent and refuses to land it.
        await h.FireEnqueueMergeAsync(t1);
        await h.FireEnqueueMergeAsync(t2);
        Assert.Equal(2, (await h.Queue.ListAsync(h.Slug)).Count);
        Assert.All(await h.Queue.ListAsync(h.Slug), entry => Assert.Equal(MergeQueueState.Held, entry.State));
        Assert.Contains(await h.CommentsAsync(t1), c => c.Contains("merge-held/v1"));
        Assert.Null(await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None));
        Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "shared.txt")));

        // Owner approves in settings.json between polls — no engine restart.
        h.SetMergeApproval(true);

        var first = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);
        Assert.Equal(MergeQueueState.Merged, first!.State);
        Assert.Equal(t1, first.TicketId);
        var second = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);
        Assert.Equal(MergeQueueState.Merged, second!.State);
        Assert.Equal(t2, second.TicketId);
        Assert.Null(await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None));

        Assert.Equal("base\nfrom-t1\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "shared.txt")));
        Assert.Equal("hello\nfrom-t2\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "README.md")));
        var log = await ProcessRunner.RunAsync("git", "log --oneline", h.Workspace, TimeSpan.FromSeconds(30));
        Assert.Equal(3, log.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains(await h.CommentsAsync(t1), c => c.Contains("merge-completed/v1"));
        Assert.Contains(await h.CommentsAsync(t2), c => c.Contains("merge-completed/v1"));
    }

    /// <summary>
    /// The composition claim that matters most for the owner's default-on decision: file leases are
    /// <b>declarative</b> — they lease the scope an agent's contract claims, not the bytes it
    /// actually writes — so two runs with provably disjoint lease scopes (<c>src/**</c> vs
    /// <c>docs/**</c>) can still produce commits that touch the same line. Both dispatches are
    /// correctly permitted by R4; the merge queue is what stops the second from overwriting the
    /// first. It bounces to Blocked with a <c>merge-bounced/v1</c> receipt naming the conflicting
    /// file, the workspace still holds ticket one's content, and the loser's worktree is left
    /// rebase-free rather than mid-conflict.
    /// </summary>
    [Fact]
    public async Task Disjoint_leases_do_not_prevent_a_real_conflict_and_the_queue_bounces_it_rather_than_overwriting()
    {
        using var h = await Sp3Harness.CreateAsync("sp3-merge-conflict", ProgrammerVsDocs);
        var (t1, wt1) = await h.WorktreeDispatchAsync("Ticket one", "programmer");
        var (t2, wt2) = await h.WorktreeDispatchAsync("Ticket two", "qa-tester");

        // Both dispatches were permitted — the lease scopes really are disjoint, and both runs held
        // a lease of their own at dispatch time. Neither refusal path fired.
        Assert.DoesNotContain(await h.CommentsAsync(t1), c => c.Contains("file-lease-denial/v1"));
        Assert.DoesNotContain(await h.CommentsAsync(t2), c => c.Contains("file-lease-denial/v1"));

        // …and yet both commits edit the same line of the same file.
        await Sp3Harness.CommitAsync(wt1, "shared.txt", "base\nfrom-t1\n", "t1-change");
        await Sp3Harness.CommitAsync(wt2, "shared.txt", "base\nfrom-t2-conflicting\n", "t2-change");

        h.SetMergeApproval(true);
        await h.FireEnqueueMergeAsync(t1);
        await h.FireEnqueueMergeAsync(t2);

        Assert.Equal(MergeQueueState.Merged, (await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None))!.State);
        var bounced = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);
        Assert.Equal(MergeQueueState.Bounced, bounced!.State);
        Assert.Equal(t2, bounced.TicketId);

        Assert.Equal("Blocked", (await h.Tickets.GetTicketAsync(h.Slug, t2))!.Status);
        var receipt = Assert.Single(await h.CommentsAsync(t2), c => c.Contains("merge-bounced/v1"));
        using (var doc = JsonDocument.Parse(receipt))
        {
            Assert.Equal("conflict", doc.RootElement.GetProperty("cause").GetString());
            Assert.Contains(
                "shared.txt",
                doc.RootElement.GetProperty("conflictingFiles").EnumerateArray().Select(e => e.GetString()));
        }

        // Never a silent overwrite: ticket one's line is what the workspace still carries.
        Assert.Equal("base\nfrom-t1\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "shared.txt")));
        var status = await ProcessRunner.RunAsync("git", "status", wt2, TimeSpan.FromSeconds(30));
        Assert.True(status.Success);
        Assert.DoesNotContain("rebase in progress", status.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The ordering between the two opt-ins the owner is deciding on: <c>enqueueMerge</c> is close
    /// to useless without worktree isolation. A ticket dispatched <i>without</i>
    /// <c>isolation: "worktree"</c> has no recorded branch, so enqueueing a merge for it bounces
    /// immediately with a <c>no-worktree</c> receipt instead of queuing a candidate that could never
    /// rebase — and nothing is added to the queue at all.
    /// </summary>
    [Fact]
    public async Task Enqueueing_a_merge_for_a_ticket_that_never_ran_isolated_bounces_with_a_no_worktree_receipt()
    {
        using var h = await Sp3Harness.CreateAsync("sp3-merge-no-worktree", ProgrammerVsDocs);
        h.SetMergeApproval(true);

        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ran in place", status: "Doing");
        await h.DispatchAsync(ticket.Id, "programmer", model: Sp3Harness.NonClaudeModel);
        Assert.Null((await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!.WorktreeBranch);

        await h.FireEnqueueMergeAsync(ticket.Id);

        Assert.Empty(await h.Queue.ListAsync(h.Slug));
        Assert.Equal("Blocked", (await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!.Status);
        var receipt = Assert.Single(await h.CommentsAsync(ticket.Id), c => c.Contains("merge-bounced/v1"));
        using var doc = JsonDocument.Parse(receipt);
        Assert.Equal("no-worktree", doc.RootElement.GetProperty("cause").GetString());
    }

    /// <summary>
    /// <b>The SP-3 F1 interlock</b> (replaces the characterization test that recorded its absence).
    /// A merge whose diff falls inside a live lease held by <i>another</i> run is <b>held</b>, not
    /// bounced: nothing is written to the workspace, the lease is never stolen, a single
    /// <c>merge-held/v1</c> receipt (<c>rule: "file-lease-interlock"</c>) names the holder, and later
    /// polls re-hold it silently rather than re-receipting. When the holding run finishes and its
    /// lease is released, the very same candidate lands — a hold is a delay, not a refusal.
    /// <para>
    /// The lease here is taken on the ordinary dispatch path by a genuinely in-flight run parked on
    /// <see cref="RunConcurrencyGate"/> — the exact configuration the finding was about, since a
    /// dispatch <i>without</i> <c>isolation: "worktree"</c> executes in the same checkout the merge
    /// rewrites.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_live_overlapping_lease_holds_the_merge_until_it_is_released_and_receipts_the_hold_once()
    {
        using var h = await Sp3Harness.CreateAsync("sp3-merge-vs-lease", AllContend);
        h.SetMergeApproval(true);

        var (mergeTicket, worktree) = await h.WorktreeDispatchAsync("Ticket one", "producer");
        Directory.CreateDirectory(Path.Combine(worktree, "src"));
        await Sp3Harness.CommitAsync(worktree, Path.Combine("src", "app.txt"), "landed\n", "t1-change");
        await h.FireEnqueueMergeAsync(mergeTicket);
        Assert.Equal(MergeQueueState.Queued, Assert.Single(await h.Queue.ListAsync(h.Slug)).State);
        await h.LeasesQuiesceAsync();

        // A second run genuinely in flight, in place (no worktree isolation), holding a lease over
        // src/** — the scope the queued merge is about to rewrite in that same checkout.
        var inPlace = await h.Tickets.CreateTicketAsync(h.Slug, "Runs in the workspace", status: "Doing");
        var pinned = await h.Gate.AcquireAsync(isChat: false, agentName: "someone-else", CancellationToken.None);
        await h.DispatchAsync(inPlace.Id, "programmer");
        Assert.True(
            await Sp3Harness.WaitUntilAsync(async () => (await h.Leases.ListActiveAsync(h.Slug)).Count == 1),
            "the in-place run never took its src/** lease");
        var lease = Assert.Single(await h.Leases.ListActiveAsync(h.Slug));
        Assert.Equal(SrcGlob, Assert.Single(lease.Scope));

        // The merge is held: nothing lands, and the live lease is neither stolen nor reaped.
        var held = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);
        Assert.Equal(MergeQueueState.Held, held!.State);
        Assert.Equal(mergeTicket, held.TicketId);
        Assert.False(File.Exists(Path.Combine(h.Workspace, "src", "app.txt")));
        Assert.Contains(await h.Leases.ListActiveAsync(h.Slug), active => active.LeaseId == lease.LeaseId);
        Assert.DoesNotContain(await h.CommentsAsync(mergeTicket), c => c.Contains("merge-completed/v1"));
        Assert.DoesNotContain(await h.CommentsAsync(mergeTicket), c => c.Contains("merge-bounced/v1"));

        var receipt = Assert.Single(await h.CommentsAsync(mergeTicket), c => c.Contains("merge-held/v1"));
        using (var doc = JsonDocument.Parse(receipt))
        {
            var root = doc.RootElement;
            Assert.Equal("merge-held/v1", root.GetProperty("schema").GetString());
            Assert.Equal("file-lease-interlock", root.GetProperty("rule").GetString());
            Assert.Equal(lease.LeaseId, root.GetProperty("conflictingLeaseId").GetString());
            Assert.Equal(lease.RunId, root.GetProperty("conflictingRunId").GetString());
            Assert.Equal(inPlace.Id, root.GetProperty("conflictingTicketId").GetInt32());
            Assert.Contains(
                "src/app.txt",
                root.GetProperty("overlappingFiles").EnumerateArray().Select(e => e.GetString()));
        }

        // Retry while still blocked: held again, and the receipt is NOT written a second time —
        // the same first-hold-only discipline R6 already applies to the approval hold.
        var stillHeld = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);
        Assert.Equal(MergeQueueState.Held, stillHeld!.State);
        Assert.Single(await h.CommentsAsync(mergeTicket), c => c.Contains("merge-held/v1"));

        // The holder finishes and releases; the very same candidate now lands.
        pinned.Dispose();
        Assert.True(
            await Sp3Harness.WaitUntilAsync(async () => (await h.Leases.ListActiveAsync(h.Slug)).Count == 0),
            "the in-place run never released its lease");

        var merged = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);
        Assert.Equal(MergeQueueState.Merged, merged!.State);
        Assert.Equal(mergeTicket, merged.TicketId);
        Assert.Equal("landed\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "src", "app.txt")));
        Assert.Single(await h.CommentsAsync(mergeTicket), c => c.Contains("merge-completed/v1"));
        Assert.Single(await h.CommentsAsync(mergeTicket), c => c.Contains("merge-held/v1"));
    }

    /// <summary>
    /// The interlock is a real overlap test, not "any lease stops any merge": a live lease over
    /// <c>src/**</c> leaves a merge that only rewrites <c>README.md</c> alone. Same conservative
    /// glob-intersection R4 uses at acquire time, so the two gates cannot disagree about what
    /// "overlapping" means.
    /// </summary>
    [Fact]
    public async Task A_disjoint_live_lease_does_not_hold_the_merge()
    {
        using var h = await Sp3Harness.CreateAsync("sp3-merge-lease-disjoint", ProgrammerVsDocs);
        h.SetMergeApproval(true);

        var (mergeTicket, worktree) = await h.WorktreeDispatchAsync("Ticket one", "programmer");
        await Sp3Harness.CommitAsync(worktree, "README.md", "hello\nfrom-t1\n", "t1-change");
        await h.LeasesQuiesceAsync();

        var other = await h.Tickets.CreateTicketAsync(h.Slug, "Owns src", status: "Doing");
        var holder = await h.Leases.AcquireAsync(
            h.Slug, other.Id, "run-src-holder", "code-janitor", [SrcGlob],
            DateTime.UtcNow, TimeSpan.FromMinutes(90));
        Assert.True(holder.IsAcquired);

        await h.FireEnqueueMergeAsync(mergeTicket);
        var merged = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);

        Assert.Equal(MergeQueueState.Merged, merged!.State);
        Assert.Equal("hello\nfrom-t1\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "README.md")));
        Assert.DoesNotContain(await h.CommentsAsync(mergeTicket), c => c.Contains("merge-held/v1"));
        // …and the disjoint lease is still exactly where it was: the merge did not reap it to pass.
        Assert.Contains(await h.Leases.ListActiveAsync(h.Slug), l => l.RunId == "run-src-holder");
    }

    /// <summary>
    /// A run holds a lease over precisely the files it wrote, so counting its own lease would
    /// deadlock every merge behind its own author. The lease belonging to the ticket that produced
    /// the branch is excluded; an overlapping lease on <i>any other</i> ticket still holds.
    /// </summary>
    [Fact]
    public async Task The_producing_tickets_own_lease_does_not_hold_its_own_merge()
    {
        using var h = await Sp3Harness.CreateAsync("sp3-merge-own-lease", AllContend);
        h.SetMergeApproval(true);

        var (mergeTicket, worktree) = await h.WorktreeDispatchAsync("Ticket one", "producer");
        Directory.CreateDirectory(Path.Combine(worktree, "src"));
        await Sp3Harness.CommitAsync(worktree, Path.Combine("src", "app.txt"), "landed\n", "t1-change");
        await h.LeasesQuiesceAsync();

        // The branch's own author, still holding its lease over the very files it wrote.
        var own = await h.Leases.AcquireAsync(
            h.Slug, mergeTicket, "run-author", "producer", [SrcGlob],
            DateTime.UtcNow, TimeSpan.FromMinutes(90));
        Assert.True(own.IsAcquired);

        await h.FireEnqueueMergeAsync(mergeTicket);
        var merged = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);

        Assert.Equal(MergeQueueState.Merged, merged!.State);
        Assert.Equal("landed\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "src", "app.txt")));
        Assert.DoesNotContain(await h.CommentsAsync(mergeTicket), c => c.Contains("merge-held/v1"));
    }

    /// <summary>
    /// The third way a hold clears (after release and after the holder completes): the lease simply
    /// expires and is reaped. The reaper's <c>now</c> is injected, so "expired" is a fact about the
    /// clock the sweep was handed rather than a sleep this test has to race.
    /// </summary>
    [Fact]
    public async Task An_expired_and_reaped_lease_stops_holding_the_merge()
    {
        using var h = await Sp3Harness.CreateAsync("sp3-merge-lease-expiry", AllContend);
        h.SetMergeApproval(true);

        var (mergeTicket, worktree) = await h.WorktreeDispatchAsync("Ticket one", "producer");
        Directory.CreateDirectory(Path.Combine(worktree, "src"));
        await Sp3Harness.CommitAsync(worktree, Path.Combine("src", "app.txt"), "landed\n", "t1-change");
        await h.LeasesQuiesceAsync();

        // Acquired an hour ago for 90 minutes: live now, and 30 minutes from expiring.
        var acquiredAt = DateTime.UtcNow.AddMinutes(-60);
        var other = await h.Tickets.CreateTicketAsync(h.Slug, "Owns src", status: "Doing");
        var holder = await h.Leases.AcquireAsync(
            h.Slug, other.Id, "run-src-holder", "code-janitor", [SrcGlob],
            acquiredAt, TimeSpan.FromMinutes(90));
        Assert.True(holder.IsAcquired);

        await h.FireEnqueueMergeAsync(mergeTicket);
        Assert.Equal(
            MergeQueueState.Held,
            (await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None))!.State);
        Assert.False(File.Exists(Path.Combine(h.Workspace, "src", "app.txt")));

        // A sweep past the TTL reaps it — and only then does the merge proceed.
        var reaped = await h.Reaper.ReapAllAsync(acquiredAt.AddMinutes(120), CancellationToken.None);
        Assert.Equal("run-src-holder", Assert.Single(reaped).RunId);

        var merged = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);
        Assert.Equal(MergeQueueState.Merged, merged!.State);
        Assert.Equal("landed\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "src", "app.txt")));
        Assert.Single(await h.CommentsAsync(mergeTicket), c => c.Contains("merge-held/v1"));
    }

    /// <summary>
    /// The hold is durable, not a fact this process remembers: a restart over the same data
    /// directory re-reads the held entry and its hold reason from SQLite, re-holds it silently (no
    /// second receipt, nothing landed prematurely), and lands it once the lease clears.
    /// </summary>
    [Fact]
    public async Task A_restart_while_a_merge_is_held_resumes_held_without_a_second_receipt()
    {
        using var tmp = new TempDir();
        int mergeTicket;

        // ── first "process": the merge is claimed, held behind a live lease, and receipted once ──
        {
            var h = await Sp3Harness.AttachAsync(tmp, "sp3-merge-hold-restart", create: true, AllContend);
            h.SetMergeApproval(true);
            var (ticket, worktree) = await h.WorktreeDispatchAsync("Ticket one", "producer");
            mergeTicket = ticket;
            Directory.CreateDirectory(Path.Combine(worktree, "src"));
            await Sp3Harness.CommitAsync(worktree, Path.Combine("src", "app.txt"), "landed\n", "t1-change");
            await h.LeasesQuiesceAsync();

            var other = await h.Tickets.CreateTicketAsync(h.Slug, "Owns src", status: "Doing");
            Assert.True((await h.Leases.AcquireAsync(
                h.Slug, other.Id, "run-src-holder", "code-janitor", [SrcGlob],
                DateTime.UtcNow, TimeSpan.FromMinutes(90))).IsAcquired);

            await h.FireEnqueueMergeAsync(mergeTicket);
            Assert.Equal(
                MergeQueueState.Held,
                (await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None))!.State);
            Assert.Single(await h.CommentsAsync(mergeTicket), c => c.Contains("merge-held/v1"));
        }

        // ── restart: brand-new everything over the same directory ──────────────
        using var resumed = await Sp3Harness.AttachAsync(tmp, "sp3-merge-hold-restart", create: false, AllContend);

        var heldAgain = await resumed.Processor.ProcessProjectAsync(resumed.Slug, CancellationToken.None);
        Assert.Equal(MergeQueueState.Held, heldAgain!.State);
        Assert.False(File.Exists(Path.Combine(resumed.Workspace, "src", "app.txt")));
        Assert.Single(await resumed.CommentsAsync(mergeTicket), c => c.Contains("merge-held/v1"));

        await resumed.Leases.ReleaseAsync(resumed.Slug, "run-src-holder", DateTime.UtcNow);
        var merged = await resumed.Processor.ProcessProjectAsync(resumed.Slug, CancellationToken.None);
        Assert.Equal(MergeQueueState.Merged, merged!.State);
        Assert.Equal("landed\n", await File.ReadAllTextAsync(Path.Combine(resumed.Workspace, "src", "app.txt")));
        Assert.Single(await resumed.CommentsAsync(mergeTicket), c => c.Contains("merge-held/v1"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Scenario 5 — restart at the worst moment
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every service is thrown away and rebuilt over the same data directory at the worst possible
    /// instant: a file lease held, a team run whose join has not yet fired, and a merge-queue entry
    /// claimed <c>Merging</c> but never completed. The new process must resume all three from disk
    /// — no double dispatch (no second fan-out, no second synthesis ticket, no second merge), no
    /// lost receipts (the denial written before the restart is still on the ticket afterwards).
    /// </summary>
    [Fact]
    public async Task A_restart_with_leases_held_a_join_undecided_and_the_queue_claimed_resumes_without_double_dispatch()
    {
        using var tmp = new TempDir();
        long runId;
        int parentId, refusedTicketId, mergeTicketId, doneTicketId;
        string denialBefore;

        // ── first "process" ────────────────────────────────────────────────────
        {
            var h = await Sp3Harness.AttachAsync(tmp, "sp3-restart", create: true, ProgrammerVsDocs);
            h.SetMergeApproval(true);

            // (a) a merge candidate claimed but never completed — a process kill mid-merge.
            var (t1, wt1) = await h.WorktreeDispatchAsync("Ticket one", "programmer");
            await Sp3Harness.CommitAsync(wt1, "shared.txt", "base\nfrom-t1\n", "t1-change");
            await h.FireEnqueueMergeAsync(t1);
            var stuck = await h.Queue.ClaimNextAsync(h.Slug, approved: true, DateTime.UtcNow, CancellationToken.None);
            Assert.Equal(MergeQueueState.Merging, stuck!.State);
            mergeTicketId = t1;

            // (b) a team run fanned out, one lane done, the join undecided.
            var parent = await h.Tickets.CreateTicketAsync(h.Slug, "Review the release", status: "Review");
            await h.FireParallelRunAsync(parent, synthesizer: "producer", join: "allDone");
            var run = Assert.Single(await h.Teams.ListRunsAsync(h.Slug, parent.Id));
            var lanes = await h.Teams.ListTasksAsync(h.Slug, run.Id);
            parentId = parent.Id;
            runId = run.Id;
            doneTicketId = lanes[0].TicketId;
            refusedTicketId = lanes[1].TicketId;
            await h.Tickets.MoveTicketAsync(h.Slug, doneTicketId, "Done", "automation");

            // (c) a live lease held by a run this process will never finish, plus the receipt the
            //     branch it refused already carries.
            var holder = await h.Leases.AcquireAsync(
                h.Slug, refusedTicketId, "run-orphan", "code-janitor", [DocsGlob],
                DateTime.UtcNow, TimeSpan.FromMinutes(90));
            Assert.True(holder.IsAcquired);
            await h.DispatchAsync(refusedTicketId, "qa-tester");
            denialBefore = Assert.Single(await h.CommentsAsync(refusedTicketId), c => c.Contains("file-lease-denial/v1"));
            Assert.Empty(h.AgentRuns.AllForTicket(h.Slug, refusedTicketId));
        }

        // ── restart: brand-new everything over the same directory ──────────────
        using var resumed = await Sp3Harness.AttachAsync(tmp, "sp3-restart", create: false, ProgrammerVsDocs);

        // The lease outlived the process that took it, and still refuses the same branch — and the
        // receipt written before the restart is still there, byte for byte.
        var stillHeld = Assert.Single(
            await resumed.Leases.ListActiveAsync(resumed.Slug), lease => lease.RunId == "run-orphan");
        Assert.Equal(DocsGlob, Assert.Single(stillHeld.Scope));
        await resumed.DispatchAsync(refusedTicketId, "qa-tester");
        Assert.Empty(resumed.AgentRuns.AllForTicket(resumed.Slug, refusedTicketId));
        var denials = (await resumed.CommentsAsync(refusedTicketId)).Where(c => c.Contains("file-lease-denial/v1")).ToList();
        Assert.Equal(2, denials.Count);
        Assert.Equal(denialBefore, denials[0]); // the pre-restart receipt survived unaltered

        // The run resumes from the board, not from anything this process was handed.
        var open = Assert.Single(await resumed.Teams.ListRunsAsync(resumed.Slug, openOnly: true));
        Assert.Equal(runId, open.Id);
        Assert.Equal(TeamRunStatus.Running, open.Status);
        // The lane finished before the restart is on the board; the first reconcile of the new
        // process is what records it on the run — nothing was handed across the boundary.
        Assert.Equal("Done", (await resumed.Tickets.GetTicketAsync(resumed.Slug, doneTicketId))!.Status);
        await resumed.Runs.ReconcileProjectAsync(resumed.Slug);
        Assert.Equal(
            TeamTaskStatus.Done,
            (await resumed.Teams.GetTaskByTicketAsync(resumed.Slug, doneTicketId))!.Status);
        Assert.Equal(TeamRunStatus.Running, (await resumed.Teams.GetRunAsync(resumed.Slug, runId))!.Status);

        // Re-firing the fan-out after the restart re-attaches: no second run, no duplicate lanes.
        var parentTicket = (await resumed.Tickets.GetTicketAsync(resumed.Slug, parentId))!;
        await resumed.FireParallelRunAsync(parentTicket, synthesizer: "producer", join: "allDone");
        Assert.Single(await resumed.Teams.ListRunsAsync(resumed.Slug, parentId));
        Assert.Equal(3, (await resumed.Teams.ListTasksAsync(resumed.Slug, runId)).Count);
        Assert.Equal(3, (await resumed.Tickets.ListTicketsAsync(resumed.Slug, parentId: parentId)).Count);

        // The claimed merge is recovered and completed exactly once by the new processor.
        var merged = await resumed.Processor.ProcessProjectAsync(resumed.Slug, CancellationToken.None);
        Assert.Equal(MergeQueueState.Merged, merged!.State);
        Assert.Equal(mergeTicketId, merged.TicketId);
        Assert.Equal("base\nfrom-t1\n", await File.ReadAllTextAsync(Path.Combine(resumed.Workspace, "shared.txt")));
        Assert.Null(await resumed.Processor.ProcessProjectAsync(resumed.Slug, CancellationToken.None));
        Assert.Single(await resumed.CommentsAsync(mergeTicketId), c => c.Contains("merge-completed/v1"));

        // …and the join finishes in the new process: exactly one synthesis ticket, ever.
        foreach (var lane in await resumed.Teams.ListTasksAsync(resumed.Slug, runId))
            await resumed.Tickets.MoveTicketAsync(resumed.Slug, lane.TicketId, "Done", "automation");
        await resumed.Runs.ReconcileProjectAsync(resumed.Slug);
        await resumed.Runs.ReconcileProjectAsync(resumed.Slug);
        var joined = await resumed.Teams.GetRunAsync(resumed.Slug, runId);
        Assert.Equal(TeamRunStatus.Joining, joined!.Status);
        Assert.Equal(4, (await resumed.Tickets.ListTicketsAsync(resumed.Slug, parentId: parentId)).Count);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Scenario 6 — everything at once
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The case the gate is named for. One team run in which an ownership conflict, a refused
    /// dependency cycle and a join all happen: two contending lanes serialize (one dispatches, one
    /// is refused with its receipt), a back-edge that would deadlock the graph is rejected while the
    /// conflict is live, a conservative reap leaves the live lease alone, the refused lane proceeds
    /// once the lease is released, and the dedup task that waited on both lanes is finally released
    /// and joined. Every refusal leaves its receipt; the run still reaches a decided outcome.
    /// </summary>
    [Fact]
    public async Task Ownership_conflict_a_refused_cycle_and_the_join_all_land_in_one_run_each_with_its_receipt()
    {
        using var h = await Sp3Harness.CreateAsync("sp3-everything", AllContend);
        await h.Teams.SaveDefinitionAsync(h.Slug, Sp3Harness.DedupTeam());
        var parent = await h.Tickets.CreateTicketAsync(h.Slug, "Review the release", status: "Review");
        var run = await h.Runs.StartRunAsync(h.Slug, Sp3Harness.DedupTeamSlug, parent.Id);
        var tasks = await h.Teams.ListTasksAsync(h.Slug, run.Id);
        var security = tasks.Single(t => t.TemplateKey == "security-lane");
        var performance = tasks.Single(t => t.TemplateKey == "performance-lane");
        var dedup = tasks.Single(t => t.TemplateKey == "dedup");

        // ── ownership conflict: both lanes declare src/**, so only one may run ──
        using var pinned = await h.Gate.AcquireAsync(isChat: false, agentName: "someone-else", CancellationToken.None);
        await h.DispatchAsync(security.TicketId, security.AgentSlug);
        Assert.True(await Sp3Harness.WaitUntilAsync(async () => (await h.Leases.ListActiveAsync(h.Slug)).Count == 1));
        await h.DispatchAsync(performance.TicketId, performance.AgentSlug);

        Assert.Single(h.AgentRuns.AllForProject(h.Slug));
        var denial = Assert.Single(await h.CommentsAsync(performance.TicketId), c => c.Contains("file-lease-denial/v1"));
        using (var doc = JsonDocument.Parse(denial))
        {
            Assert.Equal("block", doc.RootElement.GetProperty("enforcementMode").GetString());
            Assert.Equal(security.TicketId, doc.RootElement.GetProperty("conflictingTicketId").GetInt32());
        }

        // ── cycle: while the conflict is live, close the loop on the same graph ──
        var cycle = await Assert.ThrowsAsync<TicketDependencyException>(
            () => h.Tickets.AddTicketDependencyAsync(h.Slug, performance.TicketId, dedup.TicketId));
        Assert.Equal("dependency_cycle", cycle.Code);
        Assert.Empty((await h.Tickets.ListBlockingTicketsAsync(h.Slug, performance.TicketId))!);

        // ── conservative reap: a live lease is not collected just because something waits on it ──
        Assert.Empty(await h.Reaper.ReapAllAsync(DateTime.UtcNow, CancellationToken.None));
        Assert.Single(await h.Leases.ListActiveAsync(h.Slug));

        // ── serialization, not loss: the refused lane runs once the lease is released ──
        pinned.Dispose();
        Assert.True(
            await Sp3Harness.WaitUntilAsync(async () => (await h.Leases.ListActiveAsync(h.Slug)).Count == 0),
            "the winning lane never released its lease");
        await h.DispatchAsync(performance.TicketId, performance.AgentSlug);
        Assert.True(
            await Sp3Harness.WaitUntilAsync(() =>
                Task.FromResult(h.AgentRuns.AllForTicket(h.Slug, performance.TicketId).Any())),
            "the refused lane never dispatched after the conflict cleared");
        Assert.Single(await h.CommentsAsync(performance.TicketId), c => c.Contains("file-lease-denial/v1"));

        // ── join: the dedup task the cycle attacked is released, and the run decides ──
        await h.Tickets.MoveTicketAsync(h.Slug, security.TicketId, "Done", "automation");
        await h.Tickets.MoveTicketAsync(h.Slug, performance.TicketId, "Done", "automation");
        await h.Runs.ReconcileRunAsync(h.Slug, run.Id);
        Assert.Equal(TeamRunService.ReadyStatus, (await h.Tickets.GetTicketAsync(h.Slug, dedup.TicketId))!.Status);

        await h.Tickets.MoveTicketAsync(h.Slug, dedup.TicketId, "Done", "automation");
        await h.Runs.ReconcileRunAsync(h.Slug, run.Id);
        var joined = await h.Teams.GetRunAsync(h.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Joining, joined!.Status);
        var synthesis = await h.Tickets.GetTicketAsync(h.Slug, joined.SynthesisTicketId!.Value);
        Assert.Contains("Lanes that reported (3 of 3)", synthesis!.Description, StringComparison.Ordinal);
        await h.Tickets.MoveTicketAsync(h.Slug, synthesis.Id, "Done", "automation");
        await h.Runs.ReconcileProjectAsync(h.Slug);
        Assert.Equal(TeamRunStatus.Completed, (await h.Teams.GetRunAsync(h.Slug, run.Id))!.Status);
    }
}

/// <summary>
/// One full set of the real services over one temp data directory: the same idiom as
/// <c>TeamRunLifecycleTests.Sut</c> and <c>ActionExecutorFileLeaseTests.Harness</c>, widened to
/// carry everything SP-3 has to compose at once (leases + reaper, merge queue + processor + owner
/// gate, team runs, and an <see cref="ActionExecutor"/> wired to all of them). <see cref="AttachAsync"/>
/// builds a second, entirely independent set over the same directory — which is what "the engine
/// restarted" means for state that lives in SQLite and git rather than in this process.
/// </summary>
internal sealed class Sp3Harness : IDisposable
{
    /// <summary>Fails a dispatch inside <see cref="ClaudeRunner"/> before any subprocess is spawned
    /// (<c>OllamaValidationError</c>) — the idiom <c>ActionExecutorWorktreeTests</c> uses when what
    /// is under test is whether the dispatch was *allowed*, not what the agent then did.</summary>
    public const string NonClaudeModel = "qwen3-coder:30b";

    public const string DedupTeamSlug = "sp3-dedup-review";

    public static readonly string[] Agents = ["programmer", "qa-tester", "groomer", "producer"];

    private readonly TempDir? _owned;
    private int _automationSeq;

    private Sp3Harness(
        TempDir? owned, string dataDir, ProjectService projects, TicketService tickets,
        string slug, string workspace)
    {
        _owned = owned;
        DataDir = dataDir;
        Projects = projects;
        Tickets = tickets;
        Slug = slug;
        Workspace = workspace;

        var members = new MemberService(projects);
        Teams = new TeamStore(projects, tickets);
        Runs = new TeamRunService(Teams, tickets, members, new AgentTeamService(), NullLogger<TeamRunService>.Instance);

        AgentRuns = new AgentRunRegistry();
        Gate = new RunConcurrencyGate(maxConcurrent: 1);
        var sessions = new SessionRegistry();
        var cost = new CostTracker();
        var appSettings = new AppSettingsService(dataDir);

        Leases = new FileLeaseStore(projects);
        Reaper = new FileLeaseReaper(projects, Leases, NullLogger<FileLeaseReaper>.Instance);
        Queue = new MergeQueueStore(projects);
        Processor = new MergeQueueProcessor(
            projects, tickets, Queue, Leases, appSettings, NullLogger<MergeQueueProcessor>.Instance);

        Executor = new ActionExecutor(
            tickets, members, new LabelService(projects), sessions, AgentRuns,
            new ClaudeRunner(sessions, AgentRuns, Gate, NullLogger<ClaudeRunner>.Instance),
            cost, new LocalizationService(appSettings), projects,
            new RunStateManager(AgentRuns, cost, tickets, NullLogger.Instance),
            FakeHttpClientFactory.Unused, Runs, NullLogger.Instance,
            outboundGate: null,
            leases: Leases,
            mergeQueue: Queue,
            mergeApproval: new MergeApprovalGate(appSettings.GetApprovedMergeProjects));

        Runtime = new ProjectRuntime(slug) { Workspace = workspace, Config = new AutomationConfig() };
    }

    public string DataDir { get; }
    public ProjectService Projects { get; }
    public TicketService Tickets { get; }
    public TeamStore Teams { get; }
    public TeamRunService Runs { get; }
    public AgentRunRegistry AgentRuns { get; }
    public RunConcurrencyGate Gate { get; }
    public FileLeaseStore Leases { get; }
    public FileLeaseReaper Reaper { get; }
    public MergeQueueStore Queue { get; }
    public MergeQueueProcessor Processor { get; }
    public ActionExecutor Executor { get; }
    public ProjectRuntime Runtime { get; }
    public string Slug { get; }
    public string Workspace { get; }

    // ── Construction ────────────────────────────────────────────────────────

    public static Task<Sp3Harness> CreateAsync(string name, IReadOnlyDictionary<string, string> globsByAgent)
    {
        var tmp = new TempDir();
        return AttachAsync(tmp, name, create: true, globsByAgent, owned: tmp);
    }

    public static async Task<Sp3Harness> AttachAsync(
        TempDir tmp,
        string name,
        bool create,
        IReadOnlyDictionary<string, string> globsByAgent,
        TempDir? owned = null)
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
            await InitRepoAsync(workspace);
            var existing = (await members.ListMembersAsync(project.Slug))
                .Select(member => member.Slug)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var agent in Agents.Where(agent => !existing.Contains(agent)))
                await members.CreateMemberAsync(project.Slug, agent);
        }

        // Rewritten on attach as well as on create: a restart re-reads contracts.json and the skill
        // files from the workspace, so they have to be there for the second process too.
        WriteContracts(workspace, globsByAgent);
        foreach (var agent in Agents)
            TestSkillBuilder.Create(workspace, agent, scenario: "default");

        return new Sp3Harness(owned, tmp.Path, projects, tickets, project.Slug, workspace);
    }

    /// <summary>One agent entry per member, each declaring its own <c>allowedWriteGlobs</c> in
    /// block mode — the R4 contract shape, with the scope varied per test so contention is a
    /// property of the manifest rather than of a hand-planted lease row.</summary>
    private static void WriteContracts(string workspace, IReadOnlyDictionary<string, string> globsByAgent)
    {
        var agentsDir = Path.Combine(workspace, ".agents");
        Directory.CreateDirectory(agentsDir);
        var entries = string.Join(",\n", globsByAgent.Select(pair => $$"""
                "{{pair.Key}}": {
                  "enforcement": "block",
                  "dispatches": ["assignment"],
                  "riskClass": "code-write",
                  "allowedWriteGlobs": ["{{pair.Value}}"],
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

    // ── Git ─────────────────────────────────────────────────────────────────

    public static async Task RunGitAsync(string cwd, string args)
    {
        var res = await ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromSeconds(30));
        Assert.True(res.Success, $"git {args} failed in {cwd}: {res.Stderr}\n{res.Stdout}");
    }

    private static async Task InitRepoAsync(string workspace)
    {
        await RunGitAsync(workspace, "init -q");
        await RunGitAsync(workspace, "config user.email test@example.com");
        await RunGitAsync(workspace, "config user.name \"GigaClaw Test\"");
        // Windows' Git ships core.autocrlf=true, which silently rewrites LF blobs to CRLF on
        // `git worktree add` / `git merge --ff-only` checkouts in a temp repo with no
        // .gitattributes — corrupting the exact bytes the assertions compare. Pin it off, exactly
        // as MergeQueueTests learned to (commit 4082184).
        await RunGitAsync(workspace, "config core.autocrlf false");
        await File.WriteAllTextAsync(Path.Combine(workspace, "shared.txt"), "base\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "hello\n");
        await RunGitAsync(workspace, "add -A");
        await RunGitAsync(workspace, "commit -q -m initial");
    }

    public static async Task CommitAsync(string worktree, string relativePath, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(worktree, relativePath), content);
        await RunGitAsync(worktree, "add -A");
        await RunGitAsync(worktree, $"commit -q -m {message}");
    }

    // ── Firing automations ──────────────────────────────────────────────────

    private AutomationRule Rule(string id, params ActionSpec[] actions) => new()
    {
        Id = id,
        Enabled = true,
        Trigger = new TicketInColumnTriggerSpec { Columns = ["Todo", "Doing", "Review"] },
        Conditions = [],
        Actions = [.. actions],
    };

    private async Task FireAsync(int ticketId, AutomationRule rule)
    {
        var ticket = await Tickets.GetTicketAsync(Slug, ticketId);
        await Executor.ExecuteAutomationAsync(
            Runtime, rule, new TriggerFiring(ticketId, ticket!.Title, ticket.Status), CancellationToken.None);
    }

    /// <summary>
    /// The ordinary per-agent dispatch an agent's own <c>ticketInColumn</c> automation carries.
    /// Nothing about it knows the ticket came from a fan-out — which is the whole point: a branch is
    /// subject to <see cref="RunConcurrencyGate"/> and the R4 lease gate because it takes this path.
    /// Each call gets a fresh automation id so a retry after a refusal is a new chain, exactly as a
    /// later engine tick would be.
    /// </summary>
    public Task DispatchAsync(int ticketId, string agent, string? isolation = null, string? model = null) =>
        FireAsync(ticketId, Rule(
            $"dispatch-{agent}-{Interlocked.Increment(ref _automationSeq)}",
            new RunAgentActionSpec { Agent = agent, MaxTurns = 1, Model = model, Isolation = isolation }));

    /// <summary>Fans a parent ticket out into three branches through the real <c>parallelRunAgents</c>
    /// action — one branch per agent, no edges between them.</summary>
    public Task FireParallelRunAsync(Ticket parent, string? synthesizer, string join) =>
        FireAsync(parent.Id, Rule("sp3-fan-out", new ParallelRunAgentsActionSpec
        {
            RunSlug = "sp3-parallel",
            Name = "SP-3 parallel review",
            Join = join,
            Synthesizer = synthesizer,
            Branches =
            [
                new ParallelBranchSpec { Agent = "programmer" },
                new ParallelBranchSpec { Agent = "qa-tester" },
                new ParallelBranchSpec { Agent = "groomer" }
            ]
        }));

    public Task FireEnqueueMergeAsync(int ticketId) =>
        FireAsync(ticketId, Rule($"sp3-enqueue-{ticketId}", new EnqueueMergeActionSpec()));

    /// <summary>Dispatches a fresh ticket with R5 worktree isolation and returns the recorded
    /// worktree path. Uses <see cref="NonClaudeModel"/> so the run fails fast without a subprocess —
    /// what matters here is that the worktree was created and durably recorded on the ticket.</summary>
    public async Task<(int TicketId, string WorktreePath)> WorktreeDispatchAsync(string title, string agent)
    {
        var ticket = await Tickets.CreateTicketAsync(Slug, title, status: "Doing");
        await DispatchAsync(ticket.Id, agent, isolation: "worktree", model: NonClaudeModel);
        Assert.True(
            await WaitUntilAsync(async () =>
                (await Tickets.GetTicketAsync(Slug, ticket.Id))!.WorktreePath is not null),
            $"worktree isolation never recorded a worktree for ticket #{ticket.Id}");
        var after = await Tickets.GetTicketAsync(Slug, ticket.Id);
        Assert.Equal("ticket/" + ticket.Id, after!.WorktreeBranch);
        Assert.True(Directory.Exists(after.WorktreePath));
        return (ticket.Id, after.WorktreePath!);
    }

    /// <summary>Waits until no lease is active. A dispatch takes its lease before the run starts and
    /// releases it when the run ends, so a test that wants to plant a lease of its own has to let the
    /// dispatch it just made finish letting go of its.</summary>
    public async Task LeasesQuiesceAsync() =>
        Assert.True(
            await WaitUntilAsync(async () => (await Leases.ListActiveAsync(Slug)).Count == 0),
            "a dispatched run never released its file lease");

    // ── Observation ─────────────────────────────────────────────────────────

    public async Task<List<string>> CommentsAsync(int ticketId)
    {
        var ticket = await Tickets.GetTicketAsync(Slug, ticketId);
        return ticket!.Comments.Select(c => c.Content).ToList();
    }

    /// <summary>Overwrites settings.json directly — the same file <see cref="AppSettingsService"/>
    /// re-reads on every approval check, so this is exactly an owner editing it by hand.</summary>
    public void SetMergeApproval(bool approved)
    {
        var json = approved
            ? JsonSerializer.Serialize(new { ApprovedMergeProjects = new[] { Slug } })
            : "{}";
        File.WriteAllText(Path.Combine(DataDir, "settings.json"), json);
    }

    public static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int timeoutSeconds = 30)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (!cts.IsCancellationRequested)
        {
            if (await condition()) return true;
            try { await Task.Delay(25, cts.Token); }
            catch (OperationCanceledException) { break; }
        }
        return await condition();
    }

    // ── Definitions ─────────────────────────────────────────────────────────

    /// <summary>Two independent lanes plus a dedup task that waits for both — the smallest graph
    /// that has a real dependency edge to attack with a cycle.</summary>
    public static TeamDefinition DedupTeam() =>
        new(DedupTeamSlug, "SP-3 dedup review", "Two lanes then a synthesis.", "🔍")
        {
            Roles =
            [
                new TeamRole("security", "programmer"),
                new TeamRole("performance", "qa-tester"),
                new TeamRole("lead", "producer")
            ],
            TaskGraph =
            [
                new TeamTaskTemplate("dedup", "lead", "Deduplicate findings")
                {
                    DependsOn = ["security-lane", "performance-lane"]
                },
                new TeamTaskTemplate("security-lane", "security", "Security review"),
                new TeamTaskTemplate("performance-lane", "performance", "Performance review")
            ],
            SynthesizerRole = "lead"
        };

    public void Dispose() => _owned?.Dispose();
}
