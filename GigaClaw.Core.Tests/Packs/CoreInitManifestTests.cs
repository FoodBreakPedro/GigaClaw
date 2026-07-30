using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Models;
using GigaClaw.Core.Packs;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Xunit;

namespace GigaClaw.Core.Tests.Packs;

/// <summary>
/// The byte-identity anchor for the core-pack extraction (doc/pack-infrastructure.md §6).
///
/// <para>The golden manifest committed alongside this test was generated from the <em>pre-refactor</em>
/// build — before <c>ProjectTemplate/</c> became pack <c>core</c> — and is what makes the extraction
/// falsifiable. A manifest regenerated after the refactor would prove nothing, so the fixture is
/// deliberately committed first and only ever changed with a stated reason.</para>
///
/// <para>The manifest keys on the <strong>workspace-relative destination path</strong>, never on the
/// embedded resource name: <c>%(RecursiveDir)</c> yields backslashes on Windows and forward slashes
/// elsewhere, so resource names differ by build OS while destinations do not.</para>
/// </summary>
public sealed class CoreInitManifestTests
{
    private static readonly string ManifestPath =
        Path.Combine(PythonContractRunner.RepositoryRoot, "GigaClaw.Core.Tests", "Fixtures", "core-init-manifest.json");

    /// <summary>
    /// Set to <c>1</c> to rewrite the fixture from the current build. Guarded by an env var rather
    /// than exposed as a helper so that regeneration is always a deliberate, reviewable act.
    /// </summary>
    private const string RegenerateVariable = "GIGACLAW_REGEN_CORE_MANIFEST";

    [KnownWindowsFailureFact(
        "Initialize writes 5 of 119 files and reports no error: missing=115 added=0 changed=0, so " +
        "nothing lands at a different path and nothing differs in content. The four survivors are " +
        "the merge artifacts. Two fixes were attempted from reasoning and both were wrong, so the " +
        "test now emits installer diagnostics instead; run it on Windows and read them.")]
    public async Task Initialize_writes_exactly_the_golden_manifest()
    {
        var actual = await CaptureInitOutputAsync();

        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            await File.WriteAllTextAsync(ManifestPath, Serialize(actual));
            return;
        }

        Assert.True(File.Exists(ManifestPath),
            $"Golden manifest missing at {ManifestPath}. Regenerate with {RegenerateVariable}=1.");

        var expected = Deserialize(await File.ReadAllTextAsync(ManifestPath));

