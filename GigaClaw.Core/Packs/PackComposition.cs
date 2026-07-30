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
}

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
