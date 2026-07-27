using GigaClaw.Core.Automation;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Automation;

public sealed class StatusChangeSnapshotIsolationTests
{
    [Fact]
    public void Automation_snapshots_migrate_from_legacy_state_then_diverge_independently()
    {
        using var tmp = new TempDir();
        var sessions = new SessionRegistry();
        sessions.SaveTicketSnapshot(
            tmp.Path,
            new Dictionary<int, string> { [42] = "Develop" });

        Assert.Equal("Develop", sessions.TicketSnapshot(tmp.Path, "committer")[42]);
        Assert.Equal("Develop", sessions.TicketSnapshot(tmp.Path, "approval-marker")[42]);

        sessions.SaveTicketSnapshot(
            tmp.Path,
            "committer",
            new Dictionary<int, string> { [42] = "Done" });

        Assert.Equal("Done", sessions.TicketSnapshot(tmp.Path, "committer")[42]);
        Assert.Equal(
            "Develop",
            sessions.TicketSnapshot(tmp.Path, "approval-marker")[42]);
    }
}
