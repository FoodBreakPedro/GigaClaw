using System.Text.Json.Nodes;
using GigaClaw.Core.Automation;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Packs;

/// <summary>One opaque pack file and where it lands, as a workspace-relative forward-slashed path.</summary>
public sealed record ComposedFile(string DestinationPath, string PackId, byte[] Content);

/// <summary>
/// One pack after its tree has been walked and verified against its manifest.
///
/// Opaque files (agent directories, scripts, root files) are carried in <see cref="Files"/> and
/// end up hashed into the lockfile. The four <em>merge artifacts</em> — <c>automations.json</c>,
/// <c>contracts.json</c>, <c>models.json</c>, <c>teams.json</c> — are carried as parsed fragments
/// instead, because no single pack owns those files on disk: they are merged with whatever the
/// workspace already has (including the owner's edits, which the automation editor writes straight
/// back through <c>AutomationStore.SaveAsync</c>). That split is why the lockfile records
/// <c>automations</c>/<c>contractKeys</c>/<c>modelKeys</c>/<c>teams</c> as key lists separately
/// from <c>fileHashes</c>.
/// </summary>
public sealed record ComposedPack(
    PackManifest Manifest,
    PackCompatibility Compatibility,
    IReadOnlyList<ComposedFile> Files,
    IReadOnlyList<AutomationRule> Automations,
    JsonNode? ContractDefaults,
    IReadOnlyDictionary<string, JsonNode> ContractAgents,
    IReadOnlyDictionary<string, JsonNode> Models,
    IReadOnlyDictionary<string, JsonObject> Teams)
{
    public string Id => Manifest.Id;

    /// <summary>
    /// Non-slug (<c>_</c>-prefixed) keys from the pack's <c>models.json</c> — the file's own
    /// documentation. Carried separately so it is neither mistaken for an agent mapping by
    /// ownership and collision checks nor lost on the way into the workspace. Core-only when
    /// applied, for the same reason <see cref="ContractDefaults"/> is.
    /// </summary>
    public IReadOnlyDictionary<string, JsonNode> ModelsPreamble { get; init; } =
        new Dictionary<string, JsonNode>(StringComparer.Ordinal);

    /// <summary>
    /// <see cref="Teams"/> in the order the pack's <c>teams.json</c> declares them. Team order is
    /// user-visible — it is the order of the board's team filter — so it is carried explicitly
    /// rather than left to dictionary enumeration or re-derived by sorting the slugs.
    /// </summary>
    public IReadOnlyList<string> TeamOrder { get; init; } = [];

    /// <summary>
    /// The top-level knobs of the pack's <c>automations.json</c> — everything beside the automation
    /// array. Carried because <see cref="Automations"/> alone loses them: the installed file is
    /// rebuilt from the workspace's own config plus each pack's automation list, so a default the
    /// template ships (the per-ticket spend cap) would silently not reach a new workspace.
    /// Core-only when applied, for the same reason <see cref="ContractDefaults"/> is — a feature
    /// pack contributes automations, never the project's budget.
    /// </summary>
    public AutomationDefaults Defaults { get; init; } = new(null, null, null);
}

/// <summary>Project-wide automation settings a pack ships alongside its automations.</summary>
public sealed record AutomationDefaults(
    decimal? DailyBudgetUsd,
    decimal? MaxTicketCostUsd,
    int? MinDescriptionLength);

/// <summary>The whole selection, validated and ordered: core first, then topological by
/// <c>dependsOn</c>, ties broken by id ascending ordinal (§4).</summary>
public sealed record PackComposition(
    IReadOnlyList<ComposedPack> Packs,
    int RuntimeVersion)
{
    public ComposedPack? Find(string id) =>
        Packs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>Packs whose <c>requiresRuntime.max</c> is below the current runtime. Installed,
    /// but force-disabled and refused at dispatch until updated (§5).</summary>
    public IReadOnlyList<ComposedPack> Quarantined =>
        Packs.Where(p => p.Compatibility != PackCompatibility.Compatible).ToList();
}

/// <summary>
/// Knobs for <see cref="PackComposer.Compose"/>. Kept tiny on purpose.
///
/// <see cref="HostProvidedAgents"/> is the transitional seam for the core-pack extraction. D4 says
/// a cross-pack reference must resolve to the referencing pack, <c>core</c>, or a declared
/// dependency — but until <c>ProjectTemplate/</c> becomes the <c>core</c> pack, its 33 agents are
/// written by <c>AgentsTemplateService</c> and belong to no manifest. Callers that compose against
/// such a workspace pass those slugs here; the catalog, which always composes the full manifest
/// set, passes nothing. When core becomes a pack this stays empty at every call site and the rule
/// is D4 unmodified.
/// </summary>
public sealed record PackComposeOptions(
    int RuntimeVersion = PackRuntime.Version,
    IReadOnlySet<string>? HostProvidedAgents = null);
