using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Verdicts;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// C3: the bounded repair loop. A FIX verdict sends the work back with the reviewer's findings
/// attached; after <c>maxReviewCycles</c> rounds the ticket escalates to the owner with every
/// round's reasons on the ticket. The cycle count is recounted from the comment trail on every
/// evaluation — these tests hold that property, including across a rebuilt executor.
/// </summary>
public class RepairLoopTests
{
    private const string Reviewer = "blog-reviewer";
    private const string Producer = "blog-writer";

    // ── Counting ────────────────────────────────────────────────────────────

    [Fact]
    public void Every_fix_verdict_is_one_round()
    {
        var state = RepairLoop.Resolve(
            Comments(Fix("first pass", "cite-your-sources"), Fix("second pass", "cite-your-sources")),
            maxCycles: 2);

        Assert.Equal(2, state.CyclesUsed);
        Assert.Equal(0, state.Remaining);
        Assert.True(state.Exhausted);
        Assert.Equal([1, 2], state.Cycles.Select(c => c.Number));
    }

    [Fact]
    public void A_ship_or_block_closes_the_episode_so_a_later_fix_is_round_one()
    {
        foreach (var closer in new[] { Ship("accepted"), Block("escalated", "policy-violation") })
        {
            var state = RepairLoop.Resolve(
                Comments(Fix("first pass", "thin-evidence"), closer, Fix("new work, new problem", "thin-evidence")),
                maxCycles: 2);

            Assert.Equal(1, state.CyclesUsed);
            Assert.False(state.Exhausted);
        }
    }

