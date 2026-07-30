namespace GigaClaw.Catalog;

/// <summary>
/// The declared ceilings the CI gate checks a pack against (doc/pack-infrastructure.md §7.5, §7.6).
/// A ceiling is a declaration, never a grant: it exists so a reviewer reading only
/// <c>pack.json</c> can see the widest write scope and the widest action vocabulary the pack can
/// ever reach, and so the catalog can prove the pack's own data stays inside them.
/// </summary>
public sealed record PackPermissionsView(
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> AllowedWriteGlobs);

/// <summary>
/// One pack as the catalog gate needs to see it: where its data sits on disk plus the manifest
/// declarations the gate enforces.
///
/// <para>
/// This is deliberately a <em>view</em> of a composed pack and not a second composer.
/// <c>GigaClaw.Core/Packs/PackComposer</c> owns composition — slug collisions, declared-and-verified
/// <c>provides</c>, cross-pack reference resolution, ordering. The catalog only needs to know which
/// agent belongs to which pack and what that pack declared, so it takes those facts as input and
/// stays compilable in a tree where the composer does not exist yet.
/// </para>
///
/// <para>
/// <b>Integration point.</b> When <c>ProjectTemplate/</c> becomes pack <c>core</c>, the caller
/// builds this list by projecting <c>PackComposition.Packs</c> — one <see cref="PackCatalogSource"/>
/// per <c>ComposedPack</c>, taking <see cref="Permissions"/>, <see cref="ReceiptEmitters"/> and
/// <see cref="TeamMembership"/> straight off <c>ComposedPack.Manifest</c> — and passes it to
/// <see cref="CatalogGenerator.Build"/>. Nothing else in this project changes: the gate already
/// runs over the list. Until then <see cref="HostTemplate"/> supplies the single un-manifested
/// entry and the catalog behaves exactly as it did before packs existed.
/// </para>
/// </summary>
/// <param name="Id">Pack id. <c>core</c> for the host template.</param>
/// <param name="Version">Pack semver, or <see cref="UnmanifestedVersion"/> for the host template.</param>
/// <param name="Kind"><c>core</c> or <c>specialist</c>.</param>
/// <param name="DependsOn">Ids from <c>dependsOn</c>, for the catalog's pack listing.</param>
/// <param name="AgentsDirectory">Absolute path to the pack's <c>Agents/</c> directory.</param>
/// <param name="FixtureDirectory">
/// Absolute path to the pack's replay fixtures, or null when it ships none. Core's fixtures live
/// in <c>GigaClaw.Eval/fixtures</c>; a pack's live in <c>Packs/&lt;id&gt;/eval/fixtures</c> (§9),
/// because a pack has to be reviewable and removable as one directory.
/// </param>
/// <param name="Permissions">
/// Manifest ceilings, or null for the un-manifested host template — there is no ceiling to check a
/// subset against, so §7.5/§7.6 are reported as not-applicable rather than as passing.
/// </param>
/// <param name="ReceiptEmitters">
/// <c>receiptEmitters</c>: which of this pack's agents may be the source of a receipt family other
/// agents consume. Unioned across packs by <see cref="ReceiptEmitterTable"/> (§7.3).
/// </param>
/// <param name="TeamMembership">
/// <c>teamMembership</c>: additive membership in teams owned by core or a dependency. Never removes.
/// </param>
public sealed record PackCatalogSource(
    string Id,
    string Version,
    string Kind,
    IReadOnlyList<string> DependsOn,
    string AgentsDirectory,
    string? FixtureDirectory = null,
    PackPermissionsView? Permissions = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ReceiptEmitters = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? TeamMembership = null)
{
    public const string CoreId = "core";
    public const string CoreKind = "core";
    public const string SpecialistKind = "specialist";

    /// <summary>
    /// Stands in for <c>pack.json</c> <c>version</c> on the host template, which has no manifest
    /// yet. Distinguishable from any real pack version, which §3 requires to be a valid semver a
    /// pack author bumps.
    /// </summary>
    public const string UnmanifestedVersion = "0.0.0";

    public bool IsCore => string.Equals(Kind, CoreKind, StringComparison.Ordinal);

    /// <summary>True once this pack carries a manifest, i.e. once §7.5/§7.6 have something to check.</summary>
    public bool HasManifest => Permissions is not null;

    /// <summary>
    /// The single un-manifested source that reproduces the pre-pack catalog exactly:
    /// <c>ProjectTemplate/Agents</c> for agent data, <c>GigaClaw.Eval/fixtures</c> for replay
    /// fixtures, and the transitional core emitter table (§7.3) for receipt families.
    /// </summary>
    public static PackCatalogSource HostTemplate(string repositoryRoot) => new(
        CoreId,
        UnmanifestedVersion,
        CoreKind,
        Array.Empty<string>(),
        Path.Combine(repositoryRoot, "ProjectTemplate", "Agents"),
        Path.Combine(repositoryRoot, "GigaClaw.Eval", "fixtures"),
        Permissions: null,
        ReceiptEmitters: ReceiptEmitterTable.ReadCoreTable(repositoryRoot));
}
