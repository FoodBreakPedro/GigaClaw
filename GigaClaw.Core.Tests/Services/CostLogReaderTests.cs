using System.Text.Json;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// Plan 4.2 §3/§6: Mission Control's agent-workload and cost strip read the durable cost journal
/// rather than the run registry (which <c>AutomationEngine</c> purges after 24h). These cover the
/// window filter, the rotated-file sweep, malformed-line tolerance, and the cache-savings ratio.
/// </summary>
public sealed class CostLogReaderTests
{
    private static void WriteLog(string workspace, string fileName, params CostLogEntry[] entries)
    {
        var dir = Path.Combine(workspace, ".agents", "channel");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(
            Path.Combine(dir, fileName),
            entries.Select(e => JsonSerializer.Serialize(e)));
    }

    private static CostLogEntry Entry(string agent, DateTime at, decimal usd = 1m, int input = 100, int cacheRead = 0) =>
        new(at, agent, null, "sonnet", input, 50, cacheRead, 0, usd, 12.5, 0);

    [Fact]
    public void Read_ReturnsNothingWhenTheWorkspaceHasNoJournal()
    {
        using var tmp = new TempDir();
        Assert.Empty(CostLogReader.Read(tmp.Path, DateTime.UtcNow.AddDays(-7)));
    }

    [Fact]
    public void Read_FiltersByWindow_AndSortsOldestFirst()
    {
        using var tmp = new TempDir();
        var now = DateTime.UtcNow;
        WriteLog(tmp.Path, "cost-log.jsonl",
            Entry("groomer", now.AddDays(-1)),
            Entry("programmer", now.AddHours(-2)),
            Entry("ancient", now.AddDays(-30)));

        var entries = CostLogReader.Read(tmp.Path, now.AddDays(-7));

        Assert.Equal(2, entries.Count);
        Assert.Equal("groomer", entries[0].Agent);
        Assert.Equal("programmer", entries[1].Agent);
    }

    [Fact]
    public void Read_SweepsRotatedMonthlyFiles()
    {
        using var tmp = new TempDir();
        var now = DateTime.UtcNow;
        WriteLog(tmp.Path, "cost-log.jsonl", Entry("programmer", now.AddHours(-1)));
        WriteLog(tmp.Path, $"cost-log-{now:yyyy-MM}.jsonl", Entry("qa-tester", now.AddDays(-2)));

        var agents = CostLogReader.Read(tmp.Path, now.AddDays(-7)).Select(e => e.Agent).ToList();

        Assert.Contains("programmer", agents);
        Assert.Contains("qa-tester", agents);
    }

    [Fact]
    public void Read_SkipsMalformedLines()
    {
        using var tmp = new TempDir();
        var now = DateTime.UtcNow;
        var dir = Path.Combine(tmp.Path, ".agents", "channel");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "cost-log.jsonl"),
        [
            "{ this is not json",
            "",
            JsonSerializer.Serialize(Entry("groomer", now.AddMinutes(-5))),
        ]);

        var entry = Assert.Single(CostLogReader.Read(tmp.Path, now.AddDays(-7)));
        Assert.Equal("groomer", entry.Agent);
    }

    [Fact]
    public void CacheSavingsPercent_IsCachedOverCachedPlusFreshPromptTokens()
    {
        var now = DateTime.UtcNow;
        var entries = new[]
        {
            Entry("a", now, input: 300, cacheRead: 700),
            Entry("b", now, input: 700, cacheRead: 300),
        };

        Assert.Equal(50, CostLogReader.CacheSavingsPercent(entries));
    }

    [Fact]
    public void CacheSavingsPercent_IsNullWhenNothingWasRead()
    {
        // No prompt tokens at all means "unknown", never "0% saved".
        Assert.Null(CostLogReader.CacheSavingsPercent([]));
    }
}
