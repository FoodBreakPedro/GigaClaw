using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GigaClaw.Catalog;
using GigaClaw.Core.Automation;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Eval;

/// <summary>
/// Canned-ticket replay: dispatches a catalog agent against a fixture ticket through the real
/// <see cref="ClaudeRunner"/> code path, with the mock <c>claude</c> CLI standing in for the model.
/// Nothing here talks to the network or to the user's GigaClaw data directory: every run gets a
/// throwaway workspace under the system temp folder, and the mock replays a committed NDJSON
/// scenario, so the same fixture produces the same events every time.
///
/// Grading the *content* of those events belongs to <see cref="JudgeRunner"/> and sampling
/// variance across repeated runs to <see cref="MonteCarloRunner"/> — this layer only captures and
/// mechanically asserts what came back.
/// </summary>
public sealed partial class ReplayRunner
{
    private const string Pass = "pass";
    private const string Error = "error";
    private const string MockMode = "mock";
    private const string RealCliMode = "real-cli";

    /// <summary>Opt-in gate for the costed real-CLI path. Set to "1" to allow it.</summary>
    public const string AllowRealCliVariable = "GIGACLAW_EVAL_ALLOW_REAL_CLI";

    private readonly string _repositoryRoot;
    private readonly EvalConfig _config;
    private readonly ReplayConfig _replay;
    private readonly SystemCatalog _catalog;

