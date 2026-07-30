using System.Text.Json;

namespace GigaClaw.Catalog;

/// <summary>
/// Discovers the packs present in a repository and projects each one into the
/// <see cref="PackCatalogSource"/> view the CI gate runs over.
///
/// <para>
/// <b>This is not a manifest validator.</b> <c>GigaClaw.Core/Packs/PackManifestParser</c> owns the
/// grammar — the slug and semver shapes, the <c>schemaVersion</c> refusal, the
/// <c>requiresRuntime</c> bounds, the <c>automationPatches</c> op vocabulary — and refuses a
/// malformed pack before it can be installed. This reader extracts the handful of fields the
/// catalog gate needs and is tolerant of everything else, because a pack that reaches CI has
/// already been through the parser, and because the catalog must keep working in a tree where the
/// parser has not landed yet. Two readers is a cost; the alternative — a project reference the
/// catalog cannot yet satisfy — is a build that does not compile.
/// </para>
///
/// <para>
/// Once core is a pack this whole type is replaced by a projection from
/// <c>PackComposition.Packs</c>; see the integration note on <see cref="PackCatalogSource"/>.
/// </para>
/// </summary>
public static class PackCatalogSourceReader
{
    public const string ManifestFile = "pack.json";

    /// <summary>Where non-core packs live (§2, D1). Core stays at <c>ProjectTemplate/</c>.</summary>
    public const string PacksDirectory = "Packs";

    /// <summary>
    /// Every pack in the repository, core first and the rest by id ascending ordinal — the same
    /// order §4 requires of the composer, so catalog output stays stable.
    /// Falls back to the single un-manifested <see cref="PackCatalogSource.HostTemplate"/> when no
    /// <c>pack.json</c> exists anywhere, which is today's tree and today's behaviour exactly.
    /// </summary>
    public static IReadOnlyList<PackCatalogSource> Discover(string repositoryRoot)
    {
        var sources = new List<PackCatalogSource>();

        var coreManifest = Path.Combine(repositoryRoot, "ProjectTemplate", ManifestFile);
        sources.Add(File.Exists(coreManifest)
            ? Read(coreManifest, Path.Combine(repositoryRoot, "ProjectTemplate"))
            : PackCatalogSource.HostTemplate(repositoryRoot));

        var packsRoot = Path.Combine(repositoryRoot, PacksDirectory);
        if (Directory.Exists(packsRoot))
        {
            var packs = Directory.EnumerateDirectories(packsRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(directory => File.Exists(Path.Combine(directory, ManifestFile)))
                .OrderBy(directory => Path.GetFileName(directory), StringComparer.Ordinal);
            foreach (var directory in packs)
                sources.Add(Read(Path.Combine(directory, ManifestFile), directory));
        }

        return sources;
    }

    /// <summary>Projects one <c>pack.json</c> into the gate's view of its pack.</summary>
    public static PackCatalogSource Read(string manifestPath, string packRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        var id = String(root, "id") ?? Path.GetFileName(packRoot);
        var fixtures = Path.Combine(packRoot, "eval", "fixtures");

        return new PackCatalogSource(
            id,
            String(root, "version") ?? PackCatalogSource.UnmanifestedVersion,
            String(root, "kind") ?? PackCatalogSource.SpecialistKind,
            root.TryGetProperty("dependsOn", out var dependsOn) && dependsOn.ValueKind == JsonValueKind.Array
                ? dependsOn.EnumerateArray()
                    .Select(entry => String(entry, "id"))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToArray()
                : Array.Empty<string>(),
            Path.Combine(packRoot, "Agents"),
            Directory.Exists(fixtures) ? fixtures : null,
            ReadPermissions(root),
            ReadReceiptEmitters(root),
            root.TryGetProperty("teamMembership", out var membership)
                ? ReadStringListMap(membership)
                : null);
    }

    /// <summary>
    /// The two ceilings §7.5 and §7.6 check against. Absent <c>permissions</c> yields null, which
    /// the gate reports as "no ceiling declared" rather than as an empty ceiling everything
    /// violates — a manifest that forgot the block is a manifest bug for the parser to reject, not
    /// a reason for the catalog to invent 40 spurious errors.
    /// </summary>
    private static PackPermissionsView? ReadPermissions(JsonElement root) =>
        root.TryGetProperty("permissions", out var permissions) &&
        permissions.ValueKind == JsonValueKind.Object
            ? new PackPermissionsView(
                ReadStringList(permissions, "actions"),
                ReadStringList(permissions, "allowedWriteGlobs"))
            : null;

    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? ReadReceiptEmitters(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return ReadReceiptEmitters(document.RootElement);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? ReadReceiptEmitters(JsonElement root) =>
        root.TryGetProperty(ReceiptEmitterTable.FieldName, out var emitters)
            ? ReadStringListMap(emitters)
            : null;

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>>? ReadStringListMap(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.EnumerateObject()
            .Where(property => !property.Name.StartsWith('_'))
            .ToDictionary(
                property => property.Name,
                property => (IReadOnlyList<string>)Strings(property.Value).ToArray(),
                StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? Strings(value).ToArray() : Array.Empty<string>();

    private static IEnumerable<string> Strings(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.String)
                .Select(entry => entry.GetString()!)
            : Enumerable.Empty<string>();

    private static string? String(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
