using GigaClaw.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Helpers;

/// <summary>
/// The <see cref="TeamRunService"/> every <c>ActionExecutor</c> harness needs. Tests that never
/// touch a team still have to hand the executor one, so the dependency is explicit rather than a
/// nullable that would silently turn <c>startTeamRun</c> into a no-op in production.
/// </summary>
internal static class TestTeamRuns
{
    public static TeamRunService For(ProjectService projects, TicketService tickets) =>
        new(new TeamStore(projects, tickets), tickets, new MemberService(projects), new AgentTeamService(),
            NullLogger<TeamRunService>.Instance);
}
