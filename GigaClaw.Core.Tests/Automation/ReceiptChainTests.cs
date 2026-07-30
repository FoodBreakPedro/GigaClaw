using System.Text.RegularExpressions;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// Agents hand work to each other through receipt markers in ticket comments: blog-seo refuses to
/// run without a <c>BLOG-REVIEW APPROVE v1</c> receipt carrying the current digest, ui-auditor
/// counts its own prior receipts to know which review cycle it is on. Nothing else in the suite
/// notices when an agent stops emitting a marker another agent requires — the pipeline just stalls
/// at runtime. These tests read the whole template as one graph and fail when a chain breaks.
/// </summary>
public class ReceiptChainTests
{
    private static readonly string AgentsDir =
        Path.Combine(PythonContractRunner.RepositoryRoot, "ProjectTemplate", "Agents");

    /// <summary>A SHOUTY token followed by a receipt word is how every marker in the template reads.</summary>
    private static readonly Regex FamilyRegex = new(
        @"\b([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)+)\s+(?:v1|PASS|FAIL|APPROVE|REJECT|RETURN|VALIDATED|cycle)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Cross-agent chains: which agents are allowed to be the source of a family that other agents
    /// consume. A family mentioned by exactly one agent owns itself and needs no entry; anything
    /// read by a second agent must be declared here, so a new hand-off cannot appear unnoticed.
    /// </summary>
    private static readonly Dictionary<string, string[]> Emitters = new(StringComparer.Ordinal)
    {
        ["BLOG-REVIEW"] = ["blog-reviewer"],
        ["BLOG-SEO"] = ["blog-seo"],
        ["CONTENT-REVIEW"] = ["blog-reviewer"],
        ["UI-AUDIT"] = ["ui-auditor"],
        ["GIGACLAW-VERDICT"] = ["blog-reviewer", "ui-auditor", "qa-tester", "local-media-reviewer", "evaluator"],
        ["GIGACLAW-HANDOFF"] = ["programmer", "qa-tester", "blog-writer", "blog-reviewer", "blog-seo", "producer"],
    };

    private static Dictionary<string, string> Skills() =>
        Directory.EnumerateFiles(AgentsDir, "SKILL.md", SearchOption.AllDirectories)
            .ToDictionary(path => new DirectoryInfo(Path.GetDirectoryName(path)!).Name, File.ReadAllText);

    private static Dictionary<string, HashSet<string>> Families(Dictionary<string, string> skills)
    {
        var families = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (slug, text) in skills)
        {
            foreach (Match match in FamilyRegex.Matches(text))
            {
                var family = match.Groups[1].Value;
                if (!families.TryGetValue(family, out var agents))
                    families[family] = agents = new HashSet<string>(StringComparer.Ordinal);
                agents.Add(slug);
            }
        }

        return families;
    }

    /// <summary>
    /// Emission versus consumption is the whole point: an agent that only *searches* for a receipt
    /// is a consumer, and a chain where every mention is a search is exactly the broken state this
    /// guards. A line counts as emission when it hands the marker to the helper, stands alone as a
    /// marker line, or instructs the agent to write it — and does not read as a lookup.
    /// </summary>
    private static bool Emits(string skill, string family)
    {
        var escaped = Regex.Escape(family);
        if (Regex.IsMatch(skill, $@"--marker\s+""{escaped}\b"))
            return true;

        foreach (var line in skill.Split('\n'))
        {
            if (!line.Contains(family, StringComparison.Ordinal))
                continue;
            if (Regex.IsMatch(line, @"\b(search|Search|count|Count|prior|existing|already exists|look for)\b"))
                continue; // Reading someone's receipt, not writing one.
            if (Regex.IsMatch(line, $@"^\s*`?{escaped}\b")
                || Regex.IsMatch(line, @"\b(post|Post|include|Include|including|emit|Emit|append|Append|write|Write)\b"))
                return true;
        }

        return false;
    }

    [Fact]
    public void Every_cross_agent_receipt_family_is_declared()
    {
        var families = Families(Skills());
        Assert.NotEmpty(families);

        var undeclared = families
            .Where(f => f.Value.Count > 1 && !Emitters.ContainsKey(f.Key))
            .Select(f => $"{f.Key} (read by {string.Join(", ", f.Value.Order())})")
            .ToList();

        Assert.True(
            undeclared.Count == 0,
            "These receipt families are read by more than one agent but have no declared emitter. " +
            "Add them to ReceiptChainTests.Emitters so the chain is checked:" +
            Environment.NewLine + string.Join(Environment.NewLine, undeclared));
    }

    [Fact]
    public void Every_required_receipt_is_emitted_by_someone()
    {
        var skills = Skills();
        var broken = new List<string>();

        foreach (var (family, readers) in Families(skills))
        {
            if (!Emitters.TryGetValue(family, out var owners))
                continue; // Single-agent family: it emits and consumes its own receipt.

            var consumers = readers.Where(r => !owners.Contains(r)).Order().ToList();
            if (consumers.Count == 0)
                continue;

            var emitting = owners.Where(o => skills.TryGetValue(o, out var text) && Emits(text, family)).ToList();
            if (emitting.Count == 0)
            {
                broken.Add(
                    $"{family}: required by {string.Join(", ", consumers)}, but no declared emitter " +
                    $"({string.Join(", ", owners)}) still writes it.");
            }
        }

        Assert.True(
            broken.Count == 0,
            "A receipt chain is broken — the consuming agents will stall at runtime with no test failure " +
            "anywhere else:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    [Fact]
    public void Declared_emitters_exist_as_agents()
    {
        var skills = Skills();
        foreach (var (family, owners) in Emitters)
        {
            foreach (var owner in owners)
            {
                Assert.True(
                    skills.ContainsKey(owner),
                    $"Receipt family {family} declares emitter '{owner}', which is not a template agent.");
            }
        }
    }
}
