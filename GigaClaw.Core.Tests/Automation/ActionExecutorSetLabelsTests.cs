using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Automation;

public class ActionExecutorSetLabelsTests
{
    [Fact]
    public async Task SetLabels_creates_missing_definition_and_applies_it()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("set-label-test");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var cost = new CostTracker();
        var runner = new ClaudeRunner(
            sessions, runs, new RunConcurrencyGate(1), NullLogger<ClaudeRunner>.Instance);
        var loc = new LocalizationService(new AppSettingsService(tmp.Path));
        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, loc, projects,
            new RunStateManager(runs, cost, tickets, NullLogger.Instance),
            FakeHttpClientFactory.Unused, TestTeamRuns.For(projects, tickets), NullLogger.Instance);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Approval", status: "Done");
        var runtime = new ProjectRuntime(project.Slug)
        {
            Workspace = projects.ResolveWorkspacePath(project),
            Config = new AutomationConfig { Automations = [] },
        };
        var automation = new AutomationRule
        {
            Id = "mark-approved",
            Trigger = new StatusChangeTriggerSpec { To = "Done" },
            Actions = [new SetLabelsActionSpec { Add = ["approved"] }],
        };

        await executor.ExecuteAutomationAsync(
            runtime,
            automation,
            new TriggerFiring(ticket.Id, ticket.Title, ticket.Status),
            CancellationToken.None);

        var refreshed = await tickets.GetTicketAsync(project.Slug, ticket.Id);
        Assert.NotNull(refreshed);
        Assert.Contains(refreshed.Labels, label => label.Name == "approved");
        Assert.Single(
            await labels.ListLabelsAsync(project.Slug),
            label => label.Name == "approved");
    }
}
