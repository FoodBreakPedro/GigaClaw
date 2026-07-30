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
        var escalate = harness.Automation("verdict-gate-block-escalate");
        var firing = Harness.Firing(ticket.Id);
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, ship, firing));
        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, escalate, firing));

        // Someone edits the approved draft after the review.
        harness.WriteArtifact(runtime, "content/posts/agents.md", "edited after the review\n");

        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, ship, firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, escalate, firing));

        await harness.Executor.ExecuteAutomationAsync(runtime, escalate, firing, CancellationToken.None);
        var after = (await harness.Tickets.GetTicketAsync(runtime.Slug, ticket.Id))!;
        Assert.Equal("Blocked", after.Status);
    }

    // ── The loud-fail modes ─────────────────────────────────────────────────

    /// <summary>The explicit C2 criterion: prose is not a pass.</summary>
    [Fact]
    public async Task A_prose_only_review_blocks_the_ticket_with_a_receipt()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("gate-qa-prose", "programmer");

        await harness.CommentAsync(ticket.Id, "PASS — looks good to me, 93/100. Shipping it.", "qa-tester");

        var ship = harness.Automation("verdict-gate-qa-ship-to-done");
        var escalate = harness.Automation("verdict-gate-qa-block-escalate");
        var firing = Harness.Firing(ticket.Id);

        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, ship, firing));
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, escalate, firing));

        await harness.Executor.ExecuteAutomationAsync(runtime, escalate, firing, CancellationToken.None);

        var after = (await harness.Tickets.GetTicketAsync(runtime.Slug, ticket.Id))!;
        Assert.Equal("Blocked", after.Status);
        var receipt = after.Comments.Last();
        Assert.Equal("automation", receipt.Author);
        Assert.StartsWith($"GIGACLAW-GATE v1 ticket-{ticket.Id} blocked", receipt.Content, StringComparison.Ordinal);
        Assert.Contains("MISSING", receipt.Content, StringComparison.Ordinal);
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
            runtime, harness.Automation("verdict-gate-qa-block-escalate"), firing));
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
            runtime, harness.Automation("verdict-gate-qa-block-escalate"), firing));
    }

    // ── FIX and the bounded repair loop ─────────────────────────────────────

    [Fact]
    public async Task A_fix_verdict_repairs_within_the_cap_then_escalates_with_the_whole_history()
    {
        using var tmp = new TempDir();
        var harness = new Harness(tmp.Path);
        var (runtime, ticket) = await harness.SeedAsync("gate-qa-repair", "programmer");

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

        var after = (await harness.Tickets.GetTicketAsync(runtime.Slug, ticket.Id))!;
        Assert.Equal("Blocked", after.Status);
        var escalation = after.Comments.Last().Content;
        Assert.StartsWith($"GIGACLAW-REPAIR v1 ticket-{ticket.Id} escalated 2/2", escalation, StringComparison.Ordinal);
        Assert.Contains("failing-acceptance-criterion", escalation, StringComparison.Ordinal);
        Assert.Contains("failing-adversarial-test", escalation, StringComparison.Ordinal);
        Assert.DoesNotContain("{verdictHistory}", escalation, StringComparison.Ordinal);
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
        var escalate = harness.Automation("verdict-gate-qa-block-escalate");
        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, escalate, Harness.Firing(ticket.Id)));

        await harness.Tickets.MoveTicketAsync(runtime.Slug, ticket.Id, "Todo", "qa-tester");
        Assert.False(await harness.Executor.ConditionsMatchAsync(
            runtime, escalate, new TriggerFiring(ticket.Id, "Ship the change", "Todo")));
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
        // The qa pipeline reads it as MISSING — no qa-tester verdict exists — and blocks.
        Assert.True(await harness.Executor.ConditionsMatchAsync(
            runtime, harness.Automation("verdict-gate-qa-block-escalate"), firing));
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
            Executor = new ActionExecutor(
                Tickets, members, new LabelService(Projects), sessions, runs,
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
