using System.Text.Json;

namespace GigaClaw.Catalog;

/// <summary>
/// The composed receipt-emitter table (doc/pack-infrastructure.md §7.3).
///
/// <para>
/// Agents hand work to each other through receipt markers in ticket comments, and the chain tests
/// in <c>GigaClaw.Core.Tests</c> need to know which agents are allowed to be the <em>source</em> of
/// a family that other agents consume. That table used to be a hardcoded dictionary inside the
/// test, which made the test a bottleneck: a pack shipping a new emitter of <c>GIGACLAW-VERDICT</c>
/// failed a core test until a human edited core's test file. Each pack now declares
/// <c>receiptEmitters</c> in its manifest and this type takes the union.
/// </para>
///
/// <para>
/// The union is deliberately additive and never subtractive. Two packs naming different emitters of
/// one family both hold; neither can revoke the other's, because a pack that could remove core's
/// emitter could silently break a chain core's own agents depend on.
/// </para>
/// </summary>
public static class ReceiptEmitterTable
{
    /// <summary>Manifest field name, and the key inside the transitional core table file.</summary>
    public const string FieldName = "receiptEmitters";

    /// <summary>
    /// Transitional home for core's own table while <c>ProjectTemplate/</c> has no manifest.
    /// Deliberately outside <c>ProjectTemplate/</c>: everything under that directory composes into
    /// an initialized workspace, and this is build-time review data no workspace should receive.
    /// </summary>
    public const string CoreTableFile = "core-receipt-emitters.json";

    /// <summary>Union of every source's declared emitters, families and agents sorted for stable output.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Compose(
        IEnumerable<PackCatalogSource> sources)
    {
        var union = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source.ReceiptEmitters is null) continue;
            foreach (var (family, agents) in source.ReceiptEmitters)
            {
                if (!union.TryGetValue(family, out var owners))
                    union[family] = owners = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var agent in agents) owners.Add(agent);
            }
        }

        return union
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<string>)entry.Value.ToArray(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// The table every installed pack contributes to, discovered from the repository tree. This is
    /// the entry point the receipt-chain tests call — they get the composed union without knowing
    /// how many packs exist or where any of them live.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Compose(string repositoryRoot) =>
        Compose(PackCatalogSourceReader.Discover(repositoryRoot));

    /// <summary>
    /// Core's emitters while <c>ProjectTemplate/pack.json</c> does not exist. Returns null once it
    /// does and declares <see cref="FieldName"/>, so the manifest is the single source and the two
    /// can never disagree.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? ReadCoreTable(string repositoryRoot)
    {
        var manifest = Path.Combine(repositoryRoot, "ProjectTemplate", "pack.json");
        if (File.Exists(manifest) && PackCatalogSourceReader.ReadReceiptEmitters(manifest) is { Count: > 0 } declared)
            return declared;

        var path = Path.Combine(repositoryRoot, "GigaClaw.Catalog", CoreTableFile);
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty(FieldName, out var emitters)
            ? PackCatalogSourceReader.ReadStringListMap(emitters)
            : null;
    }
}
