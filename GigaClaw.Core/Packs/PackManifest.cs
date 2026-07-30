namespace GigaClaw.Core.Packs;

/// <summary>
/// The pack-runtime version (doc/pack-infrastructure.md §5): the composition contract this build
/// of <c>GigaClaw.Core</c> honours — the manifest fields the installer reads, the automation
/// trigger/condition/action vocabulary, and the contract/verdict/handoff schema versions.
/// Owned by core, deliberately an integer, and written into <c>packs.lock.json</c>.
/// </summary>
public static class PackRuntime
{
    /// <summary>Current pack-runtime version. Bump when the composition contract changes.</summary>
    public const int Version = 1;

    /// <summary>The only <c>pack.json</c> <c>schemaVersion</c> this build parses.</summary>
    public const int ManifestSchemaVersion = 1;

    /// <summary>The only <c>packs.lock.json</c> <c>schemaVersion</c> this build reads or writes.</summary>
    public const int LockSchemaVersion = 1;
}

public enum PackKind
{
    /// <summary>Exactly one pack may declare this. Never removable.</summary>
    Core,
    Specialist,
}

/// <summary>
/// How a pack's <c>requiresRuntime</c> bounds line up with the current <see cref="PackRuntime.Version"/>.
/// Per §5 the mismatch is never repaired automatically in either direction.
/// </summary>
public enum PackCompatibility
{
    Compatible,

    /// <summary>
    /// <c>requiresRuntime.max &lt; currentRuntime</c>. Files stay on disk, automations are
    /// force-disabled at config load, agents are refused at dispatch, the board says "needs update".
    /// </summary>
    QuarantinedTooOld,

    /// <summary><c>requiresRuntime.min &gt; currentRuntime</c>. Install is refused outright.</summary>
    RefusedTooNew,
}

/// <summary>Inclusive integer bounds on the pack-runtime version (§3, D7). No range grammar.</summary>
public sealed record PackRuntimeRequirement(int Min, int Max)
{
    public PackCompatibility Evaluate(int runtimeVersion) =>
        runtimeVersion < Min ? PackCompatibility.RefusedTooNew
        : runtimeVersion > Max ? PackCompatibility.QuarantinedTooOld
        : PackCompatibility.Compatible;
}

/// <summary>A pack-to-pack dependency. Minimum version only — D7 forbids ranges.</summary>
public sealed record PackDependency(string Id, string MinVersion);

/// <summary>
/// The declared inventory. Declared <em>and</em> verified: <see cref="PackComposer"/> walks the
/// pack tree and fails on a mismatch in either direction, which is what makes a manifest
/// reviewable on its own and makes uninstall an explicit set rather than a re-walk (§3).
/// </summary>
public sealed record PackProvides(
    IReadOnlyList<string> Agents,
    IReadOnlyList<string> Scripts,
    IReadOnlyList<string> Teams,
    IReadOnlyList<string> Automations,
    IReadOnlyList<string> RootFiles);

/// <summary>Declared ceiling, never a grant (§3). Enforcement lives in the composer and, for
/// <see cref="NetworkHosts"/>, in the <c>ActionExecutor</c> preflight.</summary>
public sealed record PackPermissions(
    IReadOnlyList<string> RiskClasses,
    IReadOnlyList<string> Actions,
    string Network,
    IReadOnlyList<string> NetworkHosts,
    IReadOnlyList<string> AllowedWriteGlobs)
{
    public const string NetworkNone = "none";
    public const string NetworkDeclared = "declared";
}

/// <summary>
/// An additive edit to another pack's automation. v1 carries set-addition ops only — that is
/// precisely what makes uninstall reversible as a set subtraction (§4, D4). Reordering, removal
/// and trigger edits are refused at parse time.
/// </summary>
public sealed record PackAutomationPatch(
    string Automation,
    string Op,
    IReadOnlyList<string> Slugs,
    IReadOnlyList<string> Labels)
{
    public const string OpAddAssignees = "addAssignees";
    public const string OpAddLabels = "addLabels";

    /// <summary>The complete v1 op vocabulary. Anything else is refused, fail closed.</summary>
    public static readonly IReadOnlySet<string> SupportedOps =
        new HashSet<string>(StringComparer.Ordinal) { OpAddAssignees, OpAddLabels };
}

/// <summary><c>pack.json</c>, schemaVersion 1 (doc/pack-infrastructure.md §3).</summary>
public sealed record PackManifest(
    int SchemaVersion,
    string Id,
    string Name,
    string Description,
    string Version,
    PackKind Kind,
    bool Removable,
    PackRuntimeRequirement RequiresRuntime,
    IReadOnlyList<PackDependency> DependsOn,
    PackProvides Provides,
    IReadOnlyDictionary<string, IReadOnlyList<string>> TeamMembership,
    IReadOnlyList<PackAutomationPatch> AutomationPatches,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ReceiptEmitters,
    PackPermissions Permissions,
    IReadOnlyList<string> EvalFixtures);
