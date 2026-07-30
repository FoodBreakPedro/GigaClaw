using System.Text.Json;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// The CL half of P4: dependency edges (owned by lane CX-T) become an automation gate.
/// A dispatch automation carrying this condition must not fire while a blocker is open.
/// </summary>
public class DependenciesResolvedConditionTests
{
    private static TicketDependencyInfo Blocker(int id, string status)
        => new(id, $"Ticket {id}", status, AssignedTo: null);

    [Fact]
    public void Ticket_with_no_edges_is_not_blocked()
        => Assert.True(ConditionEvaluators.DependenciesResolved(new DependenciesResolvedConditionSpec(), []));

    [Fact]
    public void Open_blocker_holds_the_ticket()
    {
        var spec = new DependenciesResolvedConditionSpec();

        Assert.False(ConditionEvaluators.DependenciesResolved(spec, [Blocker(1, "InProgress")]));
        Assert.False(ConditionEvaluators.DependenciesResolved(spec, [Blocker(1, "Done"), Blocker(2, "Todo")]));
        Assert.True(ConditionEvaluators.DependenciesResolved(spec, [Blocker(1, "Done"), Blocker(2, "Done")]));
    }

    [Fact]
    public void Resolved_statuses_are_configurable_and_case_sensitive_like_every_other_status_check()
    {
        var spec = new DependenciesResolvedConditionSpec { ResolvedStatuses = ["Done", "Cancelled"] };

        Assert.True(ConditionEvaluators.DependenciesResolved(spec, [Blocker(1, "Cancelled")]));
        Assert.False(ConditionEvaluators.DependenciesResolved(spec, [Blocker(1, "cancelled")]));
        Assert.False(ConditionEvaluators.DependenciesResolved(spec, [Blocker(1, "Review")]));
    }

    [Fact]
    public void Empty_status_list_falls_back_to_Done_rather_than_matching_everything()
    {
        var spec = new DependenciesResolvedConditionSpec { ResolvedStatuses = [] };

        Assert.False(ConditionEvaluators.DependenciesResolved(spec, [Blocker(1, "Review")]));
        Assert.True(ConditionEvaluators.DependenciesResolved(spec, [Blocker(1, "Done")]));
    }

    [Fact]
    public void Condition_round_trips_through_the_automation_config_serializer()
    {
        var json = """
        {
          "automations": [{
            "id": "dispatch-when-unblocked",
            "trigger": { "type": "ticketInColumn", "columns": ["Todo"] },
            "conditions": [{ "type": "dependenciesResolved", "resolvedStatuses": ["Done", "Cancelled"] }],
            "actions": [{ "type": "runAgent", "agent": "programmer" }]
          }]
        }
        """;

        var config = JsonSerializer.Deserialize<AutomationConfig>(json, AutomationStore.JsonOptions)!;
        var condition = Assert.IsType<DependenciesResolvedConditionSpec>(Assert.Single(config.Automations[0].Conditions));
        Assert.Equal(["Done", "Cancelled"], condition.ResolvedStatuses);

        var reloaded = JsonSerializer.Deserialize<AutomationConfig>(
            JsonSerializer.Serialize(config, AutomationStore.JsonOptions), AutomationStore.JsonOptions)!;
        Assert.IsType<DependenciesResolvedConditionSpec>(Assert.Single(reloaded.Automations[0].Conditions));
    }

    [Fact]
    public async Task Blocked_ticket_does_not_dispatch_and_unblocks_when_the_blocker_is_done()
    {
        using var harness = new Harness();
        var project = await harness.Projects.CreateProjectAsync("dependency-gate");
        var blocker = await harness.Tickets.CreateTicketAsync(project.Slug, "Ship the schema", status: "InProgress");
        var blocked = await harness.Tickets.CreateTicketAsync(project.Slug, "Wire the gate", status: "Todo");
        await harness.Tickets.AddTicketDependencyAsync(project.Slug, blocked.Id, blocker.Id);

        var automation = new AutomationRule
        {
            Id = "dispatch-when-unblocked",
            Trigger = new TicketInColumnTriggerSpec { Columns = ["Todo"] },
            Conditions = [new DependenciesResolvedConditionSpec()],
            Actions = [],
        };
        var runtime = new ProjectRuntime(project.Slug)
        {
            Workspace = harness.Projects.ResolveWorkspacePath(project),
            Config = new AutomationConfig(),
        };
        var firing = new TriggerFiring(blocked.Id, blocked.Title, "Todo");

        Assert.False(await harness.Executor.ConditionsMatchAsync(runtime, automation, firing));

        await harness.Tickets.MoveTicketAsync(project.Slug, blocker.Id, "Done");

        Assert.True(await harness.Executor.ConditionsMatchAsync(runtime, automation, firing));
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
