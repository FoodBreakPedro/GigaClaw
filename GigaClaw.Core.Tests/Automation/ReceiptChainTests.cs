using System.Text.RegularExpressions;
using GigaClaw.Catalog;
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
    /// read by a second agent must be declared, so a new hand-off cannot appear unnoticed.
    ///
    /// <para>
    /// This table used to be a hardcoded dictionary right here, which made this test a gate on
    /// pack authorship: a pack shipping a new emitter of <c>GIGACLAW-VERDICT</c> failed a core test
    /// until a human edited core's test file, and the pack's own manifest — the thing a reviewer
    /// actually reads — said nothing about it. Each pack now declares <c>receiptEmitters</c> and
    /// <see cref="ReceiptEmitterTable"/> takes the union across every installed pack
    /// (doc/pack-infrastructure.md §7.3).
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Emitters =
        ReceiptEmitterTable.Compose(PythonContractRunner.RepositoryRoot);

    /// <summary>
    /// Every agent the composed system ships, core and packs alike. The receipt-emitter table is
    /// now a union across pack manifests (§7.3), so resolving its entries against core's directory
    /// alone would fail for any pack that declares an emitter — which is exactly what a pack is
    /// allowed to do.
    /// </summary>
    private static Dictionary<string, string> Skills()
    {
        var roots = new List<string> { AgentsDir };
        var packsRoot = Path.Combine(PythonContractRunner.RepositoryRoot, "Packs");
        if (Directory.Exists(packsRoot))
        {
            roots.AddRange(Directory
                .EnumerateDirectories(packsRoot)
                .Select(pack => Path.Combine(pack, "Agents"))
                .Where(Directory.Exists));
        }

        return roots
            .SelectMany(root => Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories))
            .ToDictionary(path => new DirectoryInfo(Path.GetDirectoryName(path)!).Name, File.ReadAllText);
    }

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
        Assert.NotEmpty(Emitters);

        var undeclared = families
            .Where(f => f.Value.Count > 1 && !Emitters.ContainsKey(f.Key))
            .Select(f => $"{f.Key} (read by {string.Join(", ", f.Value.Order())})")
            .ToList();

        Assert.True(
            undeclared.Count == 0,
            "These receipt families are read by more than one agent but have no declared emitter. " +
            "Declare them in the owning pack's `receiptEmitters` so the chain is checked:" +
            Environment.NewLine + string.Join(Environment.NewLine, undeclared));
    }

    /// <summary>
    /// The table has to reach the test as composed pack data, not as a constant someone reinstated.
    /// If this fails, the emitter declaration has drifted out of pack data and back into the test —
    /// and the next pack to ship an emitter will fail a core test it cannot fix from its own tree.
    /// </summary>
    [Fact]
    public void The_emitter_table_comes_from_composed_pack_data()
    {
        var composed = ReceiptEmitterTable.Compose(PythonContractRunner.RepositoryRoot);

        Assert.Equal(Emitters.Keys.Order(), composed.Keys.Order());
        Assert.Contains("blog-reviewer", composed["GIGACLAW-VERDICT"]);
        Assert.Contains("programmer", composed["GIGACLAW-HANDOFF"]);
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