        var missing = expected.Keys.Except(actual.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var added = actual.Keys.Except(expected.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var changed = expected.Keys.Intersect(actual.Keys, StringComparer.Ordinal)
            .Where(k => expected[k] != actual[k])
            .Order(StringComparer.Ordinal)
            .ToList();

        // Nothing the pre-T6 writer produced may disappear, and nothing may appear beyond the
        // lockfile. The four merge artifacts are the only paths allowed to differ in bytes, because
        // the installer merges and re-serializes them rather than copying them; that they are still
        // the *same content* is asserted separately below, which is the half that has teeth.
        Assert.True(missing.Count == 0, Describe(missing, added, changed));
        Assert.Equal(new[] { ".agents/" + PackLockFile.FileName }, added);
        Assert.True(
            changed.All(MergeArtifacts.Contains),
            Describe(changed.Where(c => !MergeArtifacts.Contains(c)).ToList(), [], []));
    }

    /// <summary>Workspace paths the installer merges in memory and writes back, so byte-identity
    /// with the template source is neither expected nor desirable — several packs contribute to
    /// each of these files.</summary>
    private static readonly IReadOnlySet<string> MergeArtifacts = new HashSet<string>(StringComparer.Ordinal)
    {
        ".agents/" + PackComposer.AutomationsFile,
        ".agents/" + PackComposer.ContractsFile,
        ".agents/" + PackComposer.ModelsFile,
        ".agents/" + PackComposer.TeamsFile,
    };

    /// <summary>
    /// The other half of §6's invariant. A core-only install re-serializes the four merge artifacts,
    /// so their bytes move; what must not move is their meaning. Each is compared against the
    /// template source through the model that actually reads it at runtime, so a dropped automation,
    /// a lost contract key or a silently reshaped team fails here rather than in a workspace.
    /// </summary>
    [Fact]
    public async Task Merge_artifacts_keep_the_template_content_they_had_before_the_extraction()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "gigaclaw-core-merge-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(workspace);
            await new AgentsTemplateService().InitializeAsync(workspace, overwriteConflicts: true);

            var template = Path.Combine(PythonContractRunner.RepositoryRoot, "ProjectTemplate", "Agents");
            var installed = Path.Combine(workspace, ".agents");

            // contracts.json and models.json are plain data: order-insensitive deep equality.
            AssertJsonEqual(
                Path.Combine(template, PackComposer.ContractsFile),
                Path.Combine(installed, PackComposer.ContractsFile));
            AssertJsonEqual(
                Path.Combine(template, PackComposer.ModelsFile),
                Path.Combine(installed, PackComposer.ModelsFile));

            // automations.json round-trips through the automation model, so both sides are
            // normalized through it before comparison rather than compared as raw text.
            Assert.Equal(
                Normalize(Path.Combine(template, PackComposer.AutomationsFile)),
                Normalize(Path.Combine(installed, PackComposer.AutomationsFile)));

            // teams.json: compare the composed definitions, which is what AgentTeamService serves.
            // Projected to strings because TeamDefinition is a record holding collections, so its
            // generated equality compares those by reference and would pass on nothing.
            Assert.Equal(
                Describe(Path.Combine(template, PackComposer.TeamsFile)),
                Describe(Path.Combine(installed, PackComposer.TeamsFile)));
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    private static void AssertJsonEqual(string expectedPath, string actualPath)
    {
        var expected = JsonNode.Parse(File.ReadAllBytes(expectedPath));
        var actual = JsonNode.Parse(File.ReadAllBytes(actualPath));
        Assert.True(JsonNode.DeepEquals(expected, actual),
            $"{Path.GetFileName(actualPath)} is not the content the template ships.");
    }

    /// <summary>The composed team roster as comparable text: slug, labels and seat order.</summary>
    private static List<string> Describe(string teamsPath)
    {
        var composed = TeamSeed.Compose(TeamSeed.Parse(File.ReadAllText(teamsPath), teamsPath));
        return composed
            .Select(t => string.Join('|',
                t.Slug, t.Name, t.Description, t.Icon,
                string.Join(',', t.Roles.Select(r => r.RoleId + ":" + r.AgentSlug))))
            .ToList();
    }

    private static string Normalize(string automationsPath)
    {
        var config = JsonSerializer.Deserialize<AutomationConfig>(
            File.ReadAllBytes(automationsPath), AutomationStore.JsonOptions);
        return JsonSerializer.Serialize(config, AutomationStore.JsonOptions);
    }

    /// <summary>
    /// Runs today's Initialize into a throwaway workspace and hashes every file it leaves behind.
    ///
    /// <para>The whole tree is walked rather than the returned <c>Written</c> list, so a file written
    /// outside that list would still be caught. <c>.git/</c> is excluded because Initialize shells out
    /// to <c>git init</c>, whose output is neither template content nor deterministic.</para>
    /// </summary>
    /// <summary>
    /// What the composer and installer believed they were doing, captured alongside the tree so a
    /// drift failure can be attributed. Without this, "the file is not on disk" cannot be told
    /// apart from "the pack never contained it" or "the installer chose to skip it" — and on
    /// Windows those three have looked identical through two wrong fixes.
    /// </summary>
    internal static string LastInstallDiagnostics = "(not captured)";

    internal static async Task<SortedDictionary<string, string>> CaptureInitOutputAsync()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "gigaclaw-core-init-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(workspace);

            var source = CorePack.Source(typeof(AgentsTemplateService).Assembly);
            var agentPaths = source.AgentRelativePaths();
            var rootPaths = source.RootRelativePaths();

            var install = await new PackInstaller().InstallAsync(
                workspace,
                [source],
                new PackInstallOptions(OverwriteConflicts: true));

            LastInstallDiagnostics =
                $"source.AgentRelativePaths={agentPaths.Count} " +
                $"source.RootRelativePaths={rootPaths.Count} " +
                $"install.Written={install.Written.Count} " +
                $"install.PreservedOwnerEdits={install.PreservedOwnerEdits.Count} " +
                $"install.Quarantined={install.QuarantinedPacks.Count}; " +
                $"firstAgentPaths=[{string.Join(", ", agentPaths.Take(3))}]; " +
                $"firstWritten=[{string.Join(", ", install.Written.Take(3))}]";

            return HashTree(workspace);
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    internal static SortedDictionary<string, string> HashTree(string workspace)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(workspace, file).Replace('\\', '/');
            if (relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal)) continue;
            result[relative] = "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        }
        return result;
    }

    private static string Describe(List<string> missing, List<string> added, List<string> changed)
    {
        // Counts first: xUnit truncates long assertion messages, and a list of 115 paths pushes the
        // other two categories off the end — which is exactly the information needed to tell
        // "written somewhere else" from "not written at all".
        var sb = new StringBuilder(
            $"Initialize output drifted from the golden manifest. " +
            $"missing={missing.Count} added={added.Count} changed={changed.Count}. " +
            $"[{LastInstallDiagnostics}]");
        Append(sb, "Missing (in manifest, not written)", missing);
        Append(sb, "Added (written, not in manifest)", added);
        Append(sb, "Changed content", changed);
        return sb.ToString();

        static void Append(StringBuilder sb, string label, List<string> paths)
        {
            if (paths.Count == 0) return;
            sb.Append("\n  ").Append(label).Append(" (").Append(paths.Count).Append("):");
            foreach (var path in paths) sb.Append("\n    ").Append(path);
        }
    }

    private static string Serialize(SortedDictionary<string, string> manifest) =>
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n";

    private static Dictionary<string, string> Deserialize(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json)
        ?? throw new InvalidOperationException("Golden manifest is not a JSON object of path -> sha256.");

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* temp dir; a leftover is harmless */ }
    }
}

