using System.Text;
using System.Text.Json;
using GigaClaw.Core.Services;

namespace GigaClaw.Catalog;

public sealed record AgentCatalogEntry(
    string Slug,
    string Pack,
    bool ContractPresent,
    string? RiskClass,
    string? ExplicitModelMapping,
    string? ModelCriterion,
    IReadOnlyList<string> ActionModels,
    bool ProjectFallbackRequired,
    IReadOnlyList<string> Teams,
    IReadOnlyList<string> EnabledDispatchingAutomations,
    IReadOnlyList<string> DisabledDispatchingAutomations,
    bool EvalBaselinePresent,
    bool EvalFixturePresent);

public sealed record CatalogSummary(
    int Agents,
    int Contracts,
    int Automations,
    int EnabledAutomations,
    int ExplicitModelMappings,
    int Teams,
    int Scripts);

public sealed record TeamCatalogEntry(string Slug, IReadOnlyList<string> Agents);

public sealed record AutomationCatalogEntry(
    string Id,
    bool Enabled,
    IReadOnlyList<string> Agents,
    IReadOnlyList<string> Models);

/// <summary>One installed pack, as the catalog reports it (§9).</summary>
public sealed record PackCatalogEntry(
    string Id,
    string Version,
    string Kind,
    IReadOnlyList<string> DependsOn,
    int AgentCount);

public sealed record SystemCatalog(
    int Version,
    CatalogSummary Summary,
    IReadOnlyList<PackCatalogEntry> Packs,
    IReadOnlyList<AgentCatalogEntry> Agents,
    IReadOnlyList<TeamCatalogEntry> Teams,
    IReadOnlyList<AutomationCatalogEntry> Automations,
    IReadOnlyList<string> Scripts);

/// <summary>
/// One agent missing one or more of the five bindings (doc/pack-infrastructure.md §7). Carries the
/// owning pack so a gap can be gated per pack: a pack is strict from its first commit whatever mode
/// core is in, because a pack that cannot meet the bar on day one is exactly the anti-pattern the
/// rule exists to prevent.
/// </summary>
public sealed record BindingGap(string Pack, string Slug, IReadOnlyList<string> Missing)
{
    public override string ToString() => $"[{Pack}] {Slug}: missing {string.Join(", ", Missing)}";
}

/// <summary>
/// A pack whose data escapes something its own manifest declared (§7.5, §7.6). Not a binding gap:
/// the agent may be perfectly bound and still be reaching past the ceiling a reviewer approved.
/// </summary>
public sealed record PackPolicyViolation(string Pack, string Rule, string Message)
{
    public override string ToString() => $"[{Pack}] {Rule}: {Message}";
}

/// <summary>Everything one catalog run produces: the artifact, plus the findings the gate acts on.</summary>
public sealed record CatalogBuild(
    SystemCatalog Catalog,
    IReadOnlyList<PackCatalogSource> Sources,
    IReadOnlyList<PackPolicyViolation> PackPolicyViolations);

