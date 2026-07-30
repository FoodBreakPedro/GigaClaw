using System.Text.Json;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Handoffs;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// C6: the handoff artifact that carries a run's outputs, assumptions, owned files and open loops
/// to the next agent. Covers the comment transport, the path discipline the lease layer depends
/// on, and the two-hop case the acceptance criteria call for — information surviving the hop.
/// </summary>
public class HandoffArtifactTests
{
    private const string Body = """
    {
      "schemaVersion": 1,
      "agent": "programmer",
      "ticketId": 42,
      "runId": "9f2c1a",
      "summary": "Added the verdict gate condition; reviewer can now run against it.",
      "outputs": [
        { "kind": "path", "ref": "GigaClaw.Core/Automation/ConditionEvaluators.cs", "note": "new condition" }
      ],
      "ownedFiles": ["GigaClaw.Core/Automation/ConditionEvaluators.cs"],
      "assumptions": ["Prose reviewers are handled by the MISSING outcome, not by a new gate."],
      "openLoops": [{ "statement": "automations.json wiring waits on the reviewer rewrites", "blocking": false }],
      "acceptanceCriteria": [
        { "statement": "Condition gates ticket exit", "met": true, "evidenceRef": "GigaClaw.Core/Automation/ConditionEvaluators.cs" },
        { "statement": "Repair loop returns FIX to the producer", "met": false }
      ],
      "nextRole": "qa-tester",
      "producedAtUtc": "2026-07-30T12:00:00Z"
    }
    """;

    private static string Comment(string body = Body, string marker = "GIGACLAW-HANDOFF v1 programmer ticket-42 run-9f2c1a")
        => $"Work done.\n\n{marker}\n\n```json\n{body}\n```\n";

    [Fact]
    public void Handoff_round_trips_through_the_comment_transport()
    {
        Assert.True(HandoffReader.TryRead(Comment(), out var handoff, out var error), error);

        Assert.Equal("programmer", handoff!.Agent);
        Assert.Equal("qa-tester", handoff.NextRole);
        Assert.Equal(["GigaClaw.Core/Automation/ConditionEvaluators.cs"], handoff.OwnedFiles);
        Assert.Single(handoff.OpenLoops);
        Assert.Equal(2, handoff.AcceptanceCriteria.Count);
        Assert.Contains(handoff.AcceptanceCriteria, c => !c.Met);
    }

    [Fact]
    public void Marker_that_disagrees_with_the_body_is_not_a_handoff()
    {
        Assert.False(
            HandoffReader.TryRead(Comment(marker: "GIGACLAW-HANDOFF v1 programmer ticket-43 run-9f2c1a"), out _, out var error));
        Assert.Contains("marker line says", error!, StringComparison.Ordinal);

        Assert.False(HandoffReader.TryRead("no marker here", out _, out var missing));
        Assert.Contains("marker line", missing!, StringComparison.Ordinal);
    }

