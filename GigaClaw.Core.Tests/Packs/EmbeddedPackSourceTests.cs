using GigaClaw.Core.Packs;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Packs;

/// <summary>
/// Owner Q1 makes packs repo-only: they ship inside <c>GigaClaw.Core.dll</c> and are selected at
/// Initialize. An installed application therefore has no <c>Packs/</c> directory to read from, so
/// "the pack is in the repository" is not the same claim as "the pack can be installed" — the
/// embedded image is the only production source, and these tests are what keep it equal to the
/// tree it was built from.
/// </summary>
public sealed class EmbeddedPackSourceTests
{
    private static readonly string PacksRoot =
        Path.Combine(PythonContractRunner.RepositoryRoot, "Packs");

    private static IReadOnlyList<string> RepositoryPackIds() =>
        Directory.Exists(PacksRoot)
            ? [.. Directory.EnumerateDirectories(PacksRoot)
                .Where(directory => File.Exists(Path.Combine(directory, "pack.json")))
                .Select(Path.GetFileName)
                .OrderBy(id => id, StringComparer.Ordinal)!]
            : [];

    [Fact]
    public void Every_pack_in_the_repository_is_embedded_core_first()
    {
        var expected = new List<string> { CorePack.Id };
        expected.AddRange(RepositoryPackIds());

        Assert.Equal(expected, PackSources.EmbeddedIds());
    }

    [Fact]
    public void The_embedded_image_of_a_pack_is_byte_identical_to_its_directory()
    {
        foreach (var id in RepositoryPackIds())
        {
            var directory = new DirectoryPackSource(Path.Combine(PacksRoot, id));
            var embedded = PackSources.Embedded(id);

            Assert.Equal(directory.ReadManifest(), embedded.ReadManifest());
            Assert.Equal(directory.AgentRelativePaths(), embedded.AgentRelativePaths());
            Assert.Equal(directory.RootRelativePaths(), embedded.RootRelativePaths());

            foreach (var relative in directory.AgentRelativePaths())
                Assert.Equal(directory.ReadAgentAsset(relative), embedded.ReadAgentAsset(relative));
            foreach (var relative in directory.RootRelativePaths())
                Assert.Equal(directory.ReadRootAsset(relative), embedded.ReadRootAsset(relative));
        }
    }

    /// <summary>
    /// §2: <c>eval/**</c> is build-time only. Fixtures ship with the pack in the repository so the
    /// pack stays reviewable and removable as one directory, but they are replay inputs for
    /// <c>GigaClaw.Eval</c> — a workspace has no use for them, and embedding them would put the
    /// pack's own test data into every initialized project.
    /// </summary>
    [Fact]
    public void Pack_eval_fixtures_are_never_embedded()
    {
        var evalResources = typeof(PackSources).Assembly.GetManifestResourceNames()
            .Select(name => name.Replace('\\', '/'))
            .Where(name => name.StartsWith(PackSources.EmbeddedPrefix, StringComparison.Ordinal))
            .Where(name => name.Contains("/eval/", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(evalResources);
    }

    /// <summary>The manifest and the agent tree reach a workspace through their own routes; only
    /// what §2 calls a root file may land in the workspace root.</summary>
    [Fact]
    public void The_pack_root_view_excludes_the_manifest_and_the_agent_tree()
    {
        foreach (var id in RepositoryPackIds())
        {
            var rootPaths = PackSources.Embedded(id).RootRelativePaths();

            Assert.DoesNotContain("pack.json", rootPaths);
            Assert.DoesNotContain(rootPaths, path => path.StartsWith("Agents/", StringComparison.Ordinal));
            Assert.DoesNotContain(rootPaths, path => path.StartsWith("eval/", StringComparison.Ordinal));
        }
    }
}
