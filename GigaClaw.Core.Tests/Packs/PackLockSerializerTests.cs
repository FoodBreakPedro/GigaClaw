using GigaClaw.Core.Packs;

namespace GigaClaw.Core.Tests.Packs;

/// <summary>
/// The lockfile is committed and reviewed alongside the workspace (D6), so it has to round-trip
/// exactly and it has to be written in a stable order — a reshuffled diff on every install would
/// make it unreviewable, which is the whole reason it lives in the workspace instead of a
/// registry column.
/// </summary>
public sealed class PackLockSerializerTests
{
    private static PackLockFile Sample() => new(
        PackRuntime.LockSchemaVersion,
        "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
        DateTimeOffset.Parse("2026-07-30T12:00:00Z"),
        PackRuntime.Version,
        new[]
        {
            new PackLockEntry(
                "security-assurance",
                "1.0.0",
                PackKind.Specialist,
                Removable: true,
                new PackRuntimeRequirement(1, 1),
                new[] { new PackDependency("core", "1.0.0") },
                new[] { "security-auditor", "secrets-reviewer" },
                new[] { "security-gate-on-review" },
                new[] { "security-review" },
                new[] { "security-auditor" },
                new[] { "security-auditor" },
                new[]
                {
                    new PackAutomationPatch(
                        "assignee-dispatch",
                        PackAutomationPatch.OpAddAssignees,
                        new[] { "security-auditor" },
                        Array.Empty<string>()),
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [".agents/security-auditor/SKILL.md"] = "sha256:aa",
                    [".agents/secrets-reviewer/SKILL.md"] = "sha256:bb",
                },
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
                {
                    ["automations.json"] = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["security-gate-on-review"] = "sha256:cc",
                    },
                }),
        });

    [Fact]
    public void Lockfile_round_trips_every_field()
    {
        var parsed = PackLockSerializer.Parse(PackLockSerializer.ToJson(Sample()));

        Assert.Equal(PackRuntime.LockSchemaVersion, parsed.SchemaVersion);
        Assert.Equal("3f2504e0-4f89-11d3-9a0c-0305e82c3301", parsed.InstallId);
        Assert.Equal(DateTimeOffset.Parse("2026-07-30T12:00:00Z"), parsed.InstalledAtUtc);
        Assert.Equal(PackRuntime.Version, parsed.PackRuntimeVersion);

        var entry = Assert.Single(parsed.Packs);
        Assert.Equal("security-assurance", entry.Id);
        Assert.Equal(PackKind.Specialist, entry.Kind);
        Assert.True(entry.Removable);
        Assert.Equal(new PackRuntimeRequirement(1, 1), entry.RequiresRuntime);
        Assert.Equal(new PackDependency("core", "1.0.0"), Assert.Single(entry.DependsOn));
        Assert.Equal(new[] { "security-auditor", "secrets-reviewer" }, entry.Agents);
        Assert.Equal(new[] { "security-review" }, entry.Teams);
        Assert.Equal("sha256:aa", entry.FileHashes[".agents/security-auditor/SKILL.md"]);
        Assert.Equal("sha256:cc", entry.MergeEntryHashes["automations.json"]["security-gate-on-review"]);

        var patch = Assert.Single(entry.AutomationPatches);
        Assert.Equal(PackAutomationPatch.OpAddAssignees, patch.Op);
        Assert.Equal(new[] { "security-auditor" }, patch.Slugs);
        Assert.Empty(patch.Labels);
    }

    [Fact]
    public void Lockfile_is_written_deterministically()
    {
        Assert.Equal(PackLockSerializer.ToJson(Sample()), PackLockSerializer.ToJson(Sample()));

        var json = PackLockSerializer.ToJson(Sample());
        // fileHashes keys are emitted in ordinal order, not insertion order.
        Assert.True(
            json.IndexOf(".agents/secrets-reviewer/SKILL.md", StringComparison.Ordinal)
            < json.IndexOf(".agents/security-auditor/SKILL.md", StringComparison.Ordinal));
    }

    [Fact]
    public void Lockfile_from_a_newer_core_is_refused_rather_than_guessed_at()
    {
        var json = PackLockSerializer.ToJson(Sample()).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");

        var error = Assert.Throws<PackValidationException>(() => PackLockSerializer.Parse(json));

        // Fail closed: a lockfile this build cannot reason about must never drive deletions.
        Assert.Contains("schemaVersion 2 is not supported", error.Message);
    }

    [Fact]
    public void Lockfile_that_is_not_json_is_refused()
    {
        Assert.Throws<PackValidationException>(() => PackLockSerializer.Parse("{ nope"));
    }
}
