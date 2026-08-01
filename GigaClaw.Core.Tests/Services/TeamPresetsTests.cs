using GigaClaw.Core.Automation.Verdicts;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// C8's two built-in presets, driven through the real C4/C5 services exactly like
/// <see cref="TeamRunJoinTests"/> and <see cref="TeamRunLifecycleTests"/> do — proving the shipped
/// <c>ProjectTemplate/Agents/teams.json</c> definitions (via <see cref="AgentTeamService"/>), not a
/// hand-rolled stand-in, so a regression here means the preset that ships actually broke.
/// </summary>
public sealed class TeamPresetsTests
{
    private static readonly AgentTeamService Roster = new();

    /// <summary>A handoff comment shaped the way HandoffReader accepts, findings as open loops.</summary>
    private static string Handoff(
        string agent, int ticketId, string runId, string summary, IEnumerable<(string Statement, bool Blocking)> findings)
    {
        var openLoops = string.Join(",\n", findings.Select(f =>
            $$"""{ "statement": "{{f.Statement}}", "blocking": {{(f.Blocking ? "true" : "false")}} }"""));
        return $$"""
            Lane finished.

            GIGACLAW-HANDOFF v1 {{agent}} ticket-{{ticketId}} run-{{runId}}

            ```json
            {
              "schemaVersion": 1,
              "agent": "{{agent}}",
              "ticketId": {{ticketId}},
              "runId": "{{runId}}",
              "summary": "{{summary}}",
              "outputs": [{ "kind": "path", "ref": "doc/finding.md", "note": "lane output" }],
              "openLoops": [
                {{openLoops}}
              ],
              "producedAtUtc": "2026-07-30T12:00:00Z"
            }
            ```
            """;
    }

    private static async Task FinishLaneAsync(
        Sut sut, TeamTask task, string runId, string summary, IEnumerable<(string Statement, bool Blocking)> findings)
    {
        await sut.Tickets.AddCommentAsync(
            sut.Slug, task.TicketId, Handoff(task.AgentSlug, task.TicketId, runId, summary, findings), task.AgentSlug);
        await sut.Tickets.MoveTicketAsync(sut.Slug, task.TicketId, "Done");
    }

    private static TeamTask Task(IEnumerable<TeamTask> tasks, string key) =>
        tasks.Single(task => task.TemplateKey == key);

    // ── Acceptance criterion 1: parallel-review dedup + verdict gate ───────────

    [Fact]
    public async Task ParallelReview_DedupesOverlappingFindingsAcrossLanesAndPostsAGateConsumableVerdict()
    {
        var definition = Roster.GetDefinitionBySlug(AgentTeamService.ParallelReviewSlug)!;
        Assert.True(definition.IsExecutable);
        Assert.True(definition.DedupeFindings);

        using var sut = await Sut.CreateAsync("parallel-review-dedup", definition);
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Board toolbar redesign", status: "Review");
        var run = await sut.Runs.StartRunAsync(sut.Slug, definition.Slug, parent.Id);
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);