    public ReplayRunner(string repositoryRoot)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _config = EvalJson.Read<EvalConfig>(
            Path.Combine(_repositoryRoot, "GigaClaw.Eval", "evalconfig.json"));
        _replay = _config.Replay ?? ReplayConfig.Default;
        ValidateReplayConfig(_replay);
        _catalog = EvalJson.Read<SystemCatalog>(Path.Combine(_repositoryRoot, "catalog.json"));
        _ = ResolveConfiguredPath(_config.ArtifactRoot);
        foreach (var root in _replay.Roots)
            _ = ResolveConfiguredPath(root);
    }

    /// <param name="target">A fixture id, a pipeline family, a catalog agent slug, or "all".</param>
    /// <param name="realCli">Opt in to the costed real-CLI path instead of the mock. Requires
    /// <see cref="AllowRealCliVariable"/>=1 in the environment.</param>
    public ReplayRunResult Run(string target, bool realCli = false, bool writeReport = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var fixtures = ResolveFixtures(target);
        var claudeBinary = ResolveClaudeBinary(realCli);

        // ProcessLifecycleManager caches its binary lookup in a static Lazy on first dispatch,
        // so the variable must be in place before any ClaudeRunner in this process spawns.
        Environment.SetEnvironmentVariable("GIGACLAW_CLAUDE_BIN", claudeBinary);

        var results = fixtures
            .Select(fixture => Replay(fixture, realCli))
            .ToArray();

        var reports = results
            .GroupBy(entry => entry.Fixture.Agent, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ReplayReport(
                Version: 1,
                Mode: realCli ? RealCliMode : MockMode,
                Target: target,
                Agent: group.Key,
                Fixtures: group
                    .Select(entry => entry.Result)
                    .OrderBy(result => result.Fixture, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();

        if (writeReport)
            foreach (var report in reports)
                WriteReport(report);
        stopwatch.Stop();

        var failed = reports
            .SelectMany(report => report.Fixtures)
            .Any(fixture => fixture.Status != Pass);
        return new ReplayRunResult(reports, failed ? 1 : 0, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Replays one in-memory fixture. Lets a caller exercise an expectation that does not
    /// hold without committing a fixture whose only purpose is to fail.</summary>
    public ReplayFixtureResult ReplaySingle(ReplayFixture fixture, bool realCli = false)
    {
        Environment.SetEnvironmentVariable("GIGACLAW_CLAUDE_BIN", ResolveClaudeBinary(realCli));
        return Replay(fixture, realCli).Result;
    }

    /// <summary>Replays one fixture and additionally reports what the dispatch consumed. The usage
    /// is returned beside the result rather than inside it precisely so it stays out of the
    /// reproducible report — see <see cref="ReplayUsage"/>.</summary>
    public (ReplayFixtureResult Result, ReplayUsage Usage) ReplayObserved(
        ReplayFixture fixture,
        bool realCli = false)
    {
        Environment.SetEnvironmentVariable("GIGACLAW_CLAUDE_BIN", ResolveClaudeBinary(realCli));
        var replayed = Replay(fixture, realCli);
        return (replayed.Result, replayed.Usage);
    }

    /// <summary>Resolves the same target vocabulary <see cref="Run"/> accepts, so a layer built on
    /// top of replay selects fixtures by exactly the same rules.</summary>
    public IReadOnlyList<ReplayFixture> ResolveTarget(string target) => ResolveFixtures(target);

    public IReadOnlyList<ReplayFixture> LoadFixtures()
    {
        // Roots are enumerated in configured order (core first by convention) and files ordinally
        // within each root, so the fixture list is stable across runs and platforms.
        var fixtures = _replay.Roots
            .SelectMany(root => Directory
                .EnumerateFiles(ResolveConfiguredPath(root), "*.json")
                .OrderBy(path => path, StringComparer.Ordinal))
            .Select(ReadFixture)
            .ToArray();
        var duplicate = fixtures
            .GroupBy(fixture => fixture.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Fixture id '{duplicate.Key}' appears in more than one fixture root; ids must be globally unique.");
        }
        return fixtures;
    }

    private ReplayFixture ReadFixture(string path)
    {
        var fixture = EvalJson.Read<ReplayFixture>(path);
        if (fixture.Version != 1)
            throw new InvalidDataException($"Unsupported fixture version {fixture.Version} in {path}.");
        var expectedId = Path.GetFileNameWithoutExtension(path);
        if (!string.Equals(fixture.Id, expectedId, StringComparison.Ordinal))
            throw new InvalidDataException($"Fixture '{fixture.Id}' must be stored as {expectedId}.json.");
        if (!_catalog.Agents.Any(agent => agent.Slug.Equals(fixture.Agent, StringComparison.Ordinal)))
            throw new InvalidDataException($"Fixture '{fixture.Id}' names agent '{fixture.Agent}', which is not in the catalog.");
        if (!File.Exists(ScenarioPath(fixture)))
            throw new InvalidDataException($"Fixture '{fixture.Id}' references missing scenario '{fixture.Scenario}.ndjson'.");
        return fixture;
    }

    private ReplayFixture[] ResolveFixtures(string target)
    {
        var all = LoadFixtures();
        if (all.Count == 0)
            throw new ArgumentException($"No replay fixtures found under {string.Join(", ", _replay.Roots)}.");
        if (target == "all")
            return all.OrderBy(fixture => fixture.Id, StringComparer.Ordinal).ToArray();

        var matches = all
            .Where(fixture =>
                fixture.Id.Equals(target, StringComparison.Ordinal) ||
                fixture.Family.Equals(target, StringComparison.Ordinal) ||
                fixture.Agent.Equals(target, StringComparison.Ordinal))
            .OrderBy(fixture => fixture.Id, StringComparer.Ordinal)
            .ToArray();
        if (matches.Length == 0)
        {
            var known = string.Join(", ", all.Select(fixture => fixture.Id));
            throw new ArgumentException(
                $"Unknown replay target '{target}'. Choose a fixture id ({known}), a family, an agent slug, or 'all'.");
        }
        return matches;
    }

    private (ReplayFixture Fixture, ReplayFixtureResult Result, ReplayUsage Usage) Replay(
        ReplayFixture fixture,
        bool realCli)
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "gigaclaw-replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            MaterializeWorkspace(fixture, workspace, out var scenarioDirectory);
            var run = Dispatch(fixture, workspace, scenarioDirectory, realCli);
            var events = Normalize(run.SnapshotBuffer(), workspace);
            var checks = Assert(fixture, run.Status.ToString(), run.ExitCode, events);
            var status = checks.Any(check => check.Status == Error) ? Error : Pass;
            return (fixture, new ReplayFixtureResult(
                fixture.Id,
                fixture.Family,
                fixture.Agent,
                fixture.Scenario,
                status,
                run.ExitCode,
                run.Status.ToString(),
                Digest(events),
                events,
                checks),
                new ReplayUsage(run.TotalCostUsd ?? 0m, run.TotalTokens, run.TotalCostUsd is not null));
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { /* a stray mock child may still hold a handle; temp cleans up */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private AgentRun Dispatch(
        ReplayFixture fixture,
        string workspace,
        string scenarioDirectory,
        bool realCli)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!realCli)
        {
            // Explicit scenario selection beats the in-prompt marker, so a fixture never
            // depends on the wording of the agent's committed SKILL.md.
            environment["GIGACLAW_MOCK_SCENARIOS_DIR"] = scenarioDirectory;
            environment["GIGACLAW_MOCK_SCENARIO"] = fixture.Scenario;
        }

        var context = new ClaudeRunContext
        {
            ProjectSlug = "gigaclaw-eval-replay",
            WorkspacePath = workspace,
            AgentName = fixture.Agent,
            SkillFile = $"{fixture.Agent}/SKILL.md",
            TicketId = fixture.Ticket.Id,
            TicketTitle = fixture.Ticket.Title,
            TicketStatus = fixture.Ticket.Status,
            MaxTurns = fixture.MaxTurns,
            ConcurrencyGroup = $"eval-replay:{fixture.Id}",
            Env = environment,
            // A replay is always a cold start: no resume, no session carried between fixtures.
            PersistSession = false,
            MaxRunDuration = TimeSpan.FromSeconds(_replay.TimeoutSeconds),
        };

        var runner = new ClaudeRunner(
            new SessionRegistry(),
            new AgentRunRegistry(),
            new RunConcurrencyGate(maxConcurrent: 1),
            NullLogger<ClaudeRunner>.Instance);
        return runner
            .RunAsync(context, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>Builds the throwaway workspace a replay dispatches into: the agent's committed
    /// SKILL.md, memory index, preamble and contract manifest (so the prompt matches production),
    /// plus the rendered fixture ticket and the scenario the mock replays. The agent's own files
    /// come from the pack that ships it — resolved through the catalog's <c>Pack</c> field, the
    /// same way the static layer resolves them — while pieces a pack does not ship (the preamble,
    /// and the contract manifest for a pack without one) fall back to core's template.</summary>
    private void MaterializeWorkspace(
        ReplayFixture fixture,
        string workspace,
        out string scenarioDirectory)
    {
        var coreRoot = Path.Combine(_repositoryRoot, "ProjectTemplate", "Agents");
        var agentsRoot = AgentsRootFor(fixture.Agent);
        var agentsDirectory = Path.Combine(workspace, ".agents");
        var agentDirectory = Path.Combine(agentsDirectory, fixture.Agent);
        Directory.CreateDirectory(Path.Combine(agentDirectory, "memory"));

        File.Copy(
            PreferPack(agentsRoot, coreRoot, "preamble.md"),
            Path.Combine(agentsDirectory, "preamble.md"));
        File.Copy(
            PreferPack(agentsRoot, coreRoot, "contracts.json"),
            Path.Combine(agentsDirectory, "contracts.json"));
        File.Copy(
            Path.Combine(agentsRoot, fixture.Agent, "SKILL.md"),
            Path.Combine(agentDirectory, "SKILL.md"));
        File.Copy(
            Path.Combine(agentsRoot, fixture.Agent, "memory", "MEMORY.md"),
            Path.Combine(agentDirectory, "memory", "MEMORY.md"));

        // Offline stand-in for the ticket the agent would otherwise fetch over the REST API.
        var channel = Path.Combine(agentsDirectory, "channel");
        Directory.CreateDirectory(channel);
        File.WriteAllText(
            Path.Combine(channel, $"ticket-{fixture.Ticket.Id}.md"),
            RenderTicket(fixture.Ticket));

        scenarioDirectory = Path.Combine(workspace, "mock-scenarios");
        Directory.CreateDirectory(scenarioDirectory);
        File.Copy(
            ScenarioPath(fixture),
            Path.Combine(scenarioDirectory, $"{fixture.Scenario}.ndjson"));
    }

    internal static string RenderTicket(ReplayTicket ticket)
    {
        var text = new StringBuilder();
        text.Append("# Ticket #").Append(ticket.Id).Append(" — ").Append(ticket.Title).Append('\n');
        text.Append("\nStatus: ").Append(ticket.Status).Append('\n');
        text.Append("Assignee: ").Append(ticket.Assignee).Append('\n');
        text.Append("\n## Description\n\n").Append(ticket.Description.TrimEnd()).Append('\n');
        if (ticket.Comments.Count > 0)
        {
            text.Append("\n## Comments\n");
            foreach (var comment in ticket.Comments)
                text.Append('\n').Append(comment.Author).Append(": ").Append(comment.Body.TrimEnd()).Append('\n');
        }
        return text.ToString();
    }

    private static IReadOnlyList<EvalCheckResult> Assert(
        ReplayFixture fixture,
        string runStatus,
        int? exitCode,
        IReadOnlyList<ReplayEvent> events)
    {
        var checks = new List<EvalCheckResult>
        {
            events.Count == 0
                ? Check("replay.dispatch", Error, "The dispatched agent produced no stream events.")
                : Check("replay.dispatch", Pass, $"Captured {events.Count} stream event(s)."),
            exitCode == fixture.Expect.ExitCode
                ? Check("replay.exit", Pass, $"Exit code {fixture.Expect.ExitCode} matches the fixture.")
                : Check("replay.exit", Error, $"Exit code {exitCode?.ToString() ?? "<none>"} differs from expected {fixture.Expect.ExitCode}."),
            string.Equals(runStatus, fixture.Expect.RunStatus, StringComparison.Ordinal)
                ? Check("replay.status", Pass, $"Run status '{runStatus}' matches the fixture.")
                : Check("replay.status", Error, $"Run status '{runStatus}' differs from expected '{fixture.Expect.RunStatus}'.")
        };

        // Subsequence, not equality: an added synthetic event from the runtime must not fail a
        // fixture, but a missing or reordered expected event must.
        var remaining = new Queue<string>(fixture.Expect.EventKinds);
        foreach (var captured in events)
            if (remaining.Count > 0 && string.Equals(remaining.Peek(), captured.Kind, StringComparison.Ordinal))
                remaining.Dequeue();
        checks.Add(remaining.Count == 0
            ? Check("replay.events", Pass, $"All {fixture.Expect.EventKinds.Count} expected event kind(s) appear in order.")
            : Check("replay.events", Error, $"Expected event kind(s) missing or out of order: {string.Join(", ", remaining)}."));

        var lastAssistantText = events
            .LastOrDefault(captured => captured.Kind == "assistant")?.Text ?? "";
        checks.Add(lastAssistantText.Contains(fixture.Expect.FinalTextContains, StringComparison.Ordinal)
            ? Check("replay.text", Pass, "The final assistant message contains the expected marker.")
            : Check("replay.text", Error, $"The final assistant message does not contain '{fixture.Expect.FinalTextContains}'."));

        return checks;
    }

    /// <summary>Strips everything that varies between two runs of the same fixture — the throwaway
    /// workspace path, the generated session id, and line-ending noise — so byte-identical inputs
    /// yield byte-identical reports. Timings and costs are never recorded, matching the static layer.
    ///
    /// <para>The only event that ever carries the workspace path is the "launch" event ClaudeRunner
    /// synthesizes (<c>cwd={ctx.WorkspacePath}</c>) — nothing rubric-scored reads it, which is why a
    /// leak here moves <c>inputDigest</c>/<c>evidence[].ref</c> (the digest hashes every event) but
    /// never a category score or note. The exact-substring replaces below catch the ordinary
    /// backslash and forward-slash forms. The regex pass after them is a structural fallback for any
    /// OTHER representation of the same path — an 8.3 short ancestor segment
    /// (<c>C:\Users\RUNNER~1\...</c>), a JSON-escaped doubled backslash, or a differently-cased
    /// segment — all of which still end in the <c>gigaclaw-replay-&lt;32 hex&gt;</c> leaf this run
    /// generated. Matching on that unique leaf and discarding whatever precedes it — however it is
    /// spelled — kills the leak without enumerating every path form Windows can produce it in.</para>
    /// </summary>
    internal static IReadOnlyList<ReplayEvent> Normalize(
        IReadOnlyList<StreamEvent> events,
        string workspace)
    {
        var workspacePosix = workspace.Replace('\\', '/');
        var workspaceGuard = WorkspaceGuardRegex(workspace);
        return events
            .Select((captured, index) =>
            {
                var text = (captured.Text ?? "").Replace("\r\n", "\n");
                text = text.Replace(workspace, "{{workspace}}", StringComparison.Ordinal);
                text = text.Replace(workspacePosix, "{{workspace}}", StringComparison.Ordinal);
                if (workspaceGuard is not null)
                    text = workspaceGuard.Replace(text, "{{workspace}}");
                // The launch event carries an 8-char session prefix; the CLI's own events carry
                // the full guid. Scrub the prefix form first so it is not left half-replaced.
                text = ShortSessionIdRegex().Replace(text, "session={{session_id}}");
                text = SessionIdRegex().Replace(text, "{{session_id}}");
                return new ReplayEvent(index, captured.Kind, text.Trim());
            })
            .ToArray();
    }

    /// <summary>Builds the structural fallback matcher described on <see cref="Normalize"/>: the
    /// workspace's own leaf directory name (<c>gigaclaw-replay-&lt;32 hex&gt;</c>, unique to this
    /// run) preceded by a run of non-whitespace, non-quote characters, matched case-insensitively so
    /// a differently-cased drive letter or ancestor segment does not defeat it. The leaf is split out
    /// by hand rather than through <see cref="Path.GetFileName(string)"/> because that call only
    /// recognizes the current OS's separator, and a leaked path can carry the OTHER platform's
    /// separator (this is also what lets a test construct a Windows-shaped leak and run it on any
    /// platform). Null when the workspace has no leaf to key on (defensive, not expected in
    /// practice).</summary>
    internal static Regex? WorkspaceGuardRegex(string workspace)
    {
        var leaf = workspace
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        return string.IsNullOrEmpty(leaf)
            ? null
            : new Regex(
                // '=' is excluded from the prefix run (on top of whitespace/quotes) so the match
                // cannot cross a "key=value" delimiter like the launch event's own "cwd=" or
                // "session=" — none of GigaClaw's generated temp paths ever contain '=' — and so
                // the replacement leaves "cwd=" itself in place exactly as the exact-substring
                // replaces above do.
                $"""[^\s"'=]*?{Regex.Escape(leaf)}""",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    internal static string Digest(IReadOnlyList<ReplayEvent> events) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalStream(events))));

    /// <summary>The exact bytes <see cref="Digest"/> hashes — index, kind and normalized text, one
    /// event per tab-separated line. This is the ONLY source of truth for what
    /// <c>evidence[].ref</c>/<c>inputDigest</c> are derived from; a hash tells you two runs differ,
    /// never where, so <see cref="JudgeStreamEvidence"/> diffs this string against a committed
    /// reference instead of the hash.</summary>
    internal static string CanonicalStream(IReadOnlyList<ReplayEvent> events) =>
        string.Join(
            "\n",
            events.Select(captured => $"{captured.Index}\t{captured.Kind}\t{captured.Text}"));

    /// <summary>Hermetic by default: resolves the mock CLI built from GigaClaw.ClaudeMock. The
    /// real CLI is an explicit, costed opt-in and is gated on an environment variable so it can
    /// never be reached by a plain `replay` invocation in CI.</summary>
    private string ResolveClaudeBinary(bool realCli)
    {
        var executable = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        if (realCli)
        {
            if (Environment.GetEnvironmentVariable(AllowRealCliVariable) != "1")
            {
                throw new ArgumentException(
                    $"--real-cli spends real tokens. Set {AllowRealCliVariable}=1 to acknowledge the cost.");
            }
            var configured = Environment.GetEnvironmentVariable("GIGACLAW_EVAL_CLAUDE_BIN");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;
            return executable;
        }

        var mockRoot = Path.Combine(_repositoryRoot, "GigaClaw.ClaudeMock", "bin");
        var mock = Directory.Exists(mockRoot)
            ? Directory
                .EnumerateFiles(mockRoot, executable, SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .FirstOrDefault()
            : null;
        if (mock is null)
        {
            throw new ArgumentException(
                "The mock claude CLI was not found. Run: dotnet build GigaClaw.ClaudeMock -c Release");
        }
        return mock;
    }

    private void WriteReport(ReplayReport report)
    {
        var directory = Path.Combine(
            ResolveConfiguredPath(_config.ArtifactRoot),
            _replay.ArtifactSubdirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, $"{report.Agent}.json"),
            EvalJson.Serialize(report));
    }

    public string ReportPath(string agent) => Path.Combine(
        ResolveConfiguredPath(_config.ArtifactRoot),
        _replay.ArtifactSubdirectory,
        $"{agent}.json");

    /// <summary>A scenario lives in <c>scenarios/</c> beside the fixture root that ships it, so
    /// the roots are probed in configured order. When no root carries it, the first root's path is
    /// returned so <see cref="ReadFixture"/> reports a concrete missing file.</summary>
    private string ScenarioPath(ReplayFixture fixture)
    {
        var candidates = _replay.Roots
            .Select(root => Path.Combine(
                ResolveConfiguredPath(root),
                "scenarios",
                $"{fixture.Scenario}.ndjson"))
            .ToArray();
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    /// <summary>The <c>Agents/</c> root that ships this agent, resolved through the catalog's
    /// <c>Pack</c> field exactly like <c>StaticEvalRunner.AgentsRootFor</c>: core stays at
    /// <c>ProjectTemplate/Agents</c>, every other pack at <c>Packs/&lt;id&gt;/Agents</c>.</summary>
    private string AgentsRootFor(string agentSlug)
    {
        var pack = _catalog.Agents
            .FirstOrDefault(agent => agent.Slug.Equals(agentSlug, StringComparison.Ordinal))
            ?.Pack;
        return string.IsNullOrEmpty(pack) || pack == "core"
            ? Path.Combine(_repositoryRoot, "ProjectTemplate", "Agents")
            : Path.Combine(_repositoryRoot, "Packs", pack, "Agents");
    }

    private static string PreferPack(string agentsRoot, string coreRoot, string fileName)
    {
        var packed = Path.Combine(agentsRoot, fileName);
        return File.Exists(packed) ? packed : Path.Combine(coreRoot, fileName);
    }

    private string ResolveConfiguredPath(string path)
    {
        var resolved = Path.GetFullPath(Path.Combine(_repositoryRoot, path));
        var rootPrefix = _repositoryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _repositoryRoot
            : _repositoryRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.Ordinal) &&
            !string.Equals(resolved, _repositoryRoot, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Configured eval path '{path}' escapes the repository root.");
        }
        return resolved;
    }

    private static void ValidateReplayConfig(ReplayConfig config)
    {
        foreach (var root in config.Roots)
            if (Path.IsPathRooted(root))
                throw new InvalidDataException("Replay fixture roots must be repository-relative.");
        if (config.ArtifactSubdirectory.Length == 0 ||
            config.ArtifactSubdirectory.Contains('/') ||
            config.ArtifactSubdirectory.Contains('\\'))
        {
            throw new InvalidDataException("Replay artifact subdirectory must be a single path segment.");
        }
        if (config.TimeoutSeconds <= 0)
            throw new InvalidDataException("Replay timeout must be positive.");
    }

    private static EvalCheckResult Check(string id, string status, string message) =>
        new(id, "integrity", status, message);

    [GeneratedRegex(
        "[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}",
        RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdRegex();

    [GeneratedRegex("session=[0-9a-fA-F]{8}", RegexOptions.CultureInvariant)]
    private static partial Regex ShortSessionIdRegex();
}