public sealed class CatalogGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Current catalog schema. Bumped 2 → 3 when packs, model criteria and eval fixtures landed.</summary>
    public const int SchemaVersion = 3;

    /// <summary>
    /// Reasons the <c>core</c> pack is reported on but never gated (§7.4, owner Q2).
    ///
    /// <para>
    /// Core predates the rule. At the rule's introduction it shipped 33 agents against 6 replay
    /// fixtures, so gating core on <see cref="EvalFixtureReason"/> would have failed every build for
    /// a binding that was never applied retroactively — and would have blocked the Security pack on
    /// core's backlog, which Q2 explicitly ruled out. That backlog is now closed (all 33 core agents
    /// carry a fixture as of the eval-fixture authoring pass), but the exemption itself stays: it is
    /// what let the backlog close incrementally instead of in one drop, and it is what protects the
    /// next core agent that lands without one from failing every build the moment it exists. Packs
    /// carry no exemption: all five bindings are gated from their first commit.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> CoreExemptReasons =
        new HashSet<string>(StringComparer.Ordinal) { EvalFixtureReason };

    public const string ContractReason = "contract";
    public const string ModelMappingReason = "model mapping";
    public const string ModelCriterionReason = "model criterion";
    public const string TeamReason = "team";
    public const string DispatchReason = "enabled dispatching automation";
    public const string EvalFixtureReason = "eval fixture";

    /// <summary>
    /// Today's core-only read. Identical output to the pre-pack generator apart from the schema
    /// fields packs added, because the fallback composition is a single un-manifested source
    /// pointing at <c>ProjectTemplate/Agents</c>.
    /// </summary>
    public SystemCatalog Generate(string repositoryRoot) => Build(repositoryRoot).Catalog;

    /// <summary>
    /// Builds the catalog over a composed view when one is supplied, and over the single host
    /// template when one is not. There is exactly one code path: "core only" is not a special case,
    /// it is a one-element composition.
    /// </summary>
    /// <param name="composition">
    /// The composed packs. Pass null to discover them from the repository tree — which, in a tree
    /// with no <c>pack.json</c> anywhere, yields exactly the pre-pack behaviour.
    /// </param>
    public CatalogBuild Build(string repositoryRoot, IReadOnlyList<PackCatalogSource>? composition = null)
    {
        var sources = composition is { Count: > 0 }
            ? composition
            : PackCatalogSourceReader.Discover(repositoryRoot);

        var packOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var slugs = new List<string>();
        var contracts = new Dictionary<string, ContractInfo>(StringComparer.Ordinal);
        var models = new Dictionary<string, ModelInfo>(StringComparer.Ordinal);
        var automations = new List<AutomationInfo>();
        var scripts = new SortedSet<string>(StringComparer.Ordinal);
        var fixtureAgents = new HashSet<string>(StringComparer.Ordinal);
        var teams = new List<TeamCatalogEntry>();
        var violations = new List<PackPolicyViolation>();

        foreach (var source in sources)
        {
            foreach (var slug in AgentSlugs(source.AgentsDirectory))
            {
                if (packOf.ContainsKey(slug)) continue; // D2 collision — PackComposer refuses it; the catalog reports the first.
                packOf[slug] = source.Id;
                slugs.Add(slug);
            }

            foreach (var (slug, contract) in ReadContracts(source.AgentsDirectory))
                contracts.TryAdd(slug, contract);
            foreach (var (slug, model) in ReadModels(source.AgentsDirectory))
                models.TryAdd(slug, model);
            automations.AddRange(ReadAutomations(source.AgentsDirectory, source.Id));
            foreach (var script in ReadScripts(source.AgentsDirectory)) scripts.Add(script);
            foreach (var agent in ReadFixtureAgents(source.FixtureDirectory)) fixtureAgents.Add(agent);
            teams.AddRange(ReadTeams(source));
        }

        ApplyTeamMembership(sources, teams);
        var orderedSlugs = slugs.OrderBy(slug => slug, StringComparer.Ordinal).ToArray();
        var orderedTeams = MergeTeams(teams);
        violations.AddRange(FindPackPolicyViolations(sources, contracts, automations, packOf));

        var teamsByAgent = orderedTeams
            .Where(team => team.Agents.Count > 0)
            .SelectMany(team => team.Agents.Select(slug => (slug, team.Slug)))
            .GroupBy(pair => pair.slug, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(pair => pair.Slug).OrderBy(value => value, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var evalBaselineDirectory = Path.Combine(repositoryRoot, "GigaClaw.Eval", "baselines");

        var entries = orderedSlugs.Select(slug =>
        {
            var bindings = automations
                .SelectMany(automation => automation.Bindings
                    .Where(binding => binding.Agent == slug)
                    .Select(binding => new { Automation = automation, binding.Model }))
                .ToArray();
            models.TryGetValue(slug, out var model);
            contracts.TryGetValue(slug, out var contract);
            return new AgentCatalogEntry(
                slug,
                packOf.GetValueOrDefault(slug, PackCatalogSource.CoreId),
                contract is not null,
                contract?.RiskClass,
                model?.Model,
                model?.Criterion,
                bindings
                    .Select(binding => binding.Model)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                bindings.Any(binding => string.IsNullOrWhiteSpace(binding.Model)) &&
                    string.IsNullOrWhiteSpace(model?.Model),
                teamsByAgent.GetValueOrDefault(slug, Array.Empty<string>()),
                bindings
                    .Where(binding => binding.Automation.Enabled)
                    .Select(binding => binding.Automation.Id)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray(),
                bindings
                    .Where(binding => !binding.Automation.Enabled)
                    .Select(binding => binding.Automation.Id)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray(),
                EvalBaselinePresent: File.Exists(
                    Path.Combine(evalBaselineDirectory, $"{slug}.json")),
                // Keyed off presence in a discovered fixture directory, which is also a claim of
                // executability: every discovered fixture directory must be a configured
                // Replay.FixtureRoots entry, pinned by ReplayRunnerTests
                // .EveryDiscoveredPackFixtureDirectoryIsAConfiguredReplayRoot.
                EvalFixturePresent: fixtureAgents.Contains(slug));
        }).ToArray();

        var automationEntries = automations
            .Select(automation => new AutomationCatalogEntry(
                automation.Id,
                automation.Enabled,
                automation.Bindings.Select(binding => binding.Agent)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(agent => agent, StringComparer.Ordinal)
                    .ToArray(),
                automation.Bindings.Select(binding => binding.Model)
                    .Where(model => !string.IsNullOrWhiteSpace(model))
                    .Select(model => model!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(model => model, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();

        var packEntries = sources
            .Select(source => new PackCatalogEntry(
                source.Id,
                source.Version,
                source.Kind,
                source.DependsOn.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                entries.Count(entry => entry.Pack == source.Id)))
            .ToArray();

        var catalog = new SystemCatalog(
            SchemaVersion,
            new CatalogSummary(
                entries.Length,
                contracts.Count,
                automations.Count,
                automations.Count(automation => automation.Enabled),
                models.Count,
                orderedTeams.Count,
                scripts.Count),
            packEntries,
            entries,
            orderedTeams,
            automationEntries,
            scripts.ToArray());

        return new CatalogBuild(catalog, sources, violations);
    }

    public void WriteGeneratedFiles(string repositoryRoot, SystemCatalog catalog)
    {
        File.WriteAllText(Path.Combine(repositoryRoot, "catalog.json"), JsonSerializer.Serialize(catalog, JsonOptions) + "\n");
        File.WriteAllText(Path.Combine(repositoryRoot, "doc", "catalog.md"), RenderMarkdown(catalog));
    }

    public static string RenderMarkdown(SystemCatalog catalog)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# System catalog");
        builder.AppendLine();
        builder.AppendLine("Generated by `dotnet run --project GigaClaw.Catalog -- generate`. Do not edit by hand.");
        builder.AppendLine();
        builder.AppendLine($"- Agents: {catalog.Summary.Agents}");
        builder.AppendLine($"- Contracts: {catalog.Summary.Contracts}");
        builder.AppendLine($"- Automations: {catalog.Summary.Automations} ({catalog.Summary.EnabledAutomations} enabled)");
        builder.AppendLine($"- Explicit model mappings: {catalog.Summary.ExplicitModelMappings}");
        builder.AppendLine($"- Team definitions: {catalog.Summary.Teams}");
        builder.AppendLine($"- Shared scripts: {catalog.Summary.Scripts}");
        builder.AppendLine();
        builder.AppendLine("| Pack | Version | Kind | Depends on | Agents |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var pack in catalog.Packs)
            builder.AppendLine($"| {Cell(pack.Id)} | {Cell(pack.Version)} | {Cell(pack.Kind)} | {Join(pack.DependsOn)} | {pack.AgentCount} |");
        builder.AppendLine();
        builder.AppendLine("| Agent | Pack | Contract / risk | Explicit member model | Model criterion | Action models | Project fallback required | Teams | Enabled dispatch | Disabled dispatch | Eval baseline | Eval fixture |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var agent in catalog.Agents)
            builder.AppendLine(
                $"| {Cell(agent.Slug)} | {Cell(agent.Pack)} | {(agent.ContractPresent ? Cell(agent.RiskClass ?? "yes") : "no")} | {Cell(agent.ExplicitModelMapping ?? "—")} | {Cell(agent.ModelCriterion ?? "—")} | {Join(agent.ActionModels)} | {YesNo(agent.ProjectFallbackRequired)} | {Join(agent.Teams)} | {Join(agent.EnabledDispatchingAutomations)} | {Join(agent.DisabledDispatchingAutomations)} | {YesNo(agent.EvalBaselinePresent)} | {YesNo(agent.EvalFixturePresent)} |");
        return builder.ToString();
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "—" : Cell(string.Join(", ", values));
    private static string Cell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static IEnumerable<string> AgentSlugs(string agentsDirectory)
    {
        if (!Directory.Exists(agentsDirectory)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(agentsDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => File.Exists(Path.Combine(path, "SKILL.md")))
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(slug => slug, StringComparer.Ordinal);
    }

    private static IEnumerable<KeyValuePair<string, ContractInfo>> ReadContracts(string agentsDirectory)
    {
        var path = Path.Combine(agentsDirectory, "contracts.json");
        if (!File.Exists(path)) yield break;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("agents", out var agents)) yield break;
        foreach (var property in agents.EnumerateObject())
        {
            var riskClass = property.Value.TryGetProperty("riskClass", out var risk) ? risk.GetString() : null;
            var globs = property.Value.TryGetProperty("allowedWriteGlobs", out var writes) &&
                writes.ValueKind == JsonValueKind.Array
                ? writes.EnumerateArray()
                    .Where(entry => entry.ValueKind == JsonValueKind.String)
                    .Select(entry => entry.GetString()!)
                    .ToArray()
                : Array.Empty<string>();
            yield return new KeyValuePair<string, ContractInfo>(property.Name, new ContractInfo(riskClass, globs));
        }
    }

    /// <summary>
    /// <c>models.json</c> values are <c>string | {model, criterion}</c> (§7.2). The bare string is
    /// still valid JSON and still seeds a model — it just has no stated criterion, which is a
    /// binding gap rather than a parse error, so an existing pack keeps loading while the gate
    /// tells its author what is missing.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, ModelInfo>> ReadModels(string agentsDirectory)
    {
        var path = Path.Combine(agentsDirectory, "models.json");
        if (!File.Exists(path)) yield break;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith('_')) continue;
            var info = property.Value.ValueKind switch
            {
                JsonValueKind.String => new ModelInfo(property.Value.GetString(), null),
                JsonValueKind.Object => new ModelInfo(
                    property.Value.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                        ? model.GetString()
                        : null,
                    property.Value.TryGetProperty("criterion", out var criterion) && criterion.ValueKind == JsonValueKind.String
                        ? Normalize(criterion.GetString())
                        : null),
                _ => new ModelInfo(null, null)
            };
            if (info.Model is not null) yield return new KeyValuePair<string, ModelInfo>(property.Name, info);
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IEnumerable<string> ReadScripts(string agentsDirectory)
    {
        var scriptsDirectory = Path.Combine(agentsDirectory, "scripts");
        if (!Directory.Exists(scriptsDirectory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(scriptsDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetRelativePath(agentsDirectory, path).Replace('\\', '/'));
    }

    /// <summary>
    /// Replay fixtures, keyed by the agent each one names. A <em>fixture</em> is a replay input; a
    /// <em>baseline</em> is the reviewed static-check snapshot. They are not interchangeable and
    /// the gate deliberately checks the fixture, which is what the five-binding rule says: a fixture
    /// proves the agent has been dispatched end to end at least once, a baseline only records what
    /// a static reader saw. The shape read here is the one <c>ReplayRunner.ReadFixture</c> validates.
    /// </summary>
    private static IEnumerable<string> ReadFixtureAgents(string? fixtureDirectory)
    {
        if (fixtureDirectory is null || !Directory.Exists(fixtureDirectory)) yield break;
        foreach (var path in Directory.EnumerateFiles(fixtureDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            string? agent;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                agent = document.RootElement.TryGetProperty("Agent", out var value) &&
                    value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            }
            catch (JsonException)
            {
                continue; // ReplayRunner.ReadFixture is the validator; the catalog only counts coverage.
            }
            if (!string.IsNullOrWhiteSpace(agent)) yield return agent;
        }
    }

    /// <summary>
    /// Core's teams still come from <see cref="AgentTeamService"/>, which is the same service the
    /// runtime team filter uses — reading them from anywhere else would let the catalog pass on a
    /// team the runtime cannot see (D8's rejected alternative). A non-core pack ships
    /// <c>Agents/teams.json</c>.
    /// </summary>
    private static IEnumerable<TeamCatalogEntry> ReadTeams(PackCatalogSource source)
    {
        if (source.IsCore)
        {
            return new AgentTeamService().GetTeams()
                .Select(team => new TeamCatalogEntry(team.Slug, team.AgentSlugs.ToArray()));
        }

        var path = Path.Combine(source.AgentsDirectory, "teams.json");
        if (!File.Exists(path)) return Array.Empty<TeamCatalogEntry>();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<TeamCatalogEntry>();
        return document.RootElement.EnumerateArray()
            .Where(team => team.TryGetProperty("slug", out _))
            .Select(team => new TeamCatalogEntry(
                team.GetProperty("slug").GetString()!,
                team.TryGetProperty("agentSlugs", out var agents) && agents.ValueKind == JsonValueKind.Array
                    ? agents.EnumerateArray()
                        .Where(entry => entry.ValueKind == JsonValueKind.String)
                        .Select(entry => entry.GetString()!)
                        .ToArray()
                    : Array.Empty<string>()))
            .ToArray();
    }

    /// <summary>Additive only (§3). A pack can join a team it does not own; it can never leave one for someone else.</summary>
    private static void ApplyTeamMembership(
        IReadOnlyList<PackCatalogSource> sources,
        List<TeamCatalogEntry> teams)
    {
        foreach (var source in sources)
        {
            if (source.TeamMembership is null) continue;
            foreach (var (teamSlug, added) in source.TeamMembership)
                teams.Add(new TeamCatalogEntry(teamSlug, added.ToArray()));
        }
    }

    private static IReadOnlyList<TeamCatalogEntry> MergeTeams(IEnumerable<TeamCatalogEntry> teams) =>
        teams
            .GroupBy(team => team.Slug, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new TeamCatalogEntry(
                group.Key,
                group.SelectMany(team => team.Agents)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(slug => slug, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();

    /// <summary>
    /// Expands one pack's <c>automations.json</c> into the (agent, model) bindings the catalog
    /// reasons about, resolving <c>{assignee}</c> against the automation's <c>assignedTo</c>
    /// conditions. This is the single implementation of that expansion the pack-aware path uses.
    /// </summary>
    public static IReadOnlyList<AutomationInfo> ReadAutomations(string agentsDirectory, string packId)
    {
        var path = Path.Combine(agentsDirectory, "automations.json");
        if (!File.Exists(path)) return Array.Empty<AutomationInfo>();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return ReadAutomations(document.RootElement, packId);
    }

    public static IReadOnlyList<AutomationInfo> ReadAutomations(JsonElement root, string packId)
    {
        var result = new List<AutomationInfo>();
        if (!root.TryGetProperty("automations", out var list)) return result;
        foreach (var automation in list.EnumerateArray())
        {
            var assignees = automation.TryGetProperty("conditions", out var conditions)
                ? conditions.EnumerateArray().Where(condition => condition.TryGetProperty("type", out var type) && type.GetString() == "assignedTo")
                    .SelectMany(condition => condition.GetProperty("slugs").EnumerateArray().Select(slug => slug.GetString()!))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : Enumerable.Empty<string>();
            var bindings = new List<RunAgentBinding>();
            var actionTypes = new List<string>();
            foreach (var action in automation.GetProperty("actions").EnumerateArray())
            {
                if (!action.TryGetProperty("type", out var type)) continue;
                var actionType = type.GetString();
                if (!string.IsNullOrWhiteSpace(actionType)) actionTypes.Add(actionType);
                if (actionType != "runAgent") continue;
                var agent = action.GetProperty("agent").GetString();
                var model = action.TryGetProperty("model", out var modelElement) &&
                    modelElement.ValueKind == JsonValueKind.String
                    ? modelElement.GetString()
                    : null;
                if (agent == "{assignee}")
                {
                    foreach (var assignee in assignees)
                        bindings.Add(new RunAgentBinding(assignee, model));
                }
                else if (!string.IsNullOrWhiteSpace(agent))
                {
                    bindings.Add(new RunAgentBinding(agent, model));
                }
            }
            result.Add(new AutomationInfo(
                automation.GetProperty("id").GetString()!,
                packId,
                automation.GetProperty("enabled").GetBoolean(),
                bindings,
                actionTypes.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray()));
        }
        return result;
    }

    /// <summary>
    /// The five bindings, per agent, per pack (§7). Severity is not decided here — see
    /// <see cref="CoreExemptReasons"/> and the <c>--strict</c> / <c>--strict-packs</c> split — so
    /// the same computation serves both the report and the gate and the two cannot drift.
    /// </summary>
    public static IReadOnlyList<BindingGap> FindBindingGaps(SystemCatalog catalog) =>
        catalog.Agents
            .Select(agent => new BindingGap(
                agent.Pack,
                agent.Slug,
                new[]
                {
                    !agent.ContractPresent ? ContractReason : null,
                    string.IsNullOrWhiteSpace(agent.ExplicitModelMapping) &&
                    agent.ActionModels.Count == 0
                        ? ModelMappingReason
                        : null,
                    // Binding 2 is "a model mapping *with a stated criterion*". An agent whose only
                    // model comes from a runAgent action has nowhere to state one, which is itself
                    // the finding: the fix is an explicit models.json entry, not a comment.
                    string.IsNullOrWhiteSpace(agent.ModelCriterion) ? ModelCriterionReason : null,
                    agent.Teams.Count == 0 ? TeamReason : null,
                    agent.EnabledDispatchingAutomations.Count == 0
                        ? DispatchReason
                        : null,
                    !agent.EvalFixturePresent ? EvalFixtureReason : null
                }.Where(value => value is not null).Select(value => value!).ToArray()))
            .Where(gap => gap.Missing.Count > 0)
            .ToArray();

    /// <summary>
    /// Whether a gap fails the build under the given flags. Core is gated by <c>--strict</c> on
    /// every reason except <see cref="CoreExemptReasons"/>; a pack is gated by either flag on all
    /// of them, because §7.4 makes strict mode unconditional for packs whatever mode core is in.
    /// </summary>
    public static IReadOnlyList<string> GatedReasons(BindingGap gap, bool strict, bool strictPacks, SystemCatalog catalog)
    {
        var isCore = catalog.Packs.Any(pack =>
            string.Equals(pack.Id, gap.Pack, StringComparison.Ordinal) &&
            string.Equals(pack.Kind, PackCatalogSource.CoreKind, StringComparison.Ordinal));
        if (!isCore) return strict || strictPacks ? gap.Missing : Array.Empty<string>();
        if (!strict) return Array.Empty<string>();
        return gap.Missing.Where(reason => !CoreExemptReasons.Contains(reason)).ToArray();
    }

    /// <summary>
    /// §7.5 and §7.6: a pack's own data must stay inside the ceilings its manifest declared.
    ///
    /// <para>
    /// "Subset" is read as exact string membership, not glob subsumption. That is deliberate: the
    /// spec asks for a subset check, and inferring that <c>doc/security/**</c> subsumes
    /// <c>doc/security/findings/*.md</c> means writing a glob-containment engine the spec never
    /// specified, whose false negatives would silently widen write scope — the exact failure the
    /// ceiling exists to prevent. A pack author who wants a narrower per-agent glob lists both the
    /// narrow glob and the ceiling entry it lives under, which costs one line and stays reviewable.
    /// </para>
    /// </summary>
    private static IEnumerable<PackPolicyViolation> FindPackPolicyViolations(
        IReadOnlyList<PackCatalogSource> sources,
        IReadOnlyDictionary<string, ContractInfo> contracts,
        IReadOnlyList<AutomationInfo> automations,
        IReadOnlyDictionary<string, string> packOf)
    {
        foreach (var source in sources)
        {
            if (source.Permissions is null) continue; // No manifest yet: nothing was declared to check against.

            var ceiling = source.Permissions.AllowedWriteGlobs.ToHashSet(StringComparer.Ordinal);
            foreach (var (slug, contract) in contracts.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (packOf.GetValueOrDefault(slug) != source.Id) continue;
                foreach (var glob in contract.AllowedWriteGlobs.Where(glob => !ceiling.Contains(glob)))
                {
                    yield return new PackPolicyViolation(
                        source.Id,
                        "permissions.allowedWriteGlobs",
                        $"agent '{slug}' declares write glob '{glob}', which is not in the manifest ceiling.");
                }
            }

            var declared = source.Permissions.Actions.ToHashSet(StringComparer.Ordinal);
            foreach (var automation in automations.Where(automation => automation.Pack == source.Id))
            {
                foreach (var action in automation.ActionTypes.Where(action => !declared.Contains(action)))
                {
                    yield return new PackPolicyViolation(
                        source.Id,
                        "permissions.actions",
                        $"automation '{automation.Id}' uses action type '{action}', which is not in permissions.actions.");
                }
            }
        }
    }

    private sealed record ContractInfo(string? RiskClass, IReadOnlyList<string> AllowedWriteGlobs);
    private sealed record ModelInfo(string? Model, string? Criterion);
    public sealed record RunAgentBinding(string Agent, string? Model);
    public sealed record AutomationInfo(
        string Id,
        string Pack,
        bool Enabled,
        IReadOnlyList<RunAgentBinding> Bindings,
        IReadOnlyList<string> ActionTypes);
}
