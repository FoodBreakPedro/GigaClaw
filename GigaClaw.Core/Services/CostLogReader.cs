using System.Text.Json;
using GigaClaw.Core.Automation;

namespace GigaClaw.Core.Services;

/// <summary>
/// Reads the per-workspace agent cost journal that <see cref="CostTracker"/> appends to
/// (<c>.agents/channel/cost-log.jsonl</c>, plus its monthly <c>cost-log-YYYY-MM.jsonl</c> rotations).
///
/// <para><b>Why this is Mission Control's workload source and not the run registry.</b>
/// <c>AgentRunRegistry</c> is purged every tick — <c>AutomationEngine</c> calls
/// <c>PurgeOld(TimeSpan.FromHours(24))</c>, which also deletes the run's JSON from
/// <c>RunLogStore</c> — so it can answer "what is running now" and "what ran in the last day", but
/// never "how many times did qa-tester run this week". The cost journal is the only durable record
/// of every dispatch with a timestamp and an agent name, and one sequential read of a JSONL file per
/// project is cheaper than re-hydrating run snapshots that no longer exist. Live runs still come
/// from the registry, which is the one thing the journal cannot know.</para>
/// </summary>
public static class CostLogReader
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Hard ceiling on how many entries one read may accumulate. Everything downstream is
    /// an aggregate — sums, counts, a max timestamp — so a journal large enough to hit this is
    /// already past the point where one more line changes a rendered figure, and holding it all in
    /// memory to render a dashboard is the only thing that would actually be noticed.</summary>
    public const int MaxEntries = 50_000;

    /// <summary>Every cost entry in the workspace at or after <paramref name="sinceUtc"/>, oldest
    /// first. Missing files, unreadable files and malformed lines are skipped — a cost journal is
    /// telemetry, and no dashboard section may fail because an agent wrote a bad line.</summary>
    public static IReadOnlyList<CostLogEntry> Read(string workspacePath, DateTime sinceUtc)
    {
        var dir = Path.Combine(workspacePath, ".agents", "channel");
        if (!Directory.Exists(dir)) return [];

        var entries = new List<CostLogEntry>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "cost-log*.jsonl"); }
        catch { return []; }

        foreach (var file in files)
        {
            if (entries.Count >= MaxEntries) break;
            // A rotated month whose file was last written before the window opened cannot contain a
            // line inside it; skip the read entirely rather than parsing an archive every render.
            try { if (File.GetLastWriteTimeUtc(file) < sinceUtc) continue; }
            catch { continue; }

            IEnumerable<string> lines;
            try { lines = File.ReadLines(file); }
            catch { continue; }

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                CostLogEntry? entry;
                try { entry = JsonSerializer.Deserialize<CostLogEntry>(line, Json); }
                catch { continue; }
                if (entry is null) continue;
                if (entry.At.ToUniversalTime() < sinceUtc) continue;
                entries.Add(entry);
                if (entries.Count >= MaxEntries) break;
            }
        }

        entries.Sort((a, b) => a.At.CompareTo(b.At));
        return entries;
    }

    /// <summary>
    /// Share of prompt tokens that were served from the prompt cache, as a 0–100 percentage:
    /// <c>cacheRead / (cacheRead + input)</c>. Null when nothing was read at all, so the UI can say
    /// "no data" instead of "0% saved". Cache <i>writes</i> are deliberately excluded — they are the
    /// cost of building the cache, not a saving from it.
    /// </summary>
    public static double? CacheSavingsPercent(IEnumerable<CostLogEntry> entries)
    {
        long cached = 0, fresh = 0;
        foreach (var entry in entries)
        {
            cached += entry.CacheReadTokens;
            fresh += entry.InputTokens;
        }
        var total = cached + fresh;
        return total == 0 ? null : cached * 100.0 / total;
    }
}
