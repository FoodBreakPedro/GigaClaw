using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Verdicts;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// The host-side half of the v1 verdict contract: reading a verdict off a ticket comment,
/// deciding freshness, and gating on the outcome. Prose from a not-yet-rewritten reviewer must
/// fail loudly (MISSING/INVALID), never look like a pass.
/// </summary>
public class VerdictGateTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string FixturesDir = Path.Combine(RepositoryRoot, "GigaClaw.Core.Tests", "Fixtures", "verdicts");

    private static string Comment(string agent, string decision, string digest, string json)
        => $"## Review\n\nSome prose the reviewer also wrote.\n\nGIGACLAW-VERDICT v1 {agent} {decision} artifact-{digest}\n\n```json\n{json}\n```\n";

    private static string VerdictJson(
        string agent = "qa-tester",
        string decision = "SHIP",
        string? digest = null,
        string evidencePath = "artifact.md",
        string vetoItems = "[]")
    {
        digest ??= "sha256:" + new string('a', 64);
        return $$"""
        {
          "schemaVersion": 1,
          "agent": "{{agent}}",
          "ticketId": 1,
          "verdict": "{{decision}}",
          "categories": [{ "name": "Acceptance criteria", "score": {{(decision == "SHIP" ? 10 : 4)}}, "max": 10 }],
          "vetoItems": {{vetoItems}},
          "evidence": [{ "kind": "path", "ref": "{{evidencePath}}" }],
          "reviewedAtUtc": "2026-07-30T12:00:00Z",
          "inputDigest": "{{digest}}"
        }
        """;
    }

    private static List<VerdictComment> Comments(params string[] bodies)
        => bodies.Select((body, index) => new VerdictComment(body, "qa-tester", new DateTime(2026, 7, 30, 8, index, 0, DateTimeKind.Utc))).ToList();

    [Fact]
    public void Prose_only_review_reads_as_missing_not_as_a_pass()
    {
        var resolution = VerdictScanner.Resolve(Comments("APPROVE — score 93/100. Looks good to me."));

        Assert.Equal(VerdictOutcome.Missing, resolution.Outcome);
        Assert.False(ConditionEvaluators.VerdictIs(new VerdictIsConditionSpec(), resolution.Outcome));
        Assert.True(ConditionEvaluators.VerdictIs(
            new VerdictIsConditionSpec { Verdicts = ["BLOCK", "INVALID", "STALE", "MISSING"] }, resolution.Outcome));
    }

    [Fact]
    public void Valid_verdict_resolves_to_its_decision()
    {
        var digest = "sha256:" + new string('a', 64);
        foreach (var (decision, expected) in new[] { ("SHIP", VerdictOutcome.Ship), ("FIX", VerdictOutcome.Fix), ("BLOCK", VerdictOutcome.Block) })
        {
            var resolution = VerdictScanner.Resolve(
                Comments(Comment("qa-tester", decision, digest, VerdictJson(decision: decision, digest: digest))));

            Assert.Equal(expected, resolution.Outcome);
            Assert.Equal("qa-tester", resolution.Verdict!.Agent);
        }
    }

    [Fact]
    public void Contract_violations_resolve_to_invalid()
    {
        var digest = "sha256:" + new string('a', 64);

        // SHIP carrying a veto item is a self-contradiction.
        var contradiction = VerdictScanner.Resolve(Comments(Comment(
            "qa-tester", "SHIP", digest,
            VerdictJson(digest: digest, vetoItems: """[{ "code": "failing-e2e", "statement": "Login E2E fails." }]"""))));
        Assert.Equal(VerdictOutcome.Invalid, contradiction.Outcome);

        // Marker and body disagreeing is not a verdict.
        var mismatch = VerdictScanner.Resolve(Comments(Comment(
            "qa-tester", "SHIP", digest, VerdictJson(decision: "FIX", digest: digest))));
        Assert.Equal(VerdictOutcome.Invalid, mismatch.Outcome);
        Assert.Contains("marker line says", mismatch.Diagnostic!, StringComparison.Ordinal);

        // Truncated JSON must not be read as "no verdict yet".
        var truncated = VerdictScanner.Resolve(Comments(
            $"GIGACLAW-VERDICT v1 qa-tester SHIP artifact-{digest}\n\n```json\n{{ \"schemaVersion\": 1,\n```\n"));
        Assert.Equal(VerdictOutcome.Invalid, truncated.Outcome);
    }

    [Fact]
    public void Newest_verdict_wins_and_another_reviewer_never_shadows_it()
    {
        var digest = "sha256:" + new string('a', 64);
        var comments = Comments(
            Comment("qa-tester", "FIX", digest, VerdictJson(decision: "FIX", digest: digest)),
            Comment("qa-tester", "SHIP", digest, VerdictJson(digest: digest)),
            Comment("ui-auditor", "FIX", digest, VerdictJson(agent: "ui-auditor", decision: "FIX", digest: digest)));

        Assert.Equal(VerdictOutcome.Fix, VerdictScanner.Resolve(comments).Outcome);
        Assert.Equal(VerdictOutcome.Ship, VerdictScanner.Resolve(comments, agent: "qa-tester").Outcome);
        Assert.Equal(VerdictOutcome.Missing, VerdictScanner.Resolve(comments, agent: "blog-reviewer").Outcome);
    }

    [Fact]
    public void Verdict_goes_stale_when_the_reviewed_artifact_changes()
    {
        var workspace = Directory.CreateTempSubdirectory("gigaclaw-verdict-");
        try
        {
            var artifact = Path.Combine(workspace.FullName, "artifact.md");
            File.WriteAllText(artifact, "reviewed content\n");
            var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(artifact)));

            var comments = Comments(Comment("qa-tester", "SHIP", digest, VerdictJson(digest: digest)));
            Func<AgentVerdict, (bool, string?)> freshness =
                verdict => (VerdictReader.IsFresh(verdict, workspace.FullName, out var reason), reason);

            Assert.Equal(VerdictOutcome.Ship, VerdictScanner.Resolve(comments, freshness: freshness).Outcome);

            File.WriteAllText(artifact, "edited after the review\n");
            var stale = VerdictScanner.Resolve(comments, freshness: freshness);

            Assert.Equal(VerdictOutcome.Stale, stale.Outcome);
            Assert.True(ConditionEvaluators.VerdictIs(
                new VerdictIsConditionSpec { Verdicts = ["STALE"] }, stale.Outcome));
            Assert.False(ConditionEvaluators.VerdictIs(new VerdictIsConditionSpec(), stale.Outcome));
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public void Freshness_fails_closed_when_the_artifact_cannot_be_rehashed()
    {
        var digest = "sha256:" + new string('a', 64);
        var verdict = JsonSerializer.Deserialize<JsonElement>(VerdictJson(digest: digest));
        Assert.True(VerdictReader.TryRead(verdict, out var parsed, out _));

        Assert.False(VerdictReader.IsFresh(parsed!, workspacePath: null, out var noWorkspace));
        Assert.Contains("workspace", noWorkspace!, StringComparison.Ordinal);

        var empty = Directory.CreateTempSubdirectory("gigaclaw-verdict-empty-");
        try
        {
            // The evidence file does not exist: unverifiable is stale, never fresh.
            Assert.False(VerdictReader.IsFresh(parsed!, empty.FullName, out _));
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void Unknown_outcome_names_never_open_the_gate()
    {
        var spec = new VerdictIsConditionSpec { Verdicts = ["APPROVE", "ship"] };

        Assert.True(ConditionEvaluators.VerdictIs(spec, VerdictOutcome.Ship));   // case-insensitive match
        Assert.False(ConditionEvaluators.VerdictIs(spec, VerdictOutcome.Block)); // "APPROVE" matches nothing
        Assert.False(ConditionEvaluators.VerdictIs(new VerdictIsConditionSpec { Verdicts = [] }, VerdictOutcome.Ship));
    }

    [Fact]
    public void Condition_round_trips_through_the_automation_config_serializer()
    {
        var json = """
        {
          "automations": [{
            "id": "blog-review-gate",
            "trigger": { "type": "ticketInColumn", "columns": ["Review"] },
            "conditions": [{ "type": "verdictIs", "verdicts": ["SHIP"], "agent": "blog-reviewer" }],
            "actions": [{ "type": "moveTicketStatus", "to": "Done" }]
          }]
        }
        """;

        var config = JsonSerializer.Deserialize<AutomationConfig>(json, AutomationStore.JsonOptions)!;
        var condition = Assert.IsType<VerdictIsConditionSpec>(Assert.Single(config.Automations[0].Conditions));

        Assert.Equal(["SHIP"], condition.Verdicts);
        Assert.Equal("blog-reviewer", condition.Agent);
        Assert.True(condition.RequireFreshArtifact);

        // Saving the board must not lose the condition: round-trip it back through the store options.
        var reserialized = JsonSerializer.Serialize(config, AutomationStore.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<AutomationConfig>(reserialized, AutomationStore.JsonOptions)!;
        var again = Assert.IsType<VerdictIsConditionSpec>(Assert.Single(reloaded.Automations[0].Conditions));
        Assert.Equal(["SHIP"], again.Verdicts);
        Assert.Equal("blog-reviewer", again.Agent);
    }

    [Fact]
    public void Host_reader_and_shipped_validator_agree_on_every_fixture()
    {
        foreach (var fixture in Directory.GetFiles(Path.Combine(FixturesDir, "valid"), "*.json"))
        {
            var element = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(fixture));
            Assert.True(
                VerdictReader.TryRead(element, out _, out var error),
                $"{Path.GetFileName(fixture)} is accepted by verdict_contract.py but rejected here: {error}");
        }

        foreach (var fixture in Directory.GetFiles(Path.Combine(FixturesDir, "invalid"), "*.json"))
        {
            var element = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(fixture));
            Assert.False(
                VerdictReader.TryRead(element, out _, out _),
                $"{Path.GetFileName(fixture)} is rejected by verdict_contract.py but accepted here");
        }
    }

    private static string FindRepositoryRoot()
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