        // Both lanes independently notice the same regressed file (a shared, blocking finding) plus
        // one finding each lane alone raised.
        await FinishLaneAsync(
            sut, Task(tasks, "accessibility-lane"), "a11y1", "Focus and contrast pass complete.",
            [
                ("app.css: focus ring removed on icon buttons breaks keyboard nav", true),
                ("app.css: secondary button contrast measures 2.9:1, below WCAG AA", true),
            ]);
        await FinishLaneAsync(
            sut, Task(tasks, "coverage-lane"), "cov1", "Coverage pass complete.",
            [
                ("app.css: missing focus indicator on icon buttons has no regression test", false),
                ("TicketService.cs: untested null-assignee branch", false),
            ]);

        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);
        var joined = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Joining, joined!.Status);

        var synthesis = await sut.Tickets.GetTicketAsync(sut.Slug, joined.SynthesisTicketId!.Value);

        // The deduped section merges the shared "focus ring / focus indicator" finding across both
        // lanes, attributed to both — and keeps the two lane-unique findings separate.
        Assert.Contains("## Deduplicated findings", synthesis!.Description, StringComparison.Ordinal);
        Assert.Contains("reported by: accessibility-lane (ui-auditor), coverage-lane (qa-tester)", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("[blocking]", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("secondary button contrast measures 2.9:1", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("untested null-assignee branch", synthesis.Description, StringComparison.Ordinal);
        // The full per-lane rendering is still there underneath — dedup is additive, not a replacement.
        Assert.Contains("Handoff from ui-auditor", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("Handoff from qa-tester", synthesis.Description, StringComparison.Ordinal);

        // Verdict-gated: a real, contract-valid GIGACLAW-VERDICT landed on the PARENT ticket, so an
        // ordinary verdictIs automation can gate on this run without parsing anyone's free prose.
        var parentAfterJoin = await sut.Tickets.GetTicketAsync(sut.Slug, parent.Id);
        var verdictComment = parentAfterJoin!.Comments.Single(c => VerdictReader.ContainsMarker(c.Content));
        Assert.True(VerdictReader.TryRead(verdictComment.Content, out var verdict, out var error));
        Assert.Null(error);
        Assert.Equal("team-synthesis", verdict!.Agent);
        // Two lanes both reported a blocking finding, so the gate-consumable decision is FIX, not SHIP.
        Assert.Equal("FIX", verdict.Decision);
        Assert.Contains(verdict.VetoItems, veto => veto.Statement.Contains("focus ring removed", StringComparison.Ordinal));

        await sut.Tickets.MoveTicketAsync(sut.Slug, synthesis.Id, "Done");
        await sut.Runs.ReconcileProjectAsync(sut.Slug);
        Assert.Equal(TeamRunStatus.Completed, (await sut.Teams.GetRunAsync(sut.Slug, run.Id))!.Status);
    }

    [Fact]
    public async Task ParallelReview_WithNoParseableFindings_DegradesToANoOpSection()
    {
        var definition = Roster.GetDefinitionBySlug(AgentTeamService.ParallelReviewSlug)!;
        using var sut = await Sut.CreateAsync("parallel-review-no-findings", definition);
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Trivial change", status: "Review");
        var run = await sut.Runs.StartRunAsync(sut.Slug, definition.Slug, parent.Id);
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);

        await FinishLaneAsync(sut, Task(tasks, "accessibility-lane"), "a1", "Nothing to report.", []);
        await FinishLaneAsync(sut, Task(tasks, "coverage-lane"), "c1", "Nothing to report.", []);
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);

        var joined = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        var synthesis = await sut.Tickets.GetTicketAsync(sut.Slug, joined!.SynthesisTicketId!.Value);
        Assert.Contains("No parseable findings on any reporting lane's handoff.", synthesis!.Description, StringComparison.Ordinal);

        var parentAfterJoin = await sut.Tickets.GetTicketAsync(sut.Slug, parent.Id);
        var verdictComment = parentAfterJoin!.Comments.Single(c => VerdictReader.ContainsMarker(c.Content));
        Assert.True(VerdictReader.TryRead(verdictComment.Content, out var verdict, out _));
        Assert.Equal("SHIP", verdict!.Decision);
    }

    // ── Acceptance criterion 2: hypothesis-debug arbitration ───────────────────

    [Fact]
    public async Task HypothesisDebug_RecordsCompetingHypothesesAndClosesTheLosingLaneWithAReason()
    {
        var definition = Roster.GetDefinitionBySlug(AgentTeamService.HypothesisDebugSlug)!;
        Assert.True(definition.IsExecutable);
        Assert.True(definition.RequireEvidenceCitingArbitration);

        using var sut = await Sut.CreateAsync("hypothesis-debug", definition);
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Intermittent 500 on checkout", status: "Blocked");
        var run = await sut.Runs.StartRunAsync(sut.Slug, definition.Slug, parent.Id);
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);

        // Two competing hypotheses, each with its own cited evidence.
        await FinishLaneAsync(
            sut, Task(tasks, "investigator-a-lane"), "hypA", "Hypothesis: a null cache lookup races the checkout write.",
            [("Stack trace at CheckoutService.cs:88 shows a null-ref in the cache lookup path, reproduced 4/5 runs.", false)]);
        await FinishLaneAsync(
            sut, Task(tasks, "investigator-b-lane"), "hypB", "Hypothesis: the payment webhook retries out of order.",
            [("Webhook logs show two deliveries for the same order id 40s apart; no repro under load test.", false)]);

        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);
        var joined = await sut.Teams.GetRunAsync(sut.Slug, run.Id);
        Assert.Equal(TeamRunStatus.Joining, joined!.Status);

        var synthesis = await sut.Tickets.GetTicketAsync(sut.Slug, joined.SynthesisTicketId!.Value);
        Assert.Equal("producer", synthesis!.AssignedTo);
        // Both hypotheses (summary) and their evidence (open loops) are in the lead's brief.
        Assert.Contains("a null cache lookup races the checkout write", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("the payment webhook retries out of order", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("null-ref in the cache lookup path, reproduced 4/5 runs", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("two deliveries for the same order id 40s apart", synthesis.Description, StringComparison.Ordinal);
        // The brief demands an evidence-citing arbitration and states the marker the lead must emit.
        Assert.Contains("## Arbitration required", synthesis.Description, StringComparison.Ordinal);
        Assert.Contains("GIGACLAW-ARBITRATION v1 winner=", synthesis.Description, StringComparison.Ordinal);

        // The lead arbitrates, citing the winning lane's evidence.
        await sut.Tickets.AddCommentAsync(
            sut.Slug, synthesis.Id,
            "GIGACLAW-ARBITRATION v1 winner=investigator-a-lane\n"
            + "reason: the reproduced stack trace is direct evidence; the webhook theory has no repro.",
            "producer");
        await sut.Tickets.MoveTicketAsync(sut.Slug, synthesis.Id, "Done");
        await sut.Runs.ReconcileProjectAsync(sut.Slug);

        Assert.Equal(TeamRunStatus.Completed, (await sut.Teams.GetRunAsync(sut.Slug, run.Id))!.Status);

        // Mechanically enforced: the host, not the lead's prose, closed the losing lane with a reason.
        var loserTicket = await sut.Tickets.GetTicketAsync(sut.Slug, Task(tasks, "investigator-b-lane").TicketId);
        var closingComment = loserTicket!.Comments.Single(c => c.Content.Contains("was selected as the winning hypothesis", StringComparison.Ordinal));
        Assert.Contains("'investigator-a-lane'", closingComment.Content, StringComparison.Ordinal);
        Assert.Contains("the reproduced stack trace is direct evidence", closingComment.Content, StringComparison.Ordinal);
        Assert.Equal("automation", closingComment.Author);

        // The winning lane gets no such comment.
        var winnerTicket = await sut.Tickets.GetTicketAsync(sut.Slug, Task(tasks, "investigator-a-lane").TicketId);
        Assert.DoesNotContain(
            winnerTicket!.Comments, c => c.Content.Contains("was selected as the winning hypothesis", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HypothesisDebug_WithNoArbitrationMarker_ClosesNothingAndStaysANoOp()
    {
        var definition = Roster.GetDefinitionBySlug(AgentTeamService.HypothesisDebugSlug)!;
        using var sut = await Sut.CreateAsync("hypothesis-debug-no-marker", definition);
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Flaky test", status: "Blocked");
        var run = await sut.Runs.StartRunAsync(sut.Slug, definition.Slug, parent.Id);
        var tasks = await sut.Teams.ListTasksAsync(sut.Slug, run.Id);

        await FinishLaneAsync(sut, Task(tasks, "investigator-a-lane"), "a", "Hypothesis A.", [("evidence A", false)]);
        await FinishLaneAsync(sut, Task(tasks, "investigator-b-lane"), "b", "Hypothesis B.", [("evidence B", false)]);
        await sut.Runs.ReconcileRunAsync(sut.Slug, run.Id);
        var joined = await sut.Teams.GetRunAsync(sut.Slug, run.Id);

        // The lead closes its own ticket without ever posting the marker (prose-only decision).
        await sut.Tickets.AddCommentAsync(sut.Slug, joined!.SynthesisTicketId!.Value, "Went with hypothesis A, informally.", "producer");
        await sut.Tickets.MoveTicketAsync(sut.Slug, joined.SynthesisTicketId!.Value, "Done");
        await sut.Runs.ReconcileProjectAsync(sut.Slug);

        var loserTicket = await sut.Tickets.GetTicketAsync(sut.Slug, Task(tasks, "investigator-b-lane").TicketId);
        Assert.DoesNotContain(
            loserTicket!.Comments, c => c.Content.Contains("was selected as the winning hypothesis", StringComparison.Ordinal));
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private sealed class Sut : IDisposable
    {
        private readonly TempDir _owned;

        private Sut(TempDir owned, ProjectService projects, TicketService tickets, string slug)
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

        public static async Task<Sut> CreateAsync(string name, TeamDefinition definition)
        {
            var tmp = new TempDir();
            var projects = new ProjectService(tmp.Path);
            var project = await projects.CreateProjectAsync(name);
            var members = new MemberService(projects);
            var tickets = new TicketService(projects, members);
            var sut = new Sut(tmp, projects, tickets, project.Slug);

            foreach (var agentSlug in definition.AgentSlugs)
                await members.CreateMemberAsync(project.Slug, agentSlug);
            await sut.Teams.SaveDefinitionAsync(project.Slug, definition);
            return sut;
        }

        public void Dispose() => _owned.Dispose();
    }
}
