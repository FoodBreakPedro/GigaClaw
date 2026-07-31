using System.Text.Json;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Workflow;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// C5 part 2: the typed workflow graph over tickets, and the promise that an illegal one is
/// <b>un-loadable</b> rather than discoverable at 3am. The two failures the acceptance criteria name
/// are a state nothing can reach and a cycle with no gate to leave it through; both are refused at
/// config load, reported the way a malformed <c>automations.json</c> already is.
/// </summary>
public sealed class WorkflowGraphTests
{
    /// <summary>
    /// Draft → review gate → (SHIP) publish, (FIX) back to draft, (BLOCK) escalate. A real repair
    /// loop: cyclic, but the cycle runs through the gate that can end it.
    /// </summary>
    private static WorkflowGraph ReviewLoop() => new()
    {
        Initial = "draft",
        MaxCycles = 3,
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
                    new WorkflowTransition("publish") { When = "SHIP" },
                    new WorkflowTransition("draft") { When = "FIX" },
                    new WorkflowTransition("escalated") { When = "BLOCK" }
                ]
            },
            new WorkflowState("publish", WorkflowStateKind.Terminal),
            new WorkflowState("escalated", WorkflowStateKind.Terminal)
        ]
    };

    [Fact]
    public void A_gated_repair_loop_is_valid()
    {
        Assert.Empty(ReviewLoop().Validate());
        Assert.Equal("draft", ReviewLoop().EntryState);
    }

    [Fact]
    public void A_fan_out_and_join_graph_is_valid()
    {
        var graph = new WorkflowGraph
        {
            Initial = "split",
            States =
            [
                new WorkflowState("split", WorkflowStateKind.FanOut)
                {
                    Next = [new WorkflowTransition("security"), new WorkflowTransition("performance")]
                },
                new WorkflowState("security", WorkflowStateKind.Task)
                {
                    Role = "security-auditor",
                    Next = [new WorkflowTransition("merge")]
                },
                new WorkflowState("performance", WorkflowStateKind.Task)
                {
                    Role = "qa-tester",
                    Next = [new WorkflowTransition("merge")]
                },
                new WorkflowState("merge", WorkflowStateKind.Join)
                {
                    JoinOf = "split",
                    Next = [new WorkflowTransition("done")]
                },
                new WorkflowState("done", WorkflowStateKind.Terminal)
            ]
        };

        Assert.Empty(graph.Validate());
    }

    // ── The two acceptance criteria ─────────────────────────────────────────

    [Fact]
    public void An_unreachable_state_is_rejected()
    {
        var graph = ReviewLoop() with
        {
            States =
            [
                .. ReviewLoop().States,
                // Nothing transitions to it. Usually a rename applied on one side only, which stays
                // invisible until the ticket that needed the state is the one that is stuck.
                new WorkflowState("orphan", WorkflowStateKind.Task)
                {
                    Role = "producer",
                    Next = [new WorkflowTransition("publish")]
                }
            ]
        };

        var problem = Assert.Single(graph.Validate());
        Assert.Equal("State 'orphan' is unreachable from 'draft'.", problem);
    }

    [Fact]
    public void A_cycle_with_no_gate_on_it_is_rejected()
    {
        var graph = new WorkflowGraph
        {
            Initial = "draft",
            States =
            [
                new WorkflowState("draft", WorkflowStateKind.Task)
                {
                    Role = "blog-writer",
                    Next = [new WorkflowTransition("review")]
                },
                // review → draft with nothing that can decide to stop: an unbounded loop with a
                // ticket and a token budget inside it.
                new WorkflowState("review", WorkflowStateKind.Task)
                {
                    Role = "blog-reviewer",
                    Next = [new WorkflowTransition("draft"), new WorkflowTransition("publish")]
                },
                new WorkflowState("publish", WorkflowStateKind.Terminal)
            ]
        };

        var problem = Assert.Single(graph.Validate());
        Assert.Contains("cycle with no gate on it", problem, StringComparison.Ordinal);
        Assert.Contains("draft", problem, StringComparison.Ordinal);
        Assert.Contains("review", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_self_loop_without_a_gate_is_a_cycle_too()
    {
        var graph = ReviewLoop() with
        {
            States =
            [
                new WorkflowState("draft", WorkflowStateKind.Task)
                {
                    Role = "blog-writer",
                    Next = [new WorkflowTransition("draft"), new WorkflowTransition("review")]
                },
                .. ReviewLoop().States.Skip(1)
            ]
        };

        Assert.Contains(graph.Validate(), problem => problem.Contains("cycle with no gate", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cycle_that_passes_through_a_gate_is_not_reported()
    {
        // The same shape as the rejected one, with the gate put back. Guards against a validator
        // that simply refuses every cycle, which would make the repair loop undeclarable.
        Assert.DoesNotContain(ReviewLoop().Validate(), problem => problem.Contains("cycle", StringComparison.Ordinal));
    }

    // ── The rest of the structural verdict ──────────────────────────────────

    [Theory]
    [InlineData("dangling transition", "transitions to unknown state 'nowhere'")]
    [InlineData("duplicate state", "Duplicate state 'draft'")]
    [InlineData("terminal with an exit", "Terminal state 'publish' has 1 outgoing transition")]
    [InlineData("dead end", "is not terminal but has nowhere to go")]
    [InlineData("roleless task", "Task state 'draft' names no role")]
    [InlineData("gate without a condition", "Gate state 'verdict' has no condition")]
    [InlineData("condition on a task", "carries a gate condition but is a Task state")]
    [InlineData("one-armed fan-out", "a fan-out needs at least 2")]
    [InlineData("join of nothing", "does not say which fan-out it closes")]
    [InlineData("join of a task", "which is not a fan-out state")]
    [InlineData("no terminal", "no terminal state")]
    [InlineData("unknown initial", "Initial state 'start' is not one of the graph's states")]
    [InlineData("newer schema", "newer than this build understands")]
    [InlineData("zero cycles on a cyclic graph", "maxCycles is 0 but the graph has a cycle")]
    public void A_structurally_broken_graph_names_its_problem(string flaw, string expected)
    {
        var graph = Break(flaw);
        Assert.Contains(graph.Validate(), problem => problem.Contains(expected, StringComparison.Ordinal));
    }

    private static WorkflowGraph Break(string flaw)
    {
        var baseline = ReviewLoop();
        var states = baseline.States.ToList();

        WorkflowState With(string name, Func<WorkflowState, WorkflowState> mutate)
        {
            var index = states.FindIndex(state => state.Name == name);
            states[index] = mutate(states[index]);
            return states[index];
        }

        switch (flaw)
        {
            case "dangling transition":
                With("draft", state => state with { Next = [new WorkflowTransition("nowhere")] });
                break;
            case "duplicate state":
                states.Add(new WorkflowState("draft", WorkflowStateKind.Terminal));
                break;
            case "terminal with an exit":
                With("publish", state => state with { Next = [new WorkflowTransition("draft")] });
                break;
            case "dead end":
                With("review", state => state with { Next = [] });
                break;
            case "roleless task":
                With("draft", state => state with { Role = null });
                break;
            case "gate without a condition":
                With("verdict", state => state with { Gate = null });
                break;
            case "condition on a task":
                With("draft", state => state with { Gate = new VerdictIsConditionSpec() });
                break;
            case "one-armed fan-out":
                With("review", state => state with { Kind = WorkflowStateKind.FanOut, Role = null });
                break;
            case "join of nothing":
                With("review", state => state with { Kind = WorkflowStateKind.Join, Role = null });
                break;
            case "join of a task":
                With("review", state => state with { Kind = WorkflowStateKind.Join, Role = null, JoinOf = "draft" });
                break;
            case "no terminal":
                states.RemoveAll(state => state.Kind == WorkflowStateKind.Terminal);
                With("verdict", state => state with { Next = [new WorkflowTransition("draft") { When = "FIX" }] });
                break;
            case "unknown initial":
                return baseline with { Initial = "start" };
            case "newer schema":
                return baseline with { SchemaVersion = WorkflowGraph.CurrentSchemaVersion + 1 };
            default:
                return baseline with { MaxCycles = 0 };
        }

        return baseline with { States = states };
    }

    // ── The file, and the load point ────────────────────────────────────────

    [Fact]
    public void The_document_round_trips_as_words_not_ordinals()
    {
        var json = JsonSerializer.Serialize(ReviewLoop(), WorkflowGraphFile.JsonOptions);

        // A hand-edited config file must read as a hand-edited config file.
        Assert.Contains("\"kind\": \"gate\"", json, StringComparison.Ordinal);
        Assert.Contains("\"when\": \"SHIP\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"verdictIs\"", json, StringComparison.Ordinal);

        var round = JsonSerializer.Deserialize<WorkflowGraph>(json, WorkflowGraphFile.JsonOptions)!;
        Assert.Empty(round.Validate());
        Assert.IsType<VerdictIsConditionSpec>(round.Find("verdict")!.Gate);
    }

    [Fact]
    public async Task An_invalid_graph_is_rejected_at_config_load_like_a_malformed_automations_file()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("workflow-load");
        var agentsDir = Path.Combine(projects.ResolveWorkspacePath(project), ".agents");
        Directory.CreateDirectory(agentsDir);

        var store = new AutomationStore(projects);
        // No file at all is the normal case, not an error.
        var (_, _, _) = await store.LoadAsync(project.Slug);
        Assert.Null(store.GetCachedWorkflow(project.Slug));

        // A valid graph loads and is cached beside the automations.
        await WriteAsync(agentsDir, ReviewLoop());
        await store.LoadAsync(project.Slug);
        Assert.Equal(5, store.GetCachedWorkflow(project.Slug)!.States.Count);

        // An invalid one throws out of the same load point a malformed automations.json throws from.
        await WriteAsync(agentsDir, ReviewLoop() with
        {
            States = [.. ReviewLoop().States, new WorkflowState("orphan", WorkflowStateKind.Terminal)]
        });
        var exception = await Assert.ThrowsAsync<WorkflowGraphException>(() => store.LoadAsync(project.Slug));
        Assert.Contains("workflow.json is invalid", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'orphan' is unreachable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_with_an_invalid_graph_keeps_its_previous_runtime_and_logs_the_failure()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("workflow-reload");
        var agentsDir = Path.Combine(projects.ResolveWorkspacePath(project), ".agents");
        Directory.CreateDirectory(agentsDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentsDir, "automations.json"),
            """{"automations":[{"id":"a","enabled":true,"trigger":{"type":"interval","cron":"0 * * * *"},"conditions":[],"actions":[]}]}""");

        var store = new AutomationStore(projects);
        var logs = new CapturingLogger();
        var manager = new ProjectRuntimeManager(store, new TriggerStateStore(projects), projects, logs);

        await WriteAsync(agentsDir, ReviewLoop());
        await manager.EnsureLoadedAsync(project.Slug);
        var runtime = manager.GetRuntime(project.Slug);
        Assert.Single(runtime.Config!.Automations);
        Assert.Equal(5, runtime.Workflow!.States.Count);

        // A graph that no longer validates: the reload fails as a whole, the previous runtime stands,
        // and the report is the existing "Failed to reload automations" warning — the same surface a
        // malformed automations.json has always used.
        await WriteAsync(agentsDir, ReviewLoop() with
        {
            States = [.. ReviewLoop().States, new WorkflowState("orphan", WorkflowStateKind.Terminal)]
        });
        await manager.ReloadProjectAsync(project.Slug);

        Assert.Single(manager.GetRuntime(project.Slug).Config!.Automations);
        Assert.Equal(5, manager.GetRuntime(project.Slug).Workflow!.States.Count);
        Assert.Contains(logs.Warnings, warning =>
            warning.Message.Contains("Failed to reload automations", StringComparison.Ordinal)
            && warning.Exception is WorkflowGraphException
            && warning.Exception.Message.Contains("'orphan' is unreachable", StringComparison.Ordinal));
    }

    private static Task WriteAsync(string agentsDir, WorkflowGraph graph) =>
        File.WriteAllTextAsync(
            WorkflowGraphFile.PathFor(agentsDir),
            JsonSerializer.Serialize(graph, WorkflowGraphFile.JsonOptions));

    private sealed class CapturingLogger : ILogger
    {
        public List<(string Message, Exception? Exception)> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                Warnings.Add((formatter(state, exception), exception));
        }
    }
}
