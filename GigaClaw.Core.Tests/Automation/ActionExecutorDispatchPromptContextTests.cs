using GigaClaw.Core.Automation;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Automation;

public class ActionExecutorDispatchPromptContextTests
{
    [Fact]
    public async Task ComposeDispatchContextAsync_PreservesPersistedDeliverableType()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("dispatch-context-test");
        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "blog-writer");
        var tickets = new TicketService(projects, members);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var runner = new ClaudeRunner(
            sessions, runs, new RunConcurrencyGate(1), NullLogger<ClaudeRunner>.Instance);
        var cost = new CostTracker();
        var loc = new LocalizationService(new AppSettingsService(tmp.Path));
        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, loc, projects,
            new RunStateManager(runs, cost, tickets, NullLogger.Instance),
            FakeHttpClientFactory.Unused, TestTeamRuns.For(projects, tickets), NullLogger.Instance);

        var ticket = await tickets.CreateTicketAsync(
            project.Slug,
            "Dispatch me",
            deliverableType: "product-review");

        var runtime = new ProjectRuntime(project.Slug)
        {
            Workspace = projects.ResolveWorkspacePath(project),
            Config = new AutomationConfig { Automations = [] },
        };

        var promptContext = await executor.ComposeDispatchContextAsync(runtime, ticket.Id, "Follow the latest brief.");

        Assert.Equal("product-review", promptContext.RequestedDeliverableType);
        Assert.Equal(ImageSourcePreference.Pexels, promptContext.RequestedImageSource);
        Assert.Equal(VideoSourcePreference.None, promptContext.RequestedVideoSource);
        Assert.False(promptContext.RequireMediaBeforeDelivery);
        Assert.Equal("Follow the latest brief.", promptContext.ExtraContext);
    }

    [Fact]
    public async Task ComposeDispatchContextAsync_PreservesExplicitMediaPreferences()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("dispatch-context-media");
        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "blog-writer");
        var tickets = new TicketService(projects, members);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var runner = new ClaudeRunner(
            sessions, runs, new RunConcurrencyGate(1), NullLogger<ClaudeRunner>.Instance);
        var cost = new CostTracker();
        var loc = new LocalizationService(new AppSettingsService(tmp.Path));
        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, loc, projects,
            new RunStateManager(runs, cost, tickets, NullLogger.Instance),
            FakeHttpClientFactory.Unused, TestTeamRuns.For(projects, tickets), NullLogger.Instance);

        var ticket = await tickets.CreateTicketAsync(
            project.Slug,
            "Need local media",
            deliverableType: "product-review",
            imageSource: ImageSourcePreference.LocalGeneration,
            videoSource: VideoSourcePreference.OpenMontage,
            requireMediaBeforeDelivery: true);

        var runtime = new ProjectRuntime(project.Slug)
        {
            Workspace = projects.ResolveWorkspacePath(project),
            Config = new AutomationConfig { Automations = [] },
        };

        var promptContext = await executor.ComposeDispatchContextAsync(runtime, ticket.Id, "Follow the latest brief.");

        Assert.Equal(ImageSourcePreference.LocalGeneration, promptContext.RequestedImageSource);
        Assert.Equal(VideoSourcePreference.OpenMontage, promptContext.RequestedVideoSource);
        Assert.True(promptContext.RequireMediaBeforeDelivery);
    }
}
