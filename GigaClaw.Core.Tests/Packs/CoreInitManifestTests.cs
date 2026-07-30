using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    [Fact]
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

        Assert.True(
            missing.Count == 0 && added.Count == 0 && changed.Count == 0,
            Describe(missing, added, changed));
    }

    /// <summary>
    /// Runs today's Initialize into a throwaway workspace and hashes every file it leaves behind.
    ///
    /// <para>The whole tree is walked rather than the returned <c>Written</c> list, so a file written
    /// outside that list would still be caught. <c>.git/</c> is excluded because Initialize shells out
    /// to <c>git init</c>, whose output is neither template content nor deterministic.</para>
    /// </summary>
    internal static async Task<SortedDictionary<string, string>> CaptureInitOutputAsync()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "gigaclaw-core-init-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(workspace);
            await new AgentsTemplateService().InitializeAsync(workspace, overwriteConflicts: true);
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
        var sb = new StringBuilder("Initialize output drifted from the golden manifest.");
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
