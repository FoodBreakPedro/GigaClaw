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
/// C2 end to end: the automations shipped in <c>ProjectTemplate/Agents/automations.json</c> run
/// against a real project through a real <see cref="ActionExecutor"/>. These tests exercise the
/// file the template actually ships — not a hand-built fixture — so a wiring edit that breaks the
/// gate fails here rather than in a workspace.
/// </summary>
public class TemplateVerdictGateTests
{
    // ── SHIP ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_qa_ship_verdict_advances_the_ticket_out_of_review()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("gate-qa-ship", "programmer");

        await harness.CommentAsync(ticket.Id, Verdict("qa-tester", "SHIP"), "qa-tester");
        var gate = harness.Automation("verdict-gate-qa-ship-to-done");

        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, gate, Harness.Firing(ticket.Id)));
        await harness.Executor.ExecuteAutomationAsync(runtime, gate, Harness.Firing(ticket.Id), CancellationToken.None);

        var after = (await harness.Tickets.GetTicketAsync(runtime.Slug, ticket.Id))!;
        Assert.Equal("Done", after.Status);
        Assert.Contains(after.Comments, c => c.Content.StartsWith("GIGACLAW-GATE v1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_blog_ship_verdict_hands_the_draft_to_blog_seo()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("gate-blog-ship", "blog-writer", "blog-seo");

        var digest = harness.WriteArtifact(runtime, "content/posts/agents.md", "the reviewed draft\n");
        await harness.CommentAsync(
            ticket.Id,
            Verdict("blog-reviewer", "SHIP", digest, path: "content/posts/agents.md"),
            "blog-reviewer");

        var gate = harness.Automation("verdict-gate-blog-ship-to-seo");
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, gate, Harness.Firing(ticket.Id)));
        await harness.Executor.ExecuteAutomationAsync(runtime, gate, Harness.Firing(ticket.Id), CancellationToken.None);

        var after = (await harness.Tickets.GetTicketAsync(runtime.Slug, ticket.Id))!;
        Assert.Equal("Todo", after.Status);
        Assert.Equal("blog-seo", after.AssignedTo);
    }

    /// <summary>
    /// The reason `requireFreshArtifact` exists: an approval that judged bytes which have since
    /// changed must not be replayed as a pass, and the same edit must route the ticket to the
    /// escalation arm rather than leaving it silently stuck.
    /// </summary>
    [Fact]
    public async Task A_stale_approval_is_refused_and_escalated_instead()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("gate-blog-stale", "blog-writer", "blog-seo");

        var digest = harness.WriteArtifact(runtime, "content/posts/agents.md", "the reviewed draft\n");
        await harness.CommentAsync(
            ticket.Id,
            Verdict("blog-reviewer", "SHIP", digest, path: "content/posts/agents.md"),
            "blog-reviewer");

        var ship = harness.Automation("verdict-gate-blog-ship-to-seo");
        var retry = harness.Automation("verdict-gate-blog-reviewer-retry");
        // Per-reviewer arm, not a shared one: another reviewer's receipts must never exhaust
        // blog-reviewer's own first attempt on the same ticket.
        var exhausted = harness.Automation("verdict-gate-blog-reviewer-retry-exhausted");
        var firing = Harness.Firing(ticket.Id);
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, ship, firing));
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, retry, firing));

        // Someone edits the approved draft after the review.
        harness.WriteArtifact(runtime, "content/posts/agents.md", "edited after the review\n");

        // Phase 1.2 (return-to-sender): a stale judgement is the reviewer's problem, so the first
        // firing asks blog-reviewer for a fresh review rather than parking the ticket on the owner.
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, ship, firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, retry, firing));
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, exhausted, firing));

        // Spending the retry (the receipt the retry arm posts) swaps the arms over.
        await harness.CommentAsync(
            ticket.Id, ReviewerRetry.RenderReceipt(ticket.Id, "blog-reviewer"), "automation");
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, retry, firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, exhausted, firing));

        await harness.Executor.ExecuteAutomationAsync(runtime, exhausted, firing, CancellationToken.None);
        var after = (await harness.Tickets.GetTicketAsync(runtime.Slug, ticket.Id))!;
        Assert.Equal("Blocked", after.Status);
        Assert.Equal("owner", after.AssignedTo);
    }

    // ── The loud-fail modes ─────────────────────────────────────────────────

    /// <summary>
    /// The explicit C2 criterion: prose is not a pass. Phase 1.2 puts one bounded reviewer retry in
    /// front of the block — the reviewer, not the author, is the one that failed to report — and
    /// this walks the whole path: re-review once, then block with a receipt and an owner.
    /// </summary>
    [Fact]
    public async Task A_prose_only_review_is_re_reviewed_once_then_blocks_with_a_receipt()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("gate-qa-prose", "programmer", "qa-tester");

        await harness.CommentAsync(ticket.Id, "PASS — looks good to me, 93/100. Shipping it.", "qa-tester");

        var ship = harness.Automation("verdict-gate-qa-ship-to-done");
        var retry = harness.Automation("verdict-gate-qa-reviewer-retry");
        var exhausted = harness.Automation("verdict-gate-qa-reviewer-retry-exhausted");
        var block = harness.Automation("verdict-gate-qa-block-escalate");
        var firing = Harness.Firing(ticket.Id);

        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, ship, firing));
        // A MISSING verdict is not a deliberate BLOCK: the immediate-escalation arm stands down.
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, block, firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, retry, firing));
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, exhausted, firing));

        // The retry receipt is what spends the budget, and it is recounted from the ticket.
        await harness.CommentAsync(
            ticket.Id, ReviewerRetry.RenderReceipt(ticket.Id, "qa-tester"), "automation");
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, retry, firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, exhausted, firing));

        await harness.Executor.ExecuteAutomationAsync(runtime, exhausted, firing, CancellationToken.None);

        var after = (await harness.Tickets.GetTicketAsync(runtime.Slug, ticket.Id))!;
        Assert.Equal("Blocked", after.Status);
        Assert.Equal("owner", after.AssignedTo);
        var receipt = after.Comments.Last();
        Assert.Equal("automation", receipt.Author);
        Assert.StartsWith($"GIGACLAW-GATE v1 ticket-{ticket.Id} blocked", receipt.Content, StringComparison.Ordinal);
        Assert.Contains("MISSING", receipt.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The blocking receipt closes the episode, so an owner who unblocks the ticket hands the
    /// reviewer a fresh retry rather than a permanently spent one — the same contract the repair
    /// loop keeps for its own escalation receipt.
    /// </summary>
    [Fact]
    public async Task A_block_receipt_hands_the_reviewer_a_fresh_retry_budget()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("gate-qa-retry-reset", "programmer", "qa-tester");

        await harness.CommentAsync(ticket.Id, "no verdict here, just prose", "qa-tester");
        var retry = harness.Automation("verdict-gate-qa-reviewer-retry");
        var exhausted = harness.Automation("verdict-gate-qa-reviewer-retry-exhausted");
        var firing = Harness.Firing(ticket.Id);

        await harness.CommentAsync(
            ticket.Id, ReviewerRetry.RenderReceipt(ticket.Id, "qa-tester"), "automation");
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, exhausted, firing));

        // The owner hands the ticket back: same column, same producer, still no usable verdict.
        await harness.Executor.ExecuteAutomationAsync(runtime, exhausted, firing, CancellationToken.None);
        await harness.Tickets.MoveTicketAsync(runtime.Slug, ticket.Id, "Review", "owner");
        await harness.Tickets.UpdateTicketAsync(runtime.Slug, ticket.Id, assignedTo: "programmer", author: "owner");

        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, retry, firing));
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, exhausted, firing));
    }

    [Fact]
    public async Task A_verdict_that_breaks_the_contract_blocks_instead_of_passing()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("gate-qa-invalid", "programmer");

        // SHIP carrying a veto item is a self-contradiction: INVALID, never a pass.
        await harness.CommentAsync(
            ticket.Id,
            Verdict("qa-tester", "SHIP", veto: "failing-acceptance-criterion"),
            "qa-tester");

        var firing = Harness.Firing(ticket.Id);
        Assert.False(await harness.Executor.ConditionsMatchAsync(
            runtime, harness.Automation("verdict-gate-qa-ship-to-done"), firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(
            runtime, harness.Automation("verdict-gate-qa-reviewer-retry"), firing));
    }

    /// <summary>A verdict the marker line disagrees with is not a verdict.</summary>
    [Fact]
    public async Task A_marker_that_contradicts_its_body_never_opens_the_gate()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("gate-qa-mismatch", "programmer");

        var body = Verdict("qa-tester", "FIX", veto: "failing-acceptance-criterion");
        var forged = body.Replace("qa-tester FIX artifact-", "qa-tester SHIP artifact-", StringComparison.Ordinal);
        await harness.CommentAsync(ticket.Id, forged, "qa-tester");

        var firing = Harness.Firing(ticket.Id);
        Assert.False(await harness.Executor.ConditionsMatchAsync(
            runtime, harness.Automation("verdict-gate-qa-ship-to-done"), firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(
            runtime, harness.Automation("verdict-gate-qa-reviewer-retry"), firing));
    }

    // ── FIX and the bounded repair loop ─────────────────────────────────────

    [Fact]
    public async Task A_fix_verdict_repairs_within_the_cap_then_escalates_with_the_whole_history()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("gate-qa-repair", "programmer", "groomer");

        var repair = harness.Automation("verdict-gate-qa-repair-round");
        var escalate = harness.Automation("verdict-gate-qa-repair-exhausted");
        var firing = Harness.Firing(ticket.Id);

        // Round one: the budget (maxReviewCycles, 2 by default) still has room.
        await harness.CommentAsync(
            ticket.Id, Verdict("qa-tester", "FIX", seed: "round-one", veto: "failing-acceptance-criterion"), "qa-tester");
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, repair, firing));
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, escalate, firing));

        // The re-dispatch carries what was refused, so the producer is told rather than guessing.
        var context = await harness.Executor.ComposeDispatchContextAsync(runtime, ticket.Id, null);
        Assert.Contains("Repair round 1 of 2", context!, StringComparison.Ordinal);
        Assert.Contains("failing-acceptance-criterion", context, StringComparison.Ordinal);

        // Round two spends the budget: the arms swap over.
        await harness.CommentAsync(
            ticket.Id, Verdict("qa-tester", "FIX", seed: "round-two", veto: "failing-adversarial-test"), "qa-tester");
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, repair, firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, escalate, firing));

        await harness.Executor.ExecuteAutomationAsync(runtime, escalate, firing, CancellationToken.None);

        // Phase 1.3: a spent repair budget re-scopes through the groomer instead of paging the
        // owner — Blocked is reserved for "a human must decide".
        var after = (await harness.Tickets.GetTicketAsync(runtime.Slug, ticket.Id))!;
        Assert.Equal("Backlog", after.Status);
        Assert.Equal("groomer", after.AssignedTo);
        var escalation = after.Comments.Last(c => c.Author == "automation").Content;
        Assert.StartsWith($"GIGACLAW-REPAIR v1 ticket-{ticket.Id} escalated 2/2", escalation, StringComparison.Ordinal);
        Assert.Contains("failing-acceptance-criterion", escalation, StringComparison.Ordinal);
        Assert.Contains("failing-adversarial-test", escalation, StringComparison.Ordinal);
        Assert.DoesNotContain("{verdictHistory}", escalation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Phase 1.6: the `extended-repair` label buys a ticket more repair rounds without an engine
    /// change — the arms are duplicated and the base pair stands down for the label. At round three
    /// (past the contract default of 2) only the extended repair arm may fire: if the base
    /// exhaustion arm also matched, one FIX verdict would both re-dispatch the author and re-scope
    /// the ticket through the groomer.
    /// </summary>
    [Fact]
    public async Task The_extended_repair_label_buys_rounds_the_default_cap_would_have_spent()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("gate-qa-extended", "programmer", "groomer");

        var baseRound = harness.Automation("verdict-gate-qa-repair-round");
        var baseExhausted = harness.Automation("verdict-gate-qa-repair-exhausted");
        var extRound = harness.Automation("verdict-gate-qa-repair-round-extended");
        var extExhausted = harness.Automation("verdict-gate-qa-repair-exhausted-extended");
        var firing = Harness.Firing(ticket.Id);

        foreach (var round in new[] { "round-one", "round-two", "round-three" })
        {
            await harness.CommentAsync(
                ticket.Id, Verdict("qa-tester", "FIX", seed: round, veto: "failing-acceptance-criterion"), "qa-tester");
        }

        // Unlabeled: three FIX rounds against a cap of two is spent, and the extended arms are inert.
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, baseRound, firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, baseExhausted, firing));
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, extRound, firing));
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, extExhausted, firing));

        var label = await harness.Labels.CreateLabelAsync(runtime.Slug, "extended-repair", "#888888");
        await harness.Tickets.PatchTicketLabelsAsync(runtime.Slug, ticket.Id, [label.Id], [], "groomer");

        // Labeled: the base pair stands down entirely and the raised cap still has a round left.
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, baseRound, firing));
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, baseExhausted, firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, extRound, firing));
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, extExhausted, firing));

        await harness.CommentAsync(
            ticket.Id, Verdict("qa-tester", "FIX", seed: "round-four", veto: "failing-adversarial-test"), "qa-tester");
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, extRound, firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, extExhausted, firing));
    }

    /// <summary>
    /// The funded-loop stop, end to end. Exhaustion sends the ticket back to Backlog owned by the
    /// groomer, and that hand-off resets everything that bounds the loop — the trigger's attempt
    /// counter (the groomer edits the ticket) and the repair budget (the escalation receipt closes
    /// the episode). So the first lap has to leave a mark the second lap can see, or a ticket
    /// nobody can specify is re-scoped and re-run forever at real cost. The mark is the `triaged`
    /// label: one automated lap, then a person.
    /// </summary>
    [Fact]
    public async Task A_second_exhaustion_after_triage_goes_to_the_owner_instead_of_the_groomer()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("gate-qa-triage-lap", "programmer", "groomer");

        var firstLap = harness.Automation("verdict-gate-qa-repair-exhausted");
        var secondLap = harness.Automation("verdict-gate-qa-repair-exhausted-triaged");
        var firing = Harness.Firing(ticket.Id);

        foreach (var round in new[] { "round-one", "round-two" })
        {
            await harness.CommentAsync(
                ticket.Id, Verdict("qa-tester", "FIX", seed: round, veto: "failing-acceptance-criterion"), "qa-tester");
        }

        // Lap one: no `triaged` label yet, so the groomer gets its shot and the terminal arm is inert.
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, firstLap, firing));
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, secondLap, firing));

        await harness.Executor.ExecuteAutomationAsync(runtime, firstLap, firing, CancellationToken.None);

        var afterLapOne = (await harness.Tickets.GetTicketAsync(runtime.Slug, ticket.Id))!;
        Assert.Equal("Backlog", afterLapOne.Status);
        Assert.Equal("groomer", afterLapOne.AssignedTo);
        Assert.Contains(afterLapOne.Labels, l => l.Name == "triaged");

        // The groomer re-scopes and sends it round again; it exhausts a second time.
        await harness.Tickets.MoveTicketAsync(runtime.Slug, ticket.Id, "Review", "groomer");
        await harness.Tickets.UpdateTicketAsync(runtime.Slug, ticket.Id, assignedTo: "programmer", author: "groomer");
        foreach (var round in new[] { "round-three", "round-four" })
        {
            await harness.CommentAsync(
                ticket.Id, Verdict("qa-tester", "FIX", seed: round, veto: "failing-adversarial-test"), "qa-tester");
        }

        // Lap two: the label is on, so the arms have swapped — no second funded re-scoping.
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, firstLap, firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, secondLap, firing));

        await harness.Executor.ExecuteAutomationAsync(runtime, secondLap, firing, CancellationToken.None);

        var afterLapTwo = (await harness.Tickets.GetTicketAsync(runtime.Slug, ticket.Id))!;
        Assert.Equal("Blocked", afterLapTwo.Status);
        Assert.Equal("owner", afterLapTwo.AssignedTo);
        var receipt = afterLapTwo.Comments.Last(c => c.Author == "automation").Content;
        Assert.Contains("triaged", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("{verdictHistory}", receipt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate governs exit from Review only. A ticket the reviewer already routed is not
    /// re-litigated, which is what lets the shipped reviewer protocols and the gate coexist.
    /// </summary>
    [Fact]
    public async Task A_ticket_the_reviewer_already_routed_is_left_alone()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("gate-qa-routed", "programmer");

        await harness.CommentAsync(ticket.Id, "no verdict here, just prose", "qa-tester");
        var retry = harness.Automation("verdict-gate-qa-reviewer-retry");
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, retry, Harness.Firing(ticket.Id)));

        await harness.Tickets.MoveTicketAsync(runtime.Slug, ticket.Id, "Todo", "qa-tester");
        Assert.False(await harness.Executor.ConditionsMatchAsync(
            runtime, retry, new TriggerFiring(ticket.Id, "Ship the change", "Todo")));
    }

    /// <summary>
    /// Only the reviewer that owns a pipeline can move it: a verdict from an unrelated agent must
    /// not be able to advance somebody else's ticket.
    /// </summary>
    [Fact]
    public async Task Another_agents_verdict_cannot_open_the_gate()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("gate-qa-foreign", "programmer", "ui-auditor");

        await harness.CommentAsync(ticket.Id, Verdict("ui-auditor", "SHIP"), "ui-auditor");

        var firing = Harness.Firing(ticket.Id);
        Assert.False(await harness.Executor.ConditionsMatchAsync(
            runtime, harness.Automation("verdict-gate-qa-ship-to-done"), firing));
        // The qa pipeline reads it as MISSING — no qa-tester verdict exists — and sends the review
        // back to qa-tester rather than letting another agent's judgement stand in for its own.
        Assert.True(await harness.Executor.ConditionsMatchAsync(
            runtime, harness.Automation("verdict-gate-qa-reviewer-retry"), firing));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static string Verdict(
        string agent,
        string decision,
        string? digest = null,
        string? path = null,
        string? veto = null,
        string seed = "artifact")
    {
        digest ??= "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
        var vetoItems = veto is null
            ? "[]"
            : $$"""[{ "code": "{{veto}}", "statement": "{{veto}} was observed at runtime." }]""";
        var score = decision == "SHIP" && veto is null ? 10 : 4;
        var evidence = path is null
            ? $$"""[{ "kind": "hash", "ref": "{{digest}}" }]"""
            : $$"""[{ "kind": "path", "ref": "{{path}}" }, { "kind": "hash", "ref": "{{digest}}" }]""";

        var body = $$"""
        {
          "schemaVersion": 1,
          "agent": "{{agent}}",
          "ticketId": 1,
          "verdict": "{{decision}}",
          "summary": "{{seed}}",
          "categories": [{ "name": "Acceptance criteria", "score": {{score}}, "max": 10 }],
          "vetoItems": {{vetoItems}},
          "evidence": {{evidence}},
          "reviewedAtUtc": "2026-07-30T12:00:00Z",
          "inputDigest": "{{digest}}"
        }
        """;

        return $"## Review\n\n{seed}\n\nGIGACLAW-VERDICT v1 {agent} {decision} artifact-{digest}\n\n```json\n{body}\n```\n";
    }

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
    /// A real <see cref="ActionExecutor"/> over a temp project seeded with the *shipped* template:
    /// the same automations.json and contracts.json a freshly initialized workspace receives.
    /// </summary>
    private sealed class Harness
    {
        private static readonly string TemplateAgents =
            Path.Combine(RepositoryRoot, "ProjectTemplate", "Agents");

        public ProjectService Projects { get; }
        public TicketService Tickets { get; }
        public MemberService Members { get; }
        public LabelService Labels { get; }
        public ActionExecutor Executor { get; }
        public AutomationConfig Config { get; }
        private string _slug = "";

        public Harness(string root)
        {
            Config = JsonSerializer.Deserialize<AutomationConfig>(
                File.ReadAllText(Path.Combine(TemplateAgents, "automations.json")),
                AutomationStore.JsonOptions)!;

            Projects = new ProjectService(root);
            var members = Members = new MemberService(Projects);
            Tickets = new TicketService(Projects, members);
            var runs = new AgentRunRegistry();
            var sessions = new SessionRegistry();
            var cost = new CostTracker();
            Labels = new LabelService(Projects);
            Executor = new ActionExecutor(
                Tickets, members, Labels, sessions, runs,
                new ClaudeRunner(sessions, runs, new RunConcurrencyGate(1), NullLogger<ClaudeRunner>.Instance),
                cost, new LocalizationService(new AppSettingsService(root)), Projects,
                new RunStateManager(runs, cost, Tickets, NullLogger.Instance),
                FakeHttpClientFactory.Unused, TestTeamRuns.For(Projects, Tickets), NullLogger.Instance);
        }

        public GigaClaw.Core.Automation.Automation Automation(string id)
            => Config.Automations.Single(a => a.Id == id);

        public async Task<(ProjectRuntime Runtime, GigaClaw.Core.Models.Ticket Ticket)> SeedAsync(
            string name, string producer, params string[] extraMembers)
        {
            var project = await Projects.CreateProjectAsync(name);
            _slug = project.Slug;
            var workspace = Projects.ResolveWorkspacePath(project);
            Directory.CreateDirectory(Path.Combine(workspace, ".agents"));
            File.Copy(
                Path.Combine(TemplateAgents, "contracts.json"),
                Path.Combine(workspace, ".agents", "contracts.json"),
                overwrite: true);

            await Members.CreateMemberAsync(project.Slug, producer);
            foreach (var member in extraMembers)
                await Members.CreateMemberAsync(project.Slug, member);

            var ticket = await Tickets.CreateTicketAsync(
                project.Slug, "Ship the change", status: "Review", assignedTo: producer);

            return (new ProjectRuntime(project.Slug) { Workspace = workspace, Config = Config },
                    (await Tickets.GetTicketAsync(project.Slug, ticket.Id))!);
        }

        /// <summary>Writes a workspace file and returns its digest, the way a reviewer computes one.</summary>
        public string WriteArtifact(ProjectRuntime runtime, string relativePath, string content)
        {
            var full = Path.Combine(runtime.Workspace!, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full)));
        }

        public Task CommentAsync(int ticketId, string content, string author)
            => Tickets.AddCommentAsync(_slug, ticketId, content, author);

        public static TriggerFiring Firing(int ticketId)
            => new(ticketId, "Ship the change", "Review");
    }
}
