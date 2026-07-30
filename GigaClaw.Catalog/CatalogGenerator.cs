using System.Text;
using System.Text.Json;
using GigaClaw.Core.Services;

namespace GigaClaw.Catalog;

public sealed record AgentCatalogEntry(
    string Slug,
    bool ContractPresent,
    string? RiskClass,
    string? ExplicitModelMapping,
    IReadOnlyList<string> ActionModels,
    bool ProjectFallbackRequired,
    IReadOnlyList<string> Teams,
    IReadOnlyList<string> EnabledDispatchingAutomations,
    IReadOnlyList<string> DisabledDispatchingAutomations,
    bool EvalBaselinePresent);

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

public sealed record SystemCatalog(
    int Version,
    CatalogSummary Summary,
    IReadOnlyList<AgentCatalogEntry> Agents,
    IReadOnlyList<TeamCatalogEntry> Teams,
    IReadOnlyList<AutomationCatalogEntry> Automations,
    IReadOnlyList<string> Scripts);

public sealed class CatalogGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SystemCatalog Generate(string repositoryRoot)
    {
        var agentsDirectory = Path.Combine(repositoryRoot, "ProjectTemplate", "Agents");
        var slugs = Directory.EnumerateDirectories(
                agentsDirectory,
                "*",
                SearchOption.TopDirectoryOnly)
            .Where(path => File.Exists(Path.Combine(path, "SKILL.md")))
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(slug => slug, StringComparer.Ordinal)
            .ToArray();

        using var contractsDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(agentsDirectory, "contracts.json")));
        var contracts = contractsDocument.RootElement.GetProperty("agents").EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.TryGetProperty("riskClass", out var riskClass)
                    ? riskClass.GetString()
                    : null,
                StringComparer.Ordinal);
        using var modelsDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(agentsDirectory, "models.json")));
        var models = modelsDocument.RootElement.EnumerateObject()
            .Where(property => !property.Name.StartsWith('_'))
            .ToDictionary(
                property => property.Name,
                property => property.Value.GetString()!,
                StringComparer.Ordinal);
        using var automationsDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(agentsDirectory, "automations.json")));
        var automations = ReadAutomations(automationsDocument.RootElement);
        var evalBaselineDirectory = Path.Combine(repositoryRoot, "GigaClaw.Eval", "baselines");
        var teams = new AgentTeamService().GetTeams()
            .OrderBy(team => team.Slug, StringComparer.Ordinal)
            .Select(team => new TeamCatalogEntry(
                team.Slug,
                team.AgentSlugs.OrderBy(slug => slug, StringComparer.Ordinal).ToArray()))
            .ToArray();
        var teamsByAgent = teams
            .Where(team => team.Agents.Count > 0)
            .SelectMany(team => team.Agents.Select(slug => (slug, team.Slug)))
            .GroupBy(pair => pair.slug, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(pair => pair.Slug).OrderBy(value => value, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        var entries = slugs.Select(slug =>
        {
            var bindings = automations
                .SelectMany(automation => automation.Bindings
                    .Where(binding => binding.Agent == slug)
                    .Select(binding => new { Automation = automation, binding.Model }))
                .ToArray();
            models.TryGetValue(slug, out var explicitModel);
            contracts.TryGetValue(slug, out var riskClass);
            return new AgentCatalogEntry(
                slug,
                contracts.ContainsKey(slug),
                riskClass,
                explicitModel,
                bindings
                    .Select(binding => binding.Model)
                    .Where(model => !string.IsNullOrWhiteSpace(model))
                    .Select(model => model!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(model => model, StringComparer.Ordinal)
                    .ToArray(),
                bindings.Any(binding => string.IsNullOrWhiteSpace(binding.Model)) &&
                    string.IsNullOrWhiteSpace(explicitModel),
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
                    Path.Combine(evalBaselineDirectory, $"{slug}.json")));
        }).ToArray();
        var scripts = Directory.EnumerateFiles(
                Path.Combine(agentsDirectory, "scripts"),
                "*",
                SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetRelativePath(agentsDirectory, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
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

        return new SystemCatalog(
            2,
            new CatalogSummary(
                entries.Length,
                contracts.Count,
                automations.Count,
                automations.Count(automation => automation.Enabled),
                models.Count,
                teams.Length,
                scripts.Length),
            entries,
            teams,
            automationEntries,
            scripts);
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
        builder.AppendLine("| Agent | Contract / risk | Explicit member model | Action models | Project fallback required | Teams | Enabled dispatch | Disabled dispatch | Eval baseline |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var agent in catalog.Agents)
            builder.AppendLine(
                $"| {Cell(agent.Slug)} | {(agent.ContractPresent ? Cell(agent.RiskClass ?? "yes") : "no")} | {Cell(agent.ExplicitModelMapping ?? "—")} | {Join(agent.ActionModels)} | {YesNo(agent.ProjectFallbackRequired)} | {Join(agent.Teams)} | {Join(agent.EnabledDispatchingAutomations)} | {Join(agent.DisabledDispatchingAutomations)} | {YesNo(agent.EvalBaselinePresent)} |");
        return builder.ToString();
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "—" : Cell(string.Join(", ", values));
    private static string Cell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static List<AutomationInfo> ReadAutomations(JsonElement root)
    {
        var result = new List<AutomationInfo>();
        foreach (var automation in root.GetProperty("automations").EnumerateArray())
        {
            var assignees = automation.TryGetProperty("conditions", out var conditions)
                ? conditions.EnumerateArray().Where(condition => condition.TryGetProperty("type", out var type) && type.GetString() == "assignedTo")
                    .SelectMany(condition => condition.GetProperty("slugs").EnumerateArray().Select(slug => slug.GetString()!))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : Enumerable.Empty<string>();
            var bindings = new List<RunAgentBinding>();
            foreach (var action in automation.GetProperty("actions").EnumerateArray())
            {
                if (!action.TryGetProperty("type", out var type) || type.GetString() != "runAgent") continue;
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
                automation.GetProperty("enabled").GetBoolean(),
                bindings));
        }
        return result;
    }

    public static IReadOnlyList<string> FindBindingGaps(SystemCatalog catalog) =>
        catalog.Agents
            .Select(agent => new
            {
                agent.Slug,
                Missing = new[]
                {
                    !agent.ContractPresent ? "contract" : null,
                    string.IsNullOrWhiteSpace(agent.ExplicitModelMapping) &&
                    agent.ActionModels.Count == 0
                        ? "model mapping"
                        : null,
                    agent.Teams.Count == 0 ? "team" : null,
                    agent.EnabledDispatchingAutomations.Count == 0
                        ? "enabled dispatching automation"
                        : null
                }.Where(value => value is not null).ToArray()
            })
            .Where(entry => entry.Missing.Length > 0)
            .Select(entry => $"{entry.Slug}: missing {string.Join(", ", entry.Missing)}")
            .ToArray();

    private sealed record RunAgentBinding(string Agent, string? Model);
    private sealed record AutomationInfo(
        string Id,
        bool Enabled,
        IReadOnlyList<RunAgentBinding> Bindings);
}
