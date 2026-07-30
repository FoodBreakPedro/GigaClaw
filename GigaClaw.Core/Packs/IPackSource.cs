using System.Reflection;

namespace GigaClaw.Core.Packs;

/// <summary>
/// Where one pack's bytes come from. Q1 makes packs repo-only for O7, so in production every
/// source is <see cref="EmbeddedPackSource"/>; <see cref="DirectoryPackSource"/> exists for the
/// build-time tools (<c>GigaClaw.Catalog</c> composes from the working tree) and for tests.
///
/// The composition rule is the one <c>GigaClaw.Core.csproj</c> already encodes with its two
/// <c>LogicalName</c> prefixes, generalized per pack (§2): <c>Agents/**</c> composes into
/// <c>&lt;workspace&gt;/.agents/</c>, <c>eval/**</c> is build-time only and never reaches a
/// workspace, and everything else composes into the workspace root.
/// </summary>
public interface IPackSource
{
    /// <summary>Pack id, which must equal the manifest's <c>id</c> and the directory name.</summary>
    string Id { get; }

    /// <summary>Raw <c>pack.json</c> text.</summary>
    string ReadManifest();

    /// <summary>Paths under <c>Agents/</c>, forward-slashed, sorted ordinal.</summary>
    IReadOnlyList<string> AgentRelativePaths();

    /// <summary>Paths destined for the workspace root, forward-slashed, sorted ordinal.</summary>
    IReadOnlyList<string> RootRelativePaths();

    byte[] ReadAgentAsset(string relativePath);

    byte[] ReadRootAsset(string relativePath);
}

/// <summary>A pack read from a directory laid out per §2 (<c>pack.json</c>, <c>Agents/</c>, <c>eval/</c>, root files).</summary>
public sealed class DirectoryPackSource : IPackSource
{
    private readonly string _root;

    public DirectoryPackSource(string packDirectory)
    {
        _root = Path.GetFullPath(packDirectory);
        Id = new DirectoryInfo(_root).Name;
    }

    public string Id { get; }

    public string ReadManifest()
    {
        var path = Path.Combine(_root, "pack.json");
        if (!File.Exists(path))
            throw new PackValidationException($"pack '{Id}': no pack.json at {path}.");
        return File.ReadAllText(path);
    }

    public IReadOnlyList<string> AgentRelativePaths()
    {
        var agentsDir = Path.Combine(_root, "Agents");
        if (!Directory.Exists(agentsDir)) return Array.Empty<string>();
        return Enumerate(agentsDir, agentsDir);
    }

    public IReadOnlyList<string> RootRelativePaths()
    {
        if (!Directory.Exists(_root)) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(_root, file).Replace('\\', '/');
            if (rel == "pack.json") continue;
            if (rel.StartsWith("Agents/", StringComparison.Ordinal)) continue;
            // eval/** is build-time only — fixtures ship with the pack but never reach a workspace.
            if (rel.StartsWith("eval/", StringComparison.Ordinal)) continue;
            list.Add(rel);
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    public byte[] ReadAgentAsset(string relativePath) =>
        File.ReadAllBytes(Path.Combine(_root, "Agents", ToNativePath(relativePath)));

    public byte[] ReadRootAsset(string relativePath) =>
        File.ReadAllBytes(Path.Combine(_root, ToNativePath(relativePath)));

    private static string ToNativePath(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar);

    private static List<string> Enumerate(string root, string baseDir)
    {
        var list = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            list.Add(Path.GetRelativePath(baseDir, file).Replace('\\', '/'));
        list.Sort(StringComparer.Ordinal);
        return list;
    }
}

/// <summary>
/// A pack read from embedded resources — the production shape under Q1. Resource names are
/// <c>&lt;agentsPrefix&gt;&lt;path&gt;</c> and <c>&lt;rootPrefix&gt;&lt;path&gt;</c>.
///
/// The separator probing is load-bearing and not defensive coding: MSBuild's
/// <c>%(RecursiveDir)</c> yields backslashes on Windows and forward slashes elsewhere, so embedded
/// logical names differ by build OS (§6 hazard note). <c>AgentsTemplateService.ReadAsset</c>
/// already probes both; so does this.
/// </summary>
public sealed class EmbeddedPackSource : IPackSource
{
    private readonly Assembly _assembly;
    private readonly string _agentsPrefix;
    private readonly string _rootPrefix;
    private readonly string _manifestResourceName;

    public EmbeddedPackSource(
        string id,
        Assembly assembly,
        string agentsPrefix,
        string rootPrefix,
        string manifestResourceName)
    {
        Id = id;
        _assembly = assembly;
        _agentsPrefix = agentsPrefix;
        _rootPrefix = rootPrefix;
        _manifestResourceName = manifestResourceName;
    }

    public string Id { get; }

    public string ReadManifest()
    {
        using var stream = _assembly.GetManifestResourceStream(_manifestResourceName)
            ?? throw new PackValidationException(
                $"pack '{Id}': embedded manifest '{_manifestResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public IReadOnlyList<string> AgentRelativePaths() => Enumerate(_agentsPrefix);

    public IReadOnlyList<string> RootRelativePaths() => Enumerate(_rootPrefix);

    public byte[] ReadAgentAsset(string relativePath) => Read(_agentsPrefix, relativePath);

    public byte[] ReadRootAsset(string relativePath) => Read(_rootPrefix, relativePath);

    private IReadOnlyList<string> Enumerate(string prefix)
    {
        var list = new List<string>();
        foreach (var name in _assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            list.Add(name[prefix.Length..].Replace('\\', '/'));
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private byte[] Read(string prefix, string relativePath)
    {
        var names = _assembly.GetManifestResourceNames();
        var name = prefix + relativePath.Replace('/', '\\');
        if (!names.Contains(name)) name = prefix + relativePath.Replace('\\', '/');
        using var stream = _assembly.GetManifestResourceStream(name)
            ?? throw new PackValidationException($"pack '{Id}': embedded asset not found: {name}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
