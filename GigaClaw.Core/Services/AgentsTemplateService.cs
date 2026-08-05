using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GigaClaw.Core.Packs;

namespace GigaClaw.Core.Services;

public sealed class AgentsTemplateService
{
    private const string AgentsPrefix = "GigaClaw.Core.AgentsTemplate/";
    private const string RootPrefix = "GigaClaw.Core.AgentsTemplateRoot/";
    private readonly Assembly _assembly = typeof(AgentsTemplateService).Assembly;

    public IReadOnlyList<string> RelativePaths() => EnumerateWithPrefix(AgentsPrefix);
    public IReadOnlyList<string> RootRelativePaths() => EnumerateWithPrefix(RootPrefix);

    private IReadOnlyList<string> EnumerateWithPrefix(string prefix)
    {
        var names = _assembly.GetManifestResourceNames();
        var list = new List<string>();
        foreach (var n in names)
        {
            if (n.StartsWith(prefix, StringComparison.Ordinal))
                list.Add(n.Substring(prefix.Length).Replace('\\', '/'));
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>Raw bytes of one <c>.agents/</c>-destined template file, by the path <see cref="RelativePaths"/> reports.</summary>
    public byte[] ReadAgentAsset(string relativePath) => ReadAsset(AgentsPrefix, relativePath);

    /// <summary>Raw bytes of one workspace-root-destined template file, by the path <see cref="RootRelativePaths"/> reports.</summary>
    public byte[] ReadRootAsset(string relativePath) => ReadAsset(RootPrefix, relativePath);

    private byte[] ReadAsset(string prefix, string relativePath)
    {
        var allNames = _assembly.GetManifestResourceNames();
        var name = prefix + relativePath.Replace('/', '\\');
        if (!allNames.Contains(name))
            name = prefix + relativePath.Replace('\\', '/');
        using var s = _assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded asset not found: {name}");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public IReadOnlyList<string> AgentSlugs()
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in RelativePaths())
        {
            var parts = rel.Split('/');
            if (parts.Length == 2 && parts[1].Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                slugs.Add(parts[0]);
        }
        return slugs.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// AD-9 model seeding: agent-slug → default Claude model id, sourced from the embedded
    /// <c>ProjectTemplate/Agents/models.json</c> map. A value is either a bare model id string or
    /// an object <c>{ "model": "<id>", "criterion": "<why this tier>" }</c>. Both shapes seed the
    /// same <see cref="Models.Member.DefaultModel"/>; the criterion is review metadata read by
    /// <c>GigaClaw.Catalog</c>, which gates on its presence (doc/pack-infrastructure.md §7).
    /// <para>
    /// Reading the object form here is not optional. This method used to accept only
    /// <see cref="JsonValueKind.String"/> and <em>silently skip</em> anything else, so an
    /// object-valued entry would have left that agent with no default model and no error — the
    /// failure would have surfaced as an unexplained fallback-model member weeks later.
    /// </para>
    /// Missing or malformed <c>models.json</c> degrades to an empty map rather than throwing —
    /// member creation must never be blocked by a template-config problem. Slugs absent from the
    /// map simply get no explicit <see cref="Models.Member.DefaultModel"/>, falling back to the
    /// project's FallbackModel per AD-9's three-level resolution.
    /// </summary>
    public IReadOnlyDictionary<string, string> DefaultModels()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!RelativePaths().Contains("models.json", StringComparer.Ordinal))
            return map;

        try
        {
            var bytes = ReadAsset(AgentsPrefix, "models.json");
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.StartsWith('_')) continue; // e.g. "_comment" — not an agent slug
                var value = ReadModelId(prop.Value);
                if (!string.IsNullOrWhiteSpace(value)) map[prop.Name] = value;
            }
        }
        catch
        {
            // Malformed models.json — seed nothing rather than block Initialize/member creation.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return map;
    }

    /// <summary>
    /// The one place that knows <c>models.json</c> accepts two value shapes. An object without a
    /// usable <c>model</c> string yields null, which seeds nothing — the same outcome as an absent
    /// slug, never a silently wrong model id.
    /// </summary>
    private static string? ReadModelId(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Object when value.TryGetProperty("model", out var model)
            && model.ValueKind == JsonValueKind.String => model.GetString(),
        _ => null
    };

