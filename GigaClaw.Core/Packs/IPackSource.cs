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
            if (IsNoise(rel)) continue;
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

    /// <summary>
    /// Filesystem droppings that are gitignored but still sit in a working tree. This mirrors the
    /// reasoning behind the <c>__pycache__</c> Exclude in <c>GigaClaw.Core.csproj</c>: a directory
    /// source globs the <em>working directory</em>, not git, so anything the OS or an editor leaves
    /// behind becomes an undeclared pack file and fails composition. Finder writes .DS_Store into
    /// ProjectTemplate/ the first time anyone opens it, which is enough to redden the build on a
    /// machine where nothing is wrong.
    /// </summary>
    private static readonly HashSet<string> UntrackedNoise =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".DS_Store", "Thumbs.db", "desktop.ini", ".gitkeep", ".gitignore",
        };

    private static bool IsNoise(string relativePath)
    {
        var segments = relativePath.Split('/');
        return segments.Any(segment =>
            UntrackedNoise.Contains(segment) ||
            segment.Equals("__pycache__", StringComparison.Ordinal)) ||
            relativePath.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> Enumerate(string root, string baseDir)
    {
        var list = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            if (IsNoise(relative)) continue;
            list.Add(relative);
        }
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

    /// <summary>
    /// Resource names are matched with separators normalized on <em>both</em> sides.
    /// <para>
    /// The stripped remainder was already normalized, but the prefix comparison was not, and that
    /// asymmetry is a real Windows failure rather than a theoretical one: MSBuild builds these
    /// names from a <c>LogicalName</c> template containing a literal <c>/</c> plus
    /// <c>%(RecursiveDir)</c>, and on Windows the resulting name can carry backslashes where the
    /// template had a forward slash. Every glob-sourced asset then failed <c>StartsWith(prefix)</c>
    /// and silently vanished from the pack — an install that wrote only the four merge artifacts,
    /// with no error anywhere, because "no resources matched" is indistinguishable from
    /// "this pack ships none".
    /// </para>
    /// </summary>
    private IReadOnlyList<string> Enumerate(string prefix)
    {
        var normalizedPrefix = prefix.Replace('\\', '/');
        var list = new List<string>();
        foreach (var name in _assembly.GetManifestResourceNames())
        {
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(normalizedPrefix, StringComparison.Ordinal)) continue;
            list.Add(normalized[normalizedPrefix.Length..]);
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private byte[] Read(string prefix, string relativePath)
    {
        var names = _assembly.GetManifestResourceNames();
        var name = prefix + relativePath.Replace('/', '\\');
        if (!names.Contains(name)) name = prefix + relativePath.Replace('\\', '/');
        if (!names.Contains(name))
        {
            // Last resort: match on the fully normalized name. The two probes above assume the
            // prefix itself survived verbatim, which is exactly what Enumerate can no longer take
            // for granted.
            var wanted = (prefix + relativePath).Replace('\\', '/');
            name = names.FirstOrDefault(n => n.Replace('\\', '/') == wanted) ?? name;
        }
        using var stream = _assembly.GetManifestResourceStream(name)
            ?? throw new PackValidationException($"pack '{Id}': embedded asset not found: {name}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