    [Fact]
    public void The_escalation_receipt_hands_an_unblocked_ticket_a_fresh_budget()
    {
        var spent = RepairLoop.Resolve(Comments(Fix("a"), Fix("b")), maxCycles: 2);
        Assert.True(spent.Exhausted);

        var receipt = RepairLoop.RenderEscalation(spent, ticketId: 7);
        Assert.True(RepairLoop.IsEscalationReceipt(receipt));

        // The owner unblocks the ticket; the agents get a budget again rather than a dead ticket.
        var afterEscalation = RepairLoop.Resolve(
            Comments(Fix("a"), Fix("b")).Append(new VerdictComment(receipt, "automation", new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc))).ToList(),
            maxCycles: 2);
        Assert.Equal(0, afterEscalation.CyclesUsed);
        Assert.Null(afterEscalation.Newest);
    }

    [Fact]
    public void Another_reviewers_fix_does_not_spend_the_gated_reviewers_budget()
    {
        var comments = Comments(
            Fix("blog side", agent: Reviewer),
            Fix("ui side", agent: "ui-auditor"),
            Fix("blog side again", agent: Reviewer));

        Assert.Equal(3, RepairLoop.Resolve(comments, maxCycles: 3).CyclesUsed);
        Assert.Equal(2, RepairLoop.Resolve(comments, maxCycles: 3, agent: Reviewer).CyclesUsed);
        Assert.Equal(1, RepairLoop.Resolve(comments, maxCycles: 3, agent: "ui-auditor").CyclesUsed);
    }

    [Fact]
    public void Prose_and_broken_verdicts_never_spend_a_round()
    {
        var state = RepairLoop.Resolve(
            Comments(
                new VerdictComment("APPROVE — 93/100, ship it", Reviewer, new DateTime(2026, 7, 30, 8, 0, 0, DateTimeKind.Utc)),
                new VerdictComment(
                    $"GIGACLAW-VERDICT v1 {Reviewer} FIX artifact-{Digest("x")}\n\n```json\n{{ \"schemaVersion\": 1,\n```\n",
                    Reviewer, new DateTime(2026, 7, 30, 8, 1, 0, DateTimeKind.Utc))),
            maxCycles: 2);

        Assert.Equal(0, state.CyclesUsed);
    }

    [Fact]
    public void A_reviewer_that_re_reviews_unchanged_bytes_is_flagged_as_spinning()
    {
        var same = Digest("unchanged");
        var state = RepairLoop.Resolve(
            Comments(Fix("first", digest: same), Fix("again, nothing changed", digest: same)),
            maxCycles: 3);

        Assert.False(state.Cycles[0].RepeatsPreviousArtifact);
        Assert.True(state.Cycles[1].RepeatsPreviousArtifact);
        // It still spends a round: a loop that never converges must terminate, not stall forever.
        Assert.Equal(2, state.CyclesUsed);
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    [Fact]
    public void The_brief_carries_the_veto_items_the_categories_and_the_budget()
    {
        var state = RepairLoop.Resolve(
            Comments(Fix("round one", "cite-your-sources"), Fix("round two", "unverified-quote")),
            maxCycles: 3);
        var brief = RepairLoop.RenderBrief(state, "ticket #42");

        Assert.Contains("Repair round 2 of 3", brief, StringComparison.Ordinal);
        Assert.Contains("unverified-quote", brief, StringComparison.Ordinal);
        Assert.Contains("Evidence 4/10", brief, StringComparison.Ordinal);
        Assert.Contains("no primary sources", brief, StringComparison.Ordinal);
        Assert.Contains("One more FIX verdict is allowed", brief, StringComparison.Ordinal);
        // Earlier rounds survive as their veto codes, so a recurring failure stays visible.
        Assert.Contains("Earlier rounds also vetoed: cite-your-sources", brief, StringComparison.Ordinal);
        // A brief is never rendered for a ticket with no outstanding FIX.
        Assert.Equal("", RepairLoop.RenderBrief(RepairLoop.Resolve(Comments(Ship("done")), 2), "ticket #42"));
    }

    [Fact]
    public void The_escalation_comment_shows_every_round_not_just_the_last_one()
    {
        var state = RepairLoop.Resolve(
            Comments(Fix("round one", "cite-your-sources"), Fix("round two", "unverified-quote")),
            maxCycles: 2);
        var escalation = RepairLoop.RenderEscalation(state, ticketId: 42);

        Assert.StartsWith("GIGACLAW-REPAIR v1 ticket-42 escalated 2/2", escalation, StringComparison.Ordinal);
        Assert.Contains("### Round 1/2", escalation, StringComparison.Ordinal);
        Assert.Contains("### Round 2/2", escalation, StringComparison.Ordinal);
        Assert.Contains("cite-your-sources", escalation, StringComparison.Ordinal);
        Assert.Contains("unverified-quote", escalation, StringComparison.Ordinal);
        Assert.Contains("round one", escalation, StringComparison.Ordinal);
        Assert.Contains("round two", escalation, StringComparison.Ordinal);
        Assert.Contains("Evidence 4/10 — no primary sources", escalation, StringComparison.Ordinal);
        Assert.Contains("maxReviewCycles", escalation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_escalation_comment_is_not_itself_a_verdict()
    {
        // It quotes past verdicts. If it reproduced their marker lines, the next scan would read
        // the escalation as a fresh judgement and the gate would gate on a quote.
        var escalation = RepairLoop.RenderEscalation(
            RepairLoop.Resolve(Comments(Fix("round one", "cite-your-sources")), maxCycles: 1), ticketId: 42);

        Assert.False(VerdictReader.ContainsMarker(escalation));
        Assert.True(RepairLoop.IsEscalationReceipt(escalation));
    }

    // ── Budget resolution ───────────────────────────────────────────────────

    [Fact]
    public void The_cap_comes_from_the_agents_contract_then_the_defaults()
    {
        const string manifest = """
        {
          "version": 1,
          "defaults": { "maxReviewCycles": 4 },
          "agents": {
            "blog-writer": { "maxReviewCycles": 2 },
            "programmer": { "riskClass": "code-write" }
          }
        }
        """;

        Assert.True(RepairLoop.TryReadMaxCycles(manifest, ["blog-writer"], out var writer));
        Assert.Equal(2, writer);

        // Listed but silent: the defaults answer, not the agent.
        Assert.True(RepairLoop.TryReadMaxCycles(manifest, ["programmer"], out var programmer));
        Assert.Equal(4, programmer);

        // Not listed at all: still the defaults.
        Assert.True(RepairLoop.TryReadMaxCycles(manifest, ["nobody"], out var unknown));
        Assert.Equal(4, unknown);

        // The first agent that actually declares a cap wins: a listed-but-silent assignee hands
        // the question to the next candidate rather than short-circuiting to the defaults.
        Assert.True(RepairLoop.TryReadMaxCycles(manifest, ["programmer", "blog-writer"], out var order));
        Assert.Equal(2, order);

        // A manifest that cannot be parsed is not "no opinion" — the caller escalates on it.
        Assert.False(RepairLoop.TryReadMaxCycles("{not json", ["blog-writer"], out _));
        Assert.True(RepairLoop.TryReadMaxCycles("""{"defaults":{}}""", ["blog-writer"], out var silent));
        Assert.Null(silent);
    }

    [Fact]
    public void The_shipped_template_contract_declares_a_cap_for_the_gated_writers()
    {
        var manifest = File.ReadAllText(Path.Combine(RepositoryRoot, "ProjectTemplate", "Agents", "contracts.json"));

        Assert.True(RepairLoop.TryReadMaxCycles(manifest, [Producer], out var cycles));
        Assert.NotNull(cycles);
        Assert.True(cycles > 0, "blog-writer must declare a positive maxReviewCycles");
    }

    // ── Condition ───────────────────────────────────────────────────────────

    [Fact]
    public void The_two_arms_of_the_loop_are_mutually_exclusive()
    {
        var withinCap = new RepairBudgetConditionSpec { Mode = "withinCap" };
        var exhausted = new RepairBudgetConditionSpec { Mode = "exhausted" };

        var open = RepairLoop.Resolve(Comments(Fix("one")), maxCycles: 2);
        Assert.True(ConditionEvaluators.RepairBudget(withinCap, open));
        Assert.False(ConditionEvaluators.RepairBudget(exhausted, open));

        var spent = RepairLoop.Resolve(Comments(Fix("one"), Fix("two")), maxCycles: 2);
        Assert.False(ConditionEvaluators.RepairBudget(withinCap, spent));
        Assert.True(ConditionEvaluators.RepairBudget(exhausted, spent));

        // A cap of zero means "never repair, always escalate".
        var noBudget = RepairLoop.Resolve(Comments(Fix("one")), maxCycles: 0);
        Assert.True(ConditionEvaluators.RepairBudget(exhausted, noBudget));

        // Unknown mode matches neither arm: the ticket stalls visibly instead of looping.
        var typo = new RepairBudgetConditionSpec { Mode = "within-cap" };
        Assert.False(ConditionEvaluators.RepairBudget(typo, open));
        Assert.False(ConditionEvaluators.RepairBudget(typo, spent));

        // Unresolvable budget escalates rather than re-dispatching forever.
        Assert.False(ConditionEvaluators.RepairBudget(withinCap, null));
        Assert.True(ConditionEvaluators.RepairBudget(exhausted, null));
    }

    [Fact]
    public void Condition_round_trips_through_the_automation_config_serializer()
    {
        var json = """
        {
          "automations": [{
            "id": "blog-repair-escalate",
            "trigger": { "type": "ticketInColumn", "columns": ["Review"] },
            "conditions": [
              { "type": "verdictIs", "verdicts": ["FIX"], "agent": "blog-reviewer" },
              { "type": "repairBudget", "mode": "exhausted", "agent": "blog-reviewer", "maxCycles": 2 }
            ],
            "actions": [
              { "type": "addComment", "content": "{verdictHistory}", "author": "automation" },
              { "type": "moveTicketStatus", "to": "Blocked" }
            ]
          }]
        }
        """;

        var config = JsonSerializer.Deserialize<AutomationConfig>(json, AutomationStore.JsonOptions)!;
        var reloaded = JsonSerializer.Deserialize<AutomationConfig>(
            JsonSerializer.Serialize(config, AutomationStore.JsonOptions), AutomationStore.JsonOptions)!;

        var condition = Assert.IsType<RepairBudgetConditionSpec>(reloaded.Automations[0].Conditions[1]);
        Assert.Equal("exhausted", condition.Mode);
        Assert.Equal("blog-reviewer", condition.Agent);
        Assert.Equal(2, condition.MaxCycles);
    }

    // ── Integration: the loop over a real project ───────────────────────────

    [Fact]
    public async Task Fix_then_fix_then_ship_advances_inside_the_cap()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("repair-happy", maxReviewCycles: 2);

        var repair = LoopAutomation("repair", "withinCap");
        var escalate = LoopAutomation("escalate", "exhausted");
        var firing = Harness.Firing(ticket.Id);

        // Round one: the reviewer refuses, the loop still has budget.
        await harness.CommentAsync(ticket.Id, Fix("cite the numbers", "cite-your-sources").Content);
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, repair, firing));
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, escalate, firing));

        // The re-dispatch carries the reviewer's findings, not just the ticket description.
        var context = await harness.Executor.ComposeDispatchContextAsync(runtime, ticket.Id, "fix it");
        Assert.Contains("Repair round 1 of 2", context!, StringComparison.Ordinal);
        Assert.Contains("cite-your-sources", context, StringComparison.Ordinal);
        Assert.Contains("Evidence 4/10", context, StringComparison.Ordinal);
        Assert.EndsWith("fix it", context, StringComparison.Ordinal);

        // The producer repairs and the reviewer ships on the second look: inside the cap, so the
        // ticket advances instead of escalating, and the repair brief stops being injected.
        await harness.CommentAsync(ticket.Id, Ship("numbers cited").Content);
        var advance = LoopAutomation("advance", "withinCap");
        advance.Conditions = [new VerdictIsConditionSpec { Verdicts = ["SHIP"], RequireFreshArtifact = false }];

        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, advance, firing));
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, repair, firing));
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, escalate, firing));
        Assert.Equal("fix it", await harness.Executor.ComposeDispatchContextAsync(runtime, ticket.Id, "fix it"));
    }

    [Fact]
    public async Task The_cap_escalates_with_every_rounds_reasons_on_the_ticket()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("repair-escalation", maxReviewCycles: 2);

        var repair = LoopAutomation("repair", "withinCap");
        var escalate = LoopAutomation("escalate", "exhausted");
        escalate.Actions =
        [
            new AddCommentActionSpec { Content = "{verdictHistory}", Author = "automation" },
            new MoveTicketStatusActionSpec { To = "Blocked" },
        ];
        var firing = Harness.Firing(ticket.Id);

        await harness.CommentAsync(ticket.Id, Fix("round one", "cite-your-sources").Content);
        await harness.CommentAsync(ticket.Id, Fix("round two", "unverified-quote").Content);

        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, repair, firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, escalate, firing));

        await harness.Executor.ExecuteAutomationAsync(runtime, escalate, firing, CancellationToken.None);

        var after = (await harness.Tickets.GetTicketAsync(runtime.Slug, ticket.Id))!;
        Assert.Equal("Blocked", after.Status);

        var escalation = after.Comments.Last().Content;
        Assert.StartsWith($"GIGACLAW-REPAIR v1 ticket-{ticket.Id} escalated 2/2", escalation, StringComparison.Ordinal);
        // Both rounds, with their reasons — the owner never has to open a run log.
        Assert.Contains("### Round 1/2", escalation, StringComparison.Ordinal);
        Assert.Contains("cite-your-sources", escalation, StringComparison.Ordinal);
        Assert.Contains("### Round 2/2", escalation, StringComparison.Ordinal);
        Assert.Contains("unverified-quote", escalation, StringComparison.Ordinal);
        Assert.Contains("Evidence 4/10 — no primary sources", escalation, StringComparison.Ordinal);
        Assert.DoesNotContain("{verdictHistory}", escalation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rebuilt_executor_resumes_the_count_instead_of_restarting_it()
    {
        using var tmp = new TempDir();
        var repair = LoopAutomation("repair", "withinCap");
        var escalate = LoopAutomation("escalate", "exhausted");

        var first = new Harness(tmp.Path);
        var (runtime, ticket) = await first.SeedAsync("repair-restart", maxReviewCycles: 2);
        var slug = runtime.Slug;
        var ticketId = ticket.Id;

        await first.CommentAsync(ticketId, Fix("round one", "cite-your-sources").Content);
        await first.CommentAsync(ticketId, Fix("round two", "unverified-quote").Content);
        Assert.True(await first.Executor.ConditionsMatchAsync(runtime, escalate, Harness.Firing(ticketId)));

        // Engine restart: brand-new services, registries and executor over the same data directory.
        // Nothing is carried over in memory — the count is re-derived from the ticket itself.
        var second = new Harness(tmp.Path);
        var reborn = await second.AttachAsync(slug);
        var firing = Harness.Firing(ticketId);

        Assert.False(await second.Executor.ConditionsMatchAsync(reborn, repair, firing));
        Assert.True(await second.Executor.ConditionsMatchAsync(reborn, escalate, firing));
    }

    [Fact]
    public async Task An_unreadable_contract_manifest_escalates_instead_of_looping()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("repair-broken-contract", maxReviewCycles: 2);
        await File.WriteAllTextAsync(
            Path.Combine(runtime.Workspace!, ".agents", "contracts.json"), "{ not json");

        await harness.CommentAsync(ticket.Id, Fix("round one", "cite-your-sources").Content);
        var firing = Harness.Firing(ticket.Id);

        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, LoopAutomation("repair", "withinCap"), firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, LoopAutomation("escalate", "exhausted"), firing));
    }

    [Fact]
    public async Task The_loops_cost_still_lands_on_the_ticket_badge()
    {
        // Regression guard only: the repair loop dispatches through the same runAgent path, so the
        // existing CostTracker accounting must keep accumulating across rounds.
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("repair-cost", maxReviewCycles: 2);

        await harness.Tickets.AddAgentUsageAsync(runtime.Slug, ticket.Id, tokens: 1200, costUsd: 0.42);
        await harness.Tickets.AddAgentUsageAsync(runtime.Slug, ticket.Id, tokens: 900, costUsd: 0.31);

        var after = (await harness.Tickets.GetTicketAsync(runtime.Slug, ticket.Id))!;
        Assert.Equal(2100, after.AgentTokens);
        Assert.Equal(0.73, after.AgentCostUsd, 3);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static GigaClaw.Core.Automation.Automation LoopAutomation(string id, string mode) => new()
    {
        Id = id,
        Trigger = new TicketInColumnTriggerSpec { Columns = ["Review"] },
        Conditions =
        [
            // requireFreshArtifact is off: these verdicts judge a digest, not a file on disk.
            new VerdictIsConditionSpec { Verdicts = ["FIX"], RequireFreshArtifact = false },
            new RepairBudgetConditionSpec { Mode = mode },
        ],
        Actions = [new AddCommentActionSpec { Content = "noted", Author = "automation" }],
    };

    private static string Digest(string seed)
        => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

    private static VerdictComment Ship(string summary, string agent = Reviewer)
        => Verdict("SHIP", summary, agent, null, Digest(summary));

    private static VerdictComment Block(string summary, string veto, string agent = Reviewer)
        => Verdict("BLOCK", summary, agent, veto, Digest(summary));

    private static VerdictComment Fix(
        string summary, string veto = "cite-your-sources", string agent = Reviewer, string? digest = null)
        => Verdict("FIX", summary, agent, veto, digest ?? Digest(summary));

    private static int _clock;

    private static VerdictComment Verdict(string decision, string summary, string agent, string? veto, string digest)
    {
        var vetoItems = decision == "SHIP" || veto is null
            ? "[]"
            : $$"""[{ "code": "{{veto}}", "statement": "{{summary}}" }]""";
        var score = decision == "SHIP" ? 10 : 4;
        var body = $$"""
        {
          "schemaVersion": 1,
          "agent": "{{agent}}",
          "ticketId": 1,
          "verdict": "{{decision}}",
          "summary": "{{summary}}",
          "categories": [{ "name": "Evidence", "score": {{score}}, "max": 10, "notes": "no primary sources" }],
          "vetoItems": {{vetoItems}},
          "evidence": [{ "kind": "hash", "ref": "{{digest}}" }],
          "reviewedAtUtc": "2026-07-30T12:00:00Z",
          "inputDigest": "{{digest}}"
        }
        """;

        return new VerdictComment(
            $"## Review\n\n{summary}\n\nGIGACLAW-VERDICT v1 {agent} {decision} artifact-{digest}\n\n```json\n{body}\n```\n",
            agent,
            new DateTime(2026, 7, 30, 8, 0, 0, DateTimeKind.Utc).AddSeconds(Interlocked.Increment(ref _clock)));
    }

    private static List<VerdictComment> Comments(params VerdictComment[] comments) => [.. comments];

    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "GigaClaw.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate GigaClaw repository root.");
        }
    }

    /// <summary>
    /// A real <see cref="ActionExecutor"/> over a temp project, same shape as the C6 harness.
    /// The data root is a parameter so a second harness can be built over the first one's
    /// directory — that is how the restart test proves the count is not held in memory.
    /// </summary>
    private sealed class Harness
    {
        public ProjectService Projects { get; }
        public TicketService Tickets { get; }
        public MemberService Members { get; }
        public ActionExecutor Executor { get; }
        private string _slug = "";

        public Harness(string root)
        {
            Projects = new ProjectService(root);
            var members = Members = new MemberService(Projects);
            Tickets = new TicketService(Projects, members);
            var runs = new AgentRunRegistry();
            var sessions = new SessionRegistry();
            var cost = new CostTracker();
            Executor = new ActionExecutor(
                Tickets, members, new LabelService(Projects), sessions, runs,
                new ClaudeRunner(sessions, runs, new RunConcurrencyGate(1), NullLogger<ClaudeRunner>.Instance),
                cost, new LocalizationService(new AppSettingsService(root)), Projects,
                new RunStateManager(runs, cost, Tickets, NullLogger.Instance),
                FakeHttpClientFactory.Unused, NullLogger.Instance);
        }

        public async Task<(ProjectRuntime Runtime, GigaClaw.Core.Models.Ticket Ticket)> SeedAsync(
            string name, int maxReviewCycles)
        {
            var project = await Projects.CreateProjectAsync(name);
            _slug = project.Slug;
            var workspace = Projects.ResolveWorkspacePath(project);
            Directory.CreateDirectory(Path.Combine(workspace, ".agents"));
            // The cap is declared for the producing agent, and the defaults block deliberately
            // says something else so the test proves which one is read.
            await File.WriteAllTextAsync(
                Path.Combine(workspace, ".agents", "contracts.json"),
                $$"""
                {
                  "version": 1,
                  "defaults": { "maxReviewCycles": 9 },
                  "agents": { "{{Producer}}": { "maxReviewCycles": {{maxReviewCycles}} } }
                }
                """);

            await Members.CreateMemberAsync(project.Slug, Producer);
            await Members.CreateMemberAsync(project.Slug, Reviewer);
            var ticket = await Tickets.CreateTicketAsync(
                project.Slug, "Write the post", status: "Review", assignedTo: Producer);

            return (new ProjectRuntime(project.Slug) { Workspace = workspace, Config = new AutomationConfig() },
                    (await Tickets.GetTicketAsync(project.Slug, ticket.Id))!);
        }

        /// <summary>Re-attaches to an existing project after a simulated engine restart.</summary>
        public async Task<ProjectRuntime> AttachAsync(string slug)
        {
            _slug = slug;
            var project = (await Projects.GetProjectAsync(slug))!;
            return new ProjectRuntime(slug)
            {
                Workspace = Projects.ResolveWorkspacePath(project),
                Config = new AutomationConfig(),
            };
        }

        public Task CommentAsync(int ticketId, string content)
            => Tickets.AddCommentAsync(_slug, ticketId, content, Reviewer);

        public static TriggerFiring Firing(int ticketId)
            => new(ticketId, "Write the post", "Review");
    }
}