    [Fact]
    public void Owned_files_that_cannot_be_leased_are_rejected()
    {
        // The lease layer cannot enforce a claim outside the workspace, so it never gets one.
        foreach (var bad in new[] { "/etc/passwd", "../other-repo/file.cs", "~/secrets" })
        {
            var body = Body.Replace("\"GigaClaw.Core/Automation/ConditionEvaluators.cs\"]", $"\"{bad}\"]");
            Assert.False(HandoffReader.TryRead(Comment(body), out _, out var error), $"'{bad}' should be rejected");
            Assert.Contains("workspace-relative", error!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Newest_readable_handoff_wins_and_a_broken_one_is_skipped()
    {
        var older = Comment(Body.Replace("\"runId\": \"9f2c1a\"", "\"runId\": \"aaaaaa\""),
            "GIGACLAW-HANDOFF v1 programmer ticket-42 run-aaaaaa");
        var broken = "GIGACLAW-HANDOFF v1 programmer ticket-42 run-bbbbbb\n\n```json\n{ \"schemaVersion\": 1,\n```\n";

        // A later unreadable handoff must not erase the last good one.
        var latest = HandoffReader.Latest([older, broken]);
        Assert.Equal("aaaaaa", latest!.RunId);

        Assert.Equal("9f2c1a", HandoffReader.Latest([older, Comment()])!.RunId);
        Assert.Null(HandoffReader.Latest(["just a comment"]));
    }

    [Fact]
    public void Rendered_handoff_carries_the_decisions_and_the_unfinished_work()
    {
        HandoffReader.TryRead(Comment(), out var handoff, out _);
        var rendered = HandoffReader.Render(handoff!);

        Assert.Contains("Handoff from programmer", rendered, StringComparison.Ordinal);
        Assert.Contains("Prose reviewers are handled by the MISSING outcome", rendered, StringComparison.Ordinal);
        Assert.Contains("Repair loop returns FIX to the producer", rendered, StringComparison.Ordinal);
        Assert.Contains("automations.json wiring waits", rendered, StringComparison.Ordinal);
        // A criterion already met is not worth prompt budget on the next hop.
        Assert.DoesNotContain("Condition gates ticket exit", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Information_survives_the_hop_to_the_next_agent()
    {
        using var harness = new Harness();
        var project = await harness.Projects.CreateProjectAsync("handoff-hop");
        var ticket = await harness.Tickets.CreateTicketAsync(project.Slug, "Wire the gate", status: "Review");
        var runtime = new ProjectRuntime(project.Slug)
        {
            Workspace = harness.Projects.ResolveWorkspacePath(project),
            Config = new AutomationConfig(),
        };

        // Before the producing agent posts anything, dispatch context is just the action's own.
        Assert.Equal(
            "review it",
            await harness.Executor.ComposeDispatchContextAsync(runtime, ticket.Id, "review it"));

        await harness.Tickets.AddCommentAsync(project.Slug, ticket.Id, Comment(), "programmer");

        var composed = await harness.Executor.ComposeDispatchContextAsync(runtime, ticket.Id, "review it");

        Assert.Contains("Handoff from programmer", composed!, StringComparison.Ordinal);
        Assert.Contains("Repair loop returns FIX to the producer", composed, StringComparison.Ordinal);
        Assert.EndsWith("review it", composed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ticketless_dispatch_and_unreadable_handoffs_leave_context_untouched()
    {
        using var harness = new Harness();
        var project = await harness.Projects.CreateProjectAsync("handoff-noop");
        var ticket = await harness.Tickets.CreateTicketAsync(project.Slug, "No handoff here");
        var runtime = new ProjectRuntime(project.Slug)
        {
            Workspace = harness.Projects.ResolveWorkspacePath(project),
            Config = new AutomationConfig(),
        };

        Assert.Equal("ctx", await harness.Executor.ComposeDispatchContextAsync(runtime, null, "ctx"));

        await harness.Tickets.AddCommentAsync(
            project.Slug, ticket.Id,
            "GIGACLAW-HANDOFF v1 programmer ticket-1 run-x\n\n```json\n{ broken\n```\n", "programmer");

        Assert.Equal("ctx", await harness.Executor.ComposeDispatchContextAsync(runtime, ticket.Id, "ctx"));
    }

    [Fact]
    public void Host_reader_agrees_with_the_shipped_schema_on_the_worked_example()
    {
        var element = JsonSerializer.Deserialize<JsonElement>(Body);
        Assert.True(HandoffReader.TryRead(element, out _, out var error), error);
    }

    private sealed class Harness : IDisposable
    {
        public TempDir Tmp { get; } = new();
        public ProjectService Projects { get; }
        public TicketService Tickets { get; }
        public ActionExecutor Executor { get; }

        public Harness()
        {
            Projects = new ProjectService(Tmp.Path);
            var members = new MemberService(Projects);
            Tickets = new TicketService(Projects, members);
            var runs = new AgentRunRegistry();
            var sessions = new SessionRegistry();
            var cost = new CostTracker();
            Executor = new ActionExecutor(
                Tickets, members, new LabelService(Projects), sessions, runs,
                new ClaudeRunner(sessions, runs, new RunConcurrencyGate(1), NullLogger<ClaudeRunner>.Instance),
                cost, new LocalizationService(new AppSettingsService(Tmp.Path)), Projects,
                new RunStateManager(runs, cost, Tickets, NullLogger.Instance),
                FakeHttpClientFactory.Unused, NullLogger.Instance);
        }

        public void Dispose() => Tmp.Dispose();
    }
}
