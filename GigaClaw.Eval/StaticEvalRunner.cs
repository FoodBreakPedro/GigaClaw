using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GigaClaw.Catalog;
using YamlDotNet.RepresentationModel;

namespace GigaClaw.Eval;

public sealed partial class StaticEvalRunner
{
    private const string Pass = "pass";
    private const string Warning = "warning";
    private const string Error = "error";

    private readonly string _repositoryRoot;
    private readonly EvalConfig _config;
    private readonly SystemCatalog _catalog;
    private readonly Dictionary<string, JsonElement> _contracts;

    public StaticEvalRunner(string repositoryRoot)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _config = EvalJson.Read<EvalConfig>(
            Path.Combine(_repositoryRoot, "GigaClaw.Eval", "evalconfig.json"));
        ValidateConfig(_config);
        _catalog = EvalJson.Read<SystemCatalog>(
            Path.Combine(_repositoryRoot, "catalog.json"));
        _contracts = ReadContracts();
        _ = ResolveConfiguredPath(_config.ArtifactRoot);
        _ = ResolveConfiguredPath(_config.BaselineRoot);
    }

    public EvalRunResult Run(
        string target,
        bool strict = false,
        bool updateBaselines = false,
        bool writeReport = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var catalogAgents = ResolveAgents(target);
        var evaluated = catalogAgents.Select(EvaluateAgent).ToArray();

        if (updateBaselines)
        {
            foreach (var result in evaluated)
                WriteBaseline(result);
            evaluated = catalogAgents.Select(EvaluateAgent).ToArray();
        }

        var report = new EvalReport(
            Version: 1,
            Mode: strict ? "strict" : "baseline",
            Target: target,
            PromptBudget: _config.PromptBudget,
            Agents: evaluated);
        if (writeReport)
            WriteReport(report);
        stopwatch.Stop();

        var hasIntegrityErrors = evaluated.Any(agent =>
            agent.BaselineStatus is "missing" or "drift" ||
            agent.Checks.Any(check =>
                check.Category == "integrity" && check.Status == Error));
        var hasStrictFindings = evaluated.Any(agent =>
            agent.Checks.Any(check => check.Status is Warning or Error));
        return new EvalRunResult(
            report,
            hasIntegrityErrors || strict && hasStrictFindings ? 1 : 0,
            stopwatch.ElapsedMilliseconds);
    }

    private AgentCatalogEntry[] ResolveAgents(string target)
    {
        if (target == "all")
            return _catalog.Agents
                .OrderBy(agent => agent.Slug, StringComparer.Ordinal)
                .ToArray();
        var agent = _catalog.Agents.SingleOrDefault(
            candidate => candidate.Slug.Equals(target, StringComparison.Ordinal));
        if (agent is null)
            throw new ArgumentException(
                $"Unknown agent '{target}'. Choose one of the catalog's {_catalog.Agents.Count} agents or 'all'.");
        return [agent];
    }

    private EvalAgentResult EvaluateAgent(AgentCatalogEntry agent)
    {
        var checks = new List<EvalCheckResult>();
        var agentDirectory = Path.Combine(AgentsRootFor(agent.Pack), agent.Slug);
        var skillPath = Path.Combine(agentDirectory, "SKILL.md");
        string? skill = null;
        byte[] skillBytes = [];

        try
        {
            skillBytes = File.ReadAllBytes(skillPath);
            skill = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(skillBytes);
            checks.Add(string.IsNullOrWhiteSpace(skill)
                ? Check("skill.parse", Error, "SKILL.md is empty.")
                : Check("skill.parse", Pass, "SKILL.md is valid non-empty UTF-8."));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            checks.Add(Check("skill.parse", Error, $"SKILL.md could not be read as UTF-8: {exception.Message}"));
        }

        checks.Add(CheckFrontmatter(agent.Slug, skill));
        checks.Add(CheckContract(agent));
        checks.Add(CheckMemory(agent.Slug, agentDirectory));
        checks.Add(CheckModel(agent));
        checks.Add(CheckScripts(skill, agent.Pack));
        checks.Add(CheckPromptBudget(skillBytes));

        var baselineStatus = CompareBaseline(agent.Slug, checks);
        return new EvalAgentResult(agent.Slug, baselineStatus, checks);
    }

    private static EvalCheckResult CheckFrontmatter(string slug, string? skill)
    {
        if (skill is null)
            return Check("frontmatter.parse", Error, "Frontmatter could not be inspected because SKILL.md did not parse.");
        using var reader = new StringReader(skill);
        if (!string.Equals(reader.ReadLine(), "---", StringComparison.Ordinal))
            return Check("frontmatter.parse", Pass, "No optional YAML frontmatter is present.");

        var yaml = new StringBuilder();
        string? line;
        var closed = false;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line == "---")
            {
                closed = true;
                break;
            }
            yaml.AppendLine(line);
        }
        if (!closed)
            return Check("frontmatter.parse", Error, "YAML frontmatter has no closing '---' fence.");

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml.ToString()));
            if (stream.Documents.Count != 1 ||
                stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                return Check("frontmatter.parse", Error, "YAML frontmatter must be one mapping.");
            }
            if (mapping.Children.TryGetValue(
                    new YamlScalarNode("name"),
                    out var nameNode) &&
                nameNode is YamlScalarNode name &&
                !string.Equals(name.Value, slug, StringComparison.Ordinal))
            {
                return Check(
                    "frontmatter.parse",
                    Error,
                    $"Frontmatter name '{name.Value}' does not match catalog agent '{slug}'.");
            }
            return Check("frontmatter.parse", Pass, "Optional YAML frontmatter parses.");
        }
        catch (Exception exception)
        {
            return Check("frontmatter.parse", Error, $"YAML frontmatter is invalid: {exception.Message}");
        }
    }

    private EvalCheckResult CheckContract(AgentCatalogEntry agent)
    {
        if (!agent.ContractPresent)
            return Check("contract.catalog", Error, "Catalog marks the contract as missing.");
        if (!_contracts.TryGetValue(agent.Slug, out var contract))
            return Check("contract.catalog", Error, "Catalog contract is absent from contracts.json.");
        var riskClass = contract.TryGetProperty("riskClass", out var risk)
            ? risk.GetString()
            : null;
        if (!string.Equals(riskClass, agent.RiskClass, StringComparison.Ordinal))
        {
            return Check(
                "contract.catalog",
                Error,
                $"Contract riskClass '{riskClass ?? "<missing>"}' differs from catalog '{agent.RiskClass ?? "<missing>"}'.");
        }
        return Check("contract.catalog", Pass, $"Contract and riskClass '{riskClass}' match the catalog.");
    }

    private static EvalCheckResult CheckMemory(string slug, string agentDirectory)
    {
        var path = Path.Combine(agentDirectory, "memory", "MEMORY.md");
        if (!File.Exists(path))
            return Check("memory.stub", Error, "memory/MEMORY.md is missing.");
        try
        {
            var text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
            var firstLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstLine) ||
                !firstLine.StartsWith("# ", StringComparison.Ordinal) ||
                !firstLine.Contains(slug, StringComparison.Ordinal))
            {
                return Check(
                    "memory.stub",
                    Error,
                    "Memory stub must start with a Markdown heading containing the catalog agent slug.");
            }
            return Check("memory.stub", Pass, "memory/MEMORY.md is a valid non-empty agent stub.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return Check("memory.stub", Error, $"Memory stub could not be read as UTF-8: {exception.Message}");
        }
    }

    private static EvalCheckResult CheckModel(AgentCatalogEntry agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.ExplicitModelMapping))
            return Check("model.resolve", Pass, $"Resolves from explicit member mapping '{agent.ExplicitModelMapping}'.");
        if (agent.ActionModels.Count > 0)
            return Check("model.resolve", Pass, $"Resolves from action model '{agent.ActionModels[0]}'.");
        if (agent.ProjectFallbackRequired)
        {
            return Check(
                "model.resolve",
                Warning,
                "Catalog requires a project fallback; the template cannot prove a project instance has configured one.",
                category: "policy");
        }
        return Check("model.resolve", Error, "Catalog provides no explicit, action, or project-fallback model path.");
    }

    private EvalCheckResult CheckScripts(string? skill, string? pack)
    {
        if (skill is null)
            return Check("scripts.resolve", Error, "Script references could not be inspected because SKILL.md did not parse.");
        var references = SharedScriptReferenceRegex().Matches(skill)
            .Select(match => $"scripts/{match.Groups[1].Value}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var missing = references.Where(path =>
                !_catalog.Scripts.Contains(path, StringComparer.Ordinal) ||
                !File.Exists(Path.Combine(
                    _repositoryRoot,
                    "ProjectTemplate",
                    "Agents",
                    path.Replace('/', Path.DirectorySeparatorChar))) &&
                !File.Exists(Path.Combine(
                    AgentsRootFor(pack),
                    path.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();
        if (missing.Length > 0)
            return Check("scripts.resolve", Error, $"Missing shared script(s): {string.Join(", ", missing)}.");
        return Check(
            "scripts.resolve",
            Pass,
            references.Length == 0
                ? "No shared scripts referenced."
                : $"{references.Length} shared script reference(s) resolve through the catalog.");
    }

    private EvalCheckResult CheckPromptBudget(byte[] skillBytes)
    {
        var bytes = skillBytes.Length;
        if (bytes > _config.PromptBudget.MaximumThreshold)
        {
            return Check(
                "prompt.budget",
                Error,
                $"{bytes} utf8Bytes exceeds maximum {_config.PromptBudget.MaximumThreshold}.");
        }
        if (bytes > _config.PromptBudget.WarningThreshold)
        {
            return Check(
                "prompt.budget",
                Warning,
                $"{bytes} utf8Bytes exceeds warning threshold {_config.PromptBudget.WarningThreshold}.",
                category: "policy");
        }
        return Check(
            "prompt.budget",
            Pass,
            $"{bytes} utf8Bytes is within warning threshold {_config.PromptBudget.WarningThreshold}.");
    }

    private string CompareBaseline(string slug, IReadOnlyList<EvalCheckResult> checks)
    {
        var path = BaselinePath(slug);
        if (!File.Exists(path))
            return "missing";
        try
        {
            var baseline = EvalJson.Read<EvalBaseline>(path);
            var expected = baseline.ExpectedChecks
                .OrderBy(check => check.Id, StringComparer.Ordinal)
                .ToArray();
            var actual = checks
                .Select(check => new EvalBaselineCheck(
                    check.Id,
                    check.Category,
                    check.Status))
                .OrderBy(check => check.Id, StringComparer.Ordinal)
                .ToArray();
            return baseline.Version == 1 &&
                   baseline.Agent == slug &&
                   expected.SequenceEqual(actual)
                ? "match"
                : "drift";
        }
        catch
        {
            return "drift";
        }
    }

    private void WriteBaseline(EvalAgentResult result)
    {
        var baseline = new EvalBaseline(
            Version: 1,
            Agent: result.Agent,
            ExpectedChecks: result.Checks
                .Select(check => new EvalBaselineCheck(
                    check.Id,
                    check.Category,
                    check.Status))
                .OrderBy(check => check.Id, StringComparer.Ordinal)
                .ToArray());
        var path = BaselinePath(result.Agent);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, EvalJson.Serialize(baseline));
    }

    private void WriteReport(EvalReport report)
    {
        var directory = ResolveConfiguredPath(_config.ArtifactRoot);
        Directory.CreateDirectory(directory);
        var safeTarget = report.Target == "all" ? "all" : report.Target;
        File.WriteAllText(
            Path.Combine(directory, $"{safeTarget}.json"),
            EvalJson.Serialize(report));
    }

    /// <summary>
    /// The composed contract set: core's, then each pack's. A pack ships its agents' contracts in
    /// its own <c>Agents/contracts.json</c> fragment, so reading only core's file reports every
    /// pack agent as contract-less — the exact binding the pack does satisfy.
    /// Slugs are globally unique (D2), so the union cannot collide.
    /// </summary>
    private Dictionary<string, JsonElement> ReadContracts()
    {
        var contracts = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        var manifests = new List<string>
        {
            Path.Combine(_repositoryRoot, "ProjectTemplate", "Agents", "contracts.json"),
        };
        var packsRoot = Path.Combine(_repositoryRoot, "Packs");
        if (Directory.Exists(packsRoot))
        {
            manifests.AddRange(Directory
                .EnumerateDirectories(packsRoot)
                .Select(pack => Path.Combine(pack, "Agents", "contracts.json"))
                .Where(File.Exists));
        }

        foreach (var manifest in manifests)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!document.RootElement.TryGetProperty("agents", out var agents)) continue;
            foreach (var property in agents.EnumerateObject())
                contracts[property.Name] = property.Value.Clone();
        }

        return contracts;
    }

    private string BaselinePath(string slug) =>
        Path.Combine(ResolveConfiguredPath(_config.BaselineRoot), $"{slug}.json");

    private string ResolveConfiguredPath(string path)
    {
        var resolved = Path.GetFullPath(Path.Combine(_repositoryRoot, path));
        var rootPrefix = _repositoryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _repositoryRoot
            : _repositoryRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.Ordinal) &&
            !string.Equals(resolved, _repositoryRoot, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Configured eval path '{path}' escapes the repository root.");
        }
        return resolved;
    }

    private static EvalCheckResult Check(
        string id,
        string status,
        string message,
        string category = "integrity") =>
        new(id, category, status, message);

    private static void ValidateConfig(EvalConfig config)
    {
        // Version 2 is the pack-aware config (doc/pack-infrastructure.md §9): the prompt-budget
        // source names {packRoot} rather than the hardcoded ProjectTemplate root, matching what
        // AgentsRootFor has resolved since packs landed. The string is declarative — the runner
        // resolves the SKILL through AgentsRootFor, never through this literal — so the change is a
        // documentation correction that these two checks were pinning in place.
        if (config.Version != 2)
            throw new InvalidDataException($"Unsupported eval config version {config.Version}.");
        if (config.PromptBudget.Unit != "utf8Bytes")
            throw new InvalidDataException("Prompt budget Unit must be 'utf8Bytes'.");
        if (config.PromptBudget.Source != "{packRoot}/Agents/{agent}/SKILL.md")
            throw new InvalidDataException("Prompt budget Source must name the static SKILL.md input.");
        if (config.PromptBudget.WarningThreshold <= 0 ||
            config.PromptBudget.MaximumThreshold <= config.PromptBudget.WarningThreshold)
        {
            throw new InvalidDataException("Prompt budget thresholds must be positive and maximum must exceed warning.");
        }
        if (Path.IsPathRooted(config.ArtifactRoot) || Path.IsPathRooted(config.BaselineRoot))
            throw new InvalidDataException("Eval artifact and baseline roots must be repository-relative.");
    }

    [GeneratedRegex(@"\.agents/scripts/([A-Za-z0-9_.-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SharedScriptReferenceRegex();

    /// <summary>
    /// The <c>Agents/</c> root that ships this agent. Core stays at <c>ProjectTemplate/Agents</c>
    /// (§2 keeps it in place because that literal path is hardcoded in the catalog, this runner,
    /// four test classes and evalconfig.json); every other pack lives at
    /// <c>Packs/&lt;id&gt;/Agents</c>. Before core became a pack the catalog only ever listed core
    /// agents, so a single hardcoded root was correct — the moment a pack lands it silently
    /// resolves every pack agent to a path that does not exist.
    /// </summary>
    private string AgentsRootFor(string? pack) =>
        string.IsNullOrEmpty(pack) || pack == "core"
            ? Path.Combine(_repositoryRoot, "ProjectTemplate", "Agents")
            : Path.Combine(_repositoryRoot, "Packs", pack, "Agents");

}
