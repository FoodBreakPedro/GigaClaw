namespace GigaClaw.Core.Packs;

/// <summary>Install knobs. <see cref="OverwriteConflicts"/> mirrors
/// <c>AgentsTemplateService.InitializeAsync(workspace, overwriteConflicts)</c> so the two
/// writers behave the same way when a destination already exists.</summary>
public sealed record PackInstallOptions(
    bool OverwriteConflicts = false,
    int RuntimeVersion = PackRuntime.Version);

public sealed record PackInstallResult(
    string InstallId,
    IReadOnlyList<string> Written,

    /// <summary>Destinations left alone because the file already existed with different content
    /// and is not recorded as pack-owned. Never silently overwritten (§4).</summary>
    IReadOnlyList<string> PreservedOwnerEdits,

    /// <summary>Packs whose <c>requiresRuntime.max</c> is below this runtime. Installed, but the
    /// board must surface them as "needs update" and their agents refused at dispatch (§5).</summary>
    IReadOnlyList<string> QuarantinedPacks,

    /// <summary>
    /// <c>teamMembership</c> entries whose target team is not in the composed <c>teams.json</c>.
    /// Deferred rather than fatal only while the built-in teams are still C# constants; once D8
    /// lands and core ships <c>Agents/teams.json</c>, an unresolvable target becomes a hard error.
    /// </summary>
    IReadOnlyList<string> DeferredTeamMemberships,

    PackLockFile Lock);

public sealed record PackUninstallResult(
    string PackId,

    /// <summary>Files whose on-disk hash still matched the lockfile, so they were pack-owned and
    /// untouched, so they were deleted.</summary>
    IReadOnlyList<string> DeletedFiles,

    /// <summary>Files whose hash differed: the owner edited them. Left on disk and reported (§4).</summary>
    IReadOnlyList<string> OrphanedFiles,

    IReadOnlyList<string> RemovedAutomations,

    /// <summary>Owner-edited pack automations. Set <c>enabled:false</c> and reported, never
    /// deleted — an edited automation is owner work, and a dangling automation referencing a
    /// removed agent would fire and fail (§4 step 4).</summary>
    IReadOnlyList<string> DisabledAutomations,

    IReadOnlyList<string> RemovedContractKeys,
    IReadOnlyList<string> RemovedModelKeys,
    IReadOnlyList<string> RemovedTeams,

    /// <summary>Merge-artifact entries the owner edited, so they were left in place.</summary>
    IReadOnlyList<string> OrphanedMergeEntries,

    /// <summary>The pack's agent slugs. Members are NOT deleted — they carry
    /// <c>DefaultModel</c> and are referenced by run history and by <c>assignedTo</c> on
    /// historical tickets. The UI marks them orphaned instead (§4 step 5).</summary>
    IReadOnlyList<string> OrphanedMemberSlugs,

    PackLockFile Lock);
