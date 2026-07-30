using System.Reflection;

namespace GigaClaw.Core.Packs;

/// <summary>
/// Pack <c>core</c> — the bundle <c>ProjectTemplate/</c> has always been, now carrying a manifest
/// (doc/pack-infrastructure.md §6).
///
/// <para>D1 leaves <c>ProjectTemplate/</c> where it is rather than moving it under <c>Packs/</c>,
/// so core is the one pack whose content is not embedded under the <c>GigaClaw.Core.Packs/</c>
/// prefix: its <c>Agents/</c> tree and its workspace-root files keep the two long-standing
/// <c>AgentsTemplate</c> prefixes, and only <c>pack.json</c> is new. This type is the adapter that
/// presents those three prefixes as one <see cref="IPackSource"/>, which is why the literal path
/// <c>ProjectTemplate/Agents</c> can stay hardcoded in <c>CatalogGenerator</c>,
/// <c>StaticEvalRunner</c>, <c>evalconfig.json</c> and the template contract tests.</para>
/// </summary>
public static class CorePack
{
    public const string Id = "core";

    public const string AgentsPrefix = "GigaClaw.Core.AgentsTemplate/";
    public const string RootPrefix = "GigaClaw.Core.AgentsTemplateRoot/";
    public const string ManifestResourceName = "GigaClaw.Core.Packs/core/pack.json";

    /// <summary>The embedded core pack. Q1 makes packs repo-only, so this is the production shape.</summary>
    public static IPackSource Source(Assembly? assembly = null) =>
        new EmbeddedPackSource(
            Id,
            assembly ?? typeof(CorePack).Assembly,
            AgentsPrefix,
            RootPrefix,
            ManifestResourceName);
}
