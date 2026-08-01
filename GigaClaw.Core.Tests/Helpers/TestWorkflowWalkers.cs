using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Workflow;
using GigaClaw.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Helpers;

/// <summary>
/// The <see cref="WorkflowWalker"/> a <c>TriggerHandler</c> harness needs. The dependency is
/// explicit rather than nullable for the same reason <see cref="TestTeamRuns"/> is: a nullable
/// would silently turn every declared workflow into a no-op in production.
/// </summary>
internal static class TestWorkflowWalkers
{
    /// <summary>A walker whose gates go through the real <see cref="ActionExecutor"/> condition path.</summary>
    public static WorkflowWalker For(
        ProjectService projects,
        TicketService tickets,
        TeamRunService teamRuns,
        ActionExecutor executor,
        ILogger? logger = null) =>
        new(tickets, new MemberService(projects), teamRuns, executor.EvaluateWorkflowGateAsync,
            logger ?? NullLogger.Instance);

    /// <summary>A walker for harnesses that never declare a workflow: its gate is never reached.</summary>
    public static WorkflowWalker Inert(ProjectService projects, TicketService tickets) =>
        new(tickets, new MemberService(projects), TestTeamRuns.For(projects, tickets),
            static (_, _, _) => throw new NotSupportedException("This harness declares no workflow gates."),
            NullLogger.Instance);
}
