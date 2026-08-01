using System.Reflection;

namespace GigaClaw.Core.Packs;

/// <summary>
/// Discovery for the production pack shape (doc/pack-infrastructure.md §2, owner Q1).
///
/// <para>Packs are repo-only: they ship inside <c>GigaClaw.Core.dll</c> and are selected at
/// Initialize, so an installed application has no directory to read a pack from. Every pack is
/// embedded under <see cref="EmbeddedPrefix"/> as a verbatim image of its directory —
/// <c>&lt;id&gt;/pack.json</c>, <c>&lt;id&gt;/Agents/**</c> and the pack's workspace-root files at
/// <c>&lt;id&gt;/&lt;path&gt;</c> — with <c>eval/**</c> left behind at build time because §2 makes
/// fixtures build-time only.</para>
///
/// <para><c>core</c> is the one exception, and it is a naming exception only: D1 leaves
/// <c>ProjectTemplate/</c> where it is, so its content keeps the two long-standing
/// <c>AgentsTemplate</c> prefixes and only its manifest sits under this one.
/// <see cref="CorePack.Source"/> maps those three prefixes onto one source, and
/// <see cref="Embedded"/> hands core off to it so callers never have to know.</para>
/// </summary>
public static class PackSources
{
    public const string EmbeddedPrefix = "GigaClaw.Core.Packs/";

    private const string ManifestFile = "pack.json";

    /// <summary>One embedded pack by id. No I/O and no validation — a wrong id fails when the
    /// manifest is read, which is where every other manifest problem is reported too.</summary>
    public static IPackSource Embedded(string id, Assembly? assembly = null)
    {
        var asm = assembly ?? typeof(PackSources).Assembly;
        if (string.Equals(id, CorePack.Id, StringComparison.Ordinal)) return CorePack.Source(asm);

        var packPrefix = EmbeddedPrefix + id + "/";
        return new EmbeddedPackSource(
            id,
            asm,
            agentsPrefix: packPrefix + "Agents/",
            rootPrefix: packPrefix,
            manifestResourceName: packPrefix + ManifestFile,
            // The root prefix is the pack root, so it also spans the manifest and the agent tree.
            // §2 says neither is workspace-root content; eval/ is excluded at build time but is
            // named here too so the rule reads the same in both sources.
            rootExclusions: [ManifestFile, "Agents/", "eval/"]);
    }

    /// <summary>
    /// Every embedded pack, <c>core</c> first and the rest by id ascending ordinal — the order §4
    /// requires of the composer, so a caller can pass the result straight to
    /// <see cref="PackComposer.Compose"/> or <see cref="PackInstaller.InstallAsync"/>.
    /// </summary>
    public static IReadOnlyList<IPackSource> DiscoverEmbedded(Assembly? assembly = null)
    {
        var asm = assembly ?? typeof(PackSources).Assembly;
        var ids = EmbeddedIds(asm);
        return [.. ids.Select(id => Embedded(id, asm))];
    }

    /// <summary>The ids of every embedded pack, core first then ordinal.</summary>
    public static IReadOnlyList<string> EmbeddedIds(Assembly? assembly = null)
    {
        var asm = assembly ?? typeof(PackSources).Assembly;
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames())
        {
            // Separators are normalized on both sides: MSBuild's %(RecursiveDir) yields
            // backslashes on Windows, so the same resource has a different literal name per build
            // OS (§6 hazard note).
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(EmbeddedPrefix, StringComparison.Ordinal)) continue;

            var relative = normalized[EmbeddedPrefix.Length..];
            var slash = relative.IndexOf('/');
            if (slash <= 0) continue;
            if (!string.Equals(relative[(slash + 1)..], ManifestFile, StringComparison.Ordinal)) continue;
            ids.Add(relative[..slash]);
        }

        var ordered = new List<string>();
        if (ids.Remove(CorePack.Id)) ordered.Add(CorePack.Id);
        ordered.AddRange(ids);
        return ordered;
    }
}
