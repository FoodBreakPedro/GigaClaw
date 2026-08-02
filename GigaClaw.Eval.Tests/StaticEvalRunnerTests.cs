using System.Text.Json;
using GigaClaw.Catalog;

namespace GigaClaw.Eval.Tests;

public sealed class StaticEvalRunnerTests
{
    [Fact]
    public void Run_UsesCatalogInventoryAndWritesDeterministicBaselineAndReport()
    {
        using var fixture = new EvalFixture();
        fixture.AddAgent(
            "worker",
            """
            # Worker

            Run `python3 .agents/scripts/check.py output.md`.
            """);
        fixture.AddUncataloguedAgent("not-in-catalog");
        fixture.WriteInputs();
        var runner = new StaticEvalRunner(fixture.Root);

        var generated = runner.Run("all", updateBaselines: true);
        var firstReport = File.ReadAllText(fixture.ReportPath("all"));
        var repeated = runner.Run("all");
        var secondReport = File.ReadAllText(fixture.ReportPath("all"));

        Assert.Equal(0, generated.ExitCode);
        Assert.Equal(0, repeated.ExitCode);
        Assert.Equal(firstReport, secondReport);
        var result = Assert.Single(repeated.Report.Agents);
        Assert.Equal("worker", result.Agent);
        Assert.Equal("match", result.BaselineStatus);
        Assert.All(result.Checks, check => Assert.Equal("pass", check.Status));
        Assert.True(File.Exists(fixture.BaselinePath("worker")));
    }