    /// <summary>
    /// Shared "create any agent member that doesn't exist yet" step, seeding
    /// <see cref="Models.Member.DefaultModel"/> from <see cref="DefaultModels"/> (AD-9). Used by
    /// both new-project-in-place initialization (Home.razor) and the
    /// <c>POST /api/projects/{slug}/initialize</c> endpoint so the two paths cannot drift.
    /// </summary>
    public async Task<List<string>> EnsureAgentMembersAsync(string projectSlug, MemberService members)
    {
        var created = new List<string>();
        var defaultModels = DefaultModels();
        foreach (var slug in AgentSlugs())
        {
            if (await members.MemberExistsAsync(projectSlug, slug)) continue;
            defaultModels.TryGetValue(slug, out var model);
            await members.CreateMemberAsync(projectSlug, slug, model);
            created.Add(slug);
        }
        return created;
    }

    public List<string> DetectConflicts(string workspacePath)
    {
        var conflicts = new List<string>();
        foreach (var rel in RelativePaths())
        {
            var dest = Path.Combine(workspacePath, ".agents", rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dest)) conflicts.Add(".agents/" + rel);
        }
        foreach (var rel in RootRelativePaths())
        {
            var dest = Path.Combine(workspacePath, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dest)) conflicts.Add(rel);
        }
        return conflicts;
    }

    /// <summary>
    /// Composes the selected packs into a workspace. Since T6 this is a pack install
    /// (doc/pack-infrastructure.md §6): the bytes come from the <c>core</c> pack rather than from a
    /// bare loop over embedded resources, so the same staged transaction, the same owner-edit
    /// protection and the same <c>.agents/packs.lock.json</c> record apply to core as to any other
    /// pack. The one path a workspace gains relative to the pre-T6 writer is that lockfile.
    /// </summary>
    public async Task<InitializeResult> InitializeAsync(
        string workspacePath,
        bool overwriteConflicts,
        IReadOnlyList<IPackSource>? packs = null)
    {
        Directory.CreateDirectory(workspacePath);

        var install = await new PackInstaller().InstallAsync(
            workspacePath,
            packs ?? [CorePack.Source(_assembly)],
            new PackInstallOptions(overwriteConflicts));

        var written = install.Written.ToList();
        var skipped = install.PreservedOwnerEdits.ToList();

        var gitInitResult = GitInitResult.NotAttempted;
        var gitDir = Path.Combine(workspacePath, ".git");
        if (!Directory.Exists(gitDir) && !File.Exists(gitDir))
        {
            if (!IsGitAvailable())
            {
                gitInitResult = GitInitResult.GitMissing;
            }
            else
            {
                var (ok, _) = RunProcess("git", "init", workspacePath);
                gitInitResult = ok ? GitInitResult.Created : GitInitResult.Failed;
            }
        }
        else
        {
            gitInitResult = GitInitResult.AlreadyExists;
        }

        return new InitializeResult(written, skipped, gitInitResult);
    }

    public bool IsGitAvailable()
    {
        try
        {
            var (ok, _) = RunProcess("git", "--version", workingDirectory: null);
            return ok;
        }
        catch { return false; }
    }

    public bool IsClaudeAvailable()
    {
        try
        {
            var (ok, _) = RunProcess("claude", "--version", workingDirectory: null);
            return ok;
        }
        catch { return false; }
    }

    public bool IsCodexAvailable()
    {
        try
        {
            var (ok, _) = RunProcess("codex", "--version", workingDirectory: null);
            return ok;
        }
        catch { return false; }
    }

    private static (bool Ok, string Output) RunProcess(string file, string args, string? workingDirectory)
    {
        try
        {
            var res = ProcessRunner.RunAsync(file, args, workingDirectory, TimeSpan.FromSeconds(10))
                .GetAwaiter().GetResult();
            return (res.Success, res.Stdout + res.Stderr);
        }
        catch { return (false, ""); }
    }

    public enum GitInitResult { NotAttempted, AlreadyExists, Created, GitMissing, Failed }

    public sealed record InitializeResult(List<string> Written, List<string> Skipped, GitInitResult GitInit);
}