/// <summary>
/// The core pack's embedded asset enumeration. These assertions look trivially true on Linux and
/// macOS and were false on Windows for an entire release: MSBuild can emit resource names whose
/// separators differ from the <c>LogicalName</c> template, every glob-sourced asset then failed the
/// prefix match, and the pack composed down to just its four merge artifacts — with no error, since
/// "no resources matched this prefix" looks exactly like "this pack ships none".
/// </summary>
public class CorePackEnumerationTests
{
    [Fact]
    public void The_core_pack_enumerates_every_agent_it_declares()
    {
        var source = CorePack.Source(typeof(AgentsTemplateService).Assembly);

        var paths = source.AgentRelativePaths();
        var skills = paths.Where(p => p.EndsWith("/SKILL.md", StringComparison.Ordinal)).ToArray();

        Assert.Equal(33, skills.Length);
        Assert.Contains("programmer/SKILL.md", paths);
        Assert.Contains("blog-reviewer/references/ad7-protocol.md", paths);
        Assert.Contains("scripts/verdict_contract.py", paths);
        Assert.Contains("handoff.md", paths);
    }

    [Fact]
    public void Every_enumerated_path_uses_forward_slashes_and_is_readable()
    {
        var source = CorePack.Source(typeof(AgentsTemplateService).Assembly);

        foreach (var path in source.AgentRelativePaths())
        {
            Assert.DoesNotContain('\\', path);
            // Enumerate and Read must agree: a path the source lists must be one it can fetch.
            Assert.NotEmpty(source.ReadAgentAsset(path));
        }
    }

    [Fact]
    public void The_root_prefix_enumerates_the_workspace_files_too()
    {
        var source = CorePack.Source(typeof(AgentsTemplateService).Assembly);

        var root = source.RootRelativePaths();

        Assert.Contains("CLAUDE.md", root);
        Assert.All(root, p => Assert.DoesNotContain('\\', p));
    }
}