    [Fact]
    public void Run_PrunesReportsOfAgentsThatLeftTheCatalog()
    {
        using var fixture = new EvalFixture();
        fixture.AddAgent("worker", "# Worker\n");
        fixture.WriteInputs();
        // A report left behind by an agent that has since been removed from the catalog.
        var orphan = fixture.ReportPath("retired-agent");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        File.WriteAllText(orphan, "{}");

        new StaticEvalRunner(fixture.Root).Run("all", updateBaselines: true);

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(fixture.ReportPath("all")));
    }

    [Fact]
    public void Run_ParsesOptionalFrontmatterAndReportsMalformedStaticInputs()
    {
        using var fixture = new EvalFixture();
        fixture.AddAgent(
            "worker",
            """
            ---
            name: other-worker
            description: bad name
            ---

            # Worker

            Run `.agents/scripts/missing.py`.
            """,
            createMemory: false);
        fixture.WriteInputs();
        var result = new StaticEvalRunner(fixture.Root)
            .Run("worker", updateBaselines: true, writeReport: false);
        var agent = Assert.Single(result.Report.Agents);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            agent.Checks,
            check => check.Id == "frontmatter.parse" && check.Status == "error");
        Assert.Contains(
            agent.Checks,
            check => check.Id == "memory.stub" && check.Status == "error");
        Assert.Contains(
            agent.Checks,
            check => check.Id == "scripts.resolve" && check.Status == "error");
    }

    [Fact]
    public void BaselineMode_AllowsRecordedWarnings_StrictPromotesThem()
    {
        using var fixture = new EvalFixture(warningThreshold: 32, maximumThreshold: 1024);
        fixture.AddAgent("worker", "# Worker\n\nThis prompt is deliberately longer than thirty-two bytes.\n");
        fixture.WriteInputs();
        var runner = new StaticEvalRunner(fixture.Root);

        var baseline = runner.Run("worker", updateBaselines: true, writeReport: false);
        var strict = runner.Run("worker", strict: true, writeReport: false);

        Assert.Equal(0, baseline.ExitCode);
        Assert.Equal(1, strict.ExitCode);
        Assert.Contains(
            baseline.Report.Agents[0].Checks,
            check => check.Id == "prompt.budget" && check.Status == "warning");
        Assert.Equal("match", strict.Report.Agents[0].BaselineStatus);
    }

    [Fact]
    public void Run_FailsWhenCommittedBaselineDrifts()
    {
        using var fixture = new EvalFixture(warningThreshold: 80, maximumThreshold: 1024);
        fixture.AddAgent("worker", "# Worker\n");
        fixture.WriteInputs();
        var runner = new StaticEvalRunner(fixture.Root);
        Assert.Equal(
            0,
            runner.Run("worker", updateBaselines: true, writeReport: false).ExitCode);

        File.WriteAllText(
            Path.Combine(
                fixture.Root,
                "ProjectTemplate",
                "Agents",
                "worker",
                "SKILL.md"),
            "# Worker\n\n" + new string('x', 100));
        var drifted = runner.Run("worker", writeReport: false);

        Assert.Equal(1, drifted.ExitCode);
        Assert.Equal("drift", drifted.Report.Agents[0].BaselineStatus);
        Assert.Contains("1 baseline error(s) (baseline.drift: 1)", Program.Summary(drifted.Report));
    }

    [Fact]
    public void Summary_ReportsMissingBaselineThatDrivesTheExitCode()
    {
        using var fixture = new EvalFixture();
        fixture.AddAgent("worker", "# Worker\n");
        fixture.WriteInputs();
        var runner = new StaticEvalRunner(fixture.Root);

        var missing = runner.Run("worker", writeReport: false);
        var summary = Program.Summary(missing.Report);

        // baseline.missing is an agent-level condition, not a check: it must still surface in the
        // summary whenever it drives a non-zero exit, instead of hiding behind "0 error(s)".
        Assert.Equal(1, missing.ExitCode);
        Assert.Equal("missing", missing.Report.Agents[0].BaselineStatus);
        Assert.Contains("1 baseline error(s) (baseline.missing: 1)", summary);

        var repaired = runner.Run("worker", updateBaselines: true, writeReport: false);

        Assert.Equal(0, repaired.ExitCode);
        Assert.DoesNotContain("baseline error", Program.Summary(repaired.Report));
    }

    [Fact]
    public void Constructor_RejectsConfiguredWriteRootOutsideRepository()
    {
        using var fixture = new EvalFixture();
        fixture.AddAgent("worker", "# Worker\n");
        fixture.WriteInputs();
        var config = new EvalConfig(
            2,
            "../outside",
            "GigaClaw.Eval/baselines",
            new PromptBudgetConfig(
                "{packRoot}/Agents/{agent}/SKILL.md",
                "utf8Bytes",
                1024,
                2048));
        File.WriteAllText(
            Path.Combine(fixture.Root, "GigaClaw.Eval", "evalconfig.json"),
            JsonSerializer.Serialize(config));

        var exception = Assert.Throws<InvalidDataException>(
            () => new StaticEvalRunner(fixture.Root));

        Assert.Contains("escapes the repository root", exception.Message);
    }

    private sealed class EvalFixture : IDisposable
    {
        private readonly List<AgentCatalogEntry> _agents = [];
        private readonly Dictionary<string, string> _riskClasses = [];
        private readonly int _warningThreshold;
        private readonly int _maximumThreshold;

        public EvalFixture(int warningThreshold = 1024, int maximumThreshold = 2048)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "gigaclaw-eval-test-" + Guid.NewGuid().ToString("N"));
            _warningThreshold = warningThreshold;
            _maximumThreshold = maximumThreshold;
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void AddAgent(
            string slug,
            string skill,
            bool createMemory = true)
        {
            var directory = Path.Combine(Root, "ProjectTemplate", "Agents", slug);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), skill);
            if (createMemory)
            {
                Directory.CreateDirectory(Path.Combine(directory, "memory"));
                File.WriteAllText(
                    Path.Combine(directory, "memory", "MEMORY.md"),
                    $"# Memory Index — {slug}\n");
            }
            _riskClasses[slug] = "test";
            _agents.Add(new AgentCatalogEntry(
                slug,
                Pack: PackCatalogSource.CoreId,
                ContractPresent: true,
                RiskClass: "test",
                ExplicitModelMapping: "test-model",
                ModelCriterion: "test criterion",
                ActionModels: [],
                ProjectFallbackRequired: false,
                Teams: ["test-team"],
                EnabledDispatchingAutomations: ["test-dispatch"],
                DisabledDispatchingAutomations: [],
                EvalBaselinePresent: false,
                EvalFixturePresent: false));
        }

        public void AddUncataloguedAgent(string slug)
        {
            var directory = Path.Combine(Root, "ProjectTemplate", "Agents", slug);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"# {slug}\n");
        }

        public void WriteInputs()
        {
            var agentsRoot = Path.Combine(Root, "ProjectTemplate", "Agents");
            var scriptsRoot = Path.Combine(agentsRoot, "scripts");
            Directory.CreateDirectory(scriptsRoot);
            File.WriteAllText(Path.Combine(scriptsRoot, "check.py"), "print('ok')\n");
            var contracts = new
            {
                version = 1,
                agents = _riskClasses.ToDictionary(
                    entry => entry.Key,
                    entry => new { riskClass = entry.Value },
                    StringComparer.Ordinal)
            };
            File.WriteAllText(
                Path.Combine(agentsRoot, "contracts.json"),
                JsonSerializer.Serialize(contracts));

            var catalog = new SystemCatalog(
                Version: CatalogGenerator.SchemaVersion,
                Summary: new CatalogSummary(
                    _agents.Count,
                    _agents.Count,
                    1,
                    1,
                    _agents.Count,
                    1,
                    1),
                Packs: [new PackCatalogEntry(PackCatalogSource.CoreId, "1.0.0", PackCatalogSource.CoreKind, [], _agents.Count)],
                Agents: _agents.OrderBy(agent => agent.Slug, StringComparer.Ordinal).ToArray(),
                Teams: [new TeamCatalogEntry("test-team", _agents.Select(agent => agent.Slug).ToArray())],
                Automations: [new AutomationCatalogEntry("test-dispatch", true, _agents.Select(agent => agent.Slug).ToArray(), ["test-model"])],
                Scripts: ["scripts/check.py"]);
            File.WriteAllText(
                Path.Combine(Root, "catalog.json"),
                JsonSerializer.Serialize(catalog));

            var evalDirectory = Path.Combine(Root, "GigaClaw.Eval");
            Directory.CreateDirectory(evalDirectory);
            var config = new EvalConfig(
                2,
                "artifacts/eval",
                "GigaClaw.Eval/baselines",
                new PromptBudgetConfig(
                    "{packRoot}/Agents/{agent}/SKILL.md",
                    "utf8Bytes",
                    _warningThreshold,
                    _maximumThreshold));
            File.WriteAllText(
                Path.Combine(evalDirectory, "evalconfig.json"),
                JsonSerializer.Serialize(config));
        }

        public string ReportPath(string target) =>
            Path.Combine(Root, "artifacts", "eval", $"{target}.json");

        public string BaselinePath(string slug) =>
            Path.Combine(Root, "GigaClaw.Eval", "baselines", $"{slug}.json");

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { }
        }
    }
}
