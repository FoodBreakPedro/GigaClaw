using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Verdicts;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Eval;

/// <summary>What the LLM judge was asked. Deterministic: the same fixture, stream and rubric always
/// compose the same prompt, which is what makes <see cref="JudgeModelRecord.PromptDigest"/> a
/// useful thing to record.</summary>
public sealed record LlmJudgeRequest(
    ReplayFixture Fixture,
    ReplayFixtureResult Replay,
    AgentRubric Rubric,
    string Prompt,
    string InputDigest);

/// <summary>What came back, plus the identity of what produced it.</summary>
public sealed record LlmJudgeTranscript(
    string FinalText,
    string Cli,
    string CliVersion,
    string RequestedModel,
    string ReportedModel,
    int MaxTurns);

/// <summary>Seam between the judge and the model. Tests inject a canned transcript; production
/// uses <see cref="LlmJudge.DispatchRealCli"/>.</summary>
public delegate LlmJudgeTranscript LlmJudgeDispatch(LlmJudgeRequest request);

/// <summary>
/// The opt-in second judge. It asks a real model to score the same replayed stream against the
/// same rubric and to answer in the same v1 verdict shape.
///
/// It is **not** reproducible and this layer never pretends otherwise: its verdict is never
/// baselined, never compared byte-for-byte, and never sets the process exit code. What it does
/// produce is (a) a contract-valid verdict, rejected outright if it is not, (b) a record of the
/// model, CLI version and settings that produced it, and (c) a tolerance comparison against the
/// deterministic verdict for the same stream, reported as data.
/// </summary>
public static class LlmJudge
{
    public const string Mode = "llm";

    /// <summary>The LLM judge spends tokens, so it is gated on the same acknowledgement as the
    /// replay layer's real-CLI path.</summary>
    public const string AllowVariable = ReplayRunner.AllowRealCliVariable;

    /// <summary>Overrides which binary is dispatched. Same variable the replay layer honours.</summary>
    public const string BinaryVariable = "GIGACLAW_EVAL_CLAUDE_BIN";

    /// <summary>Model the judge asks for. Recorded on every verdict it produces.</summary>
    public const string ModelVariable = "GIGACLAW_EVAL_JUDGE_MODEL";

    private const int JudgeMaxTurns = 1;

    /// <summary>Runs the judge and returns a contract-valid verdict, or null plus the reasons it
    /// was refused. A verdict this method rejects is never written anywhere.</summary>
    public static (JudgeVerdict? Verdict, JudgeModelRecord Model, IReadOnlyList<string> Errors) Judge(
        LlmJudgeRequest request,
        LlmJudgeDispatch dispatch)
    {
        var transcript = dispatch(request);
        var model = new JudgeModelRecord(
            transcript.Cli,
            transcript.CliVersion,
            transcript.RequestedModel,
            transcript.ReportedModel,
            transcript.MaxTurns,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Prompt))));

        // Same transport a reviewer uses, read by the same host-side reader: an eval verdict and a
        // review verdict are the same object, so they get the same front door.
        if (!VerdictReader.TryRead(transcript.FinalText, out var parsed, out var error))
            return (null, model, [error ?? "the judge produced no readable verdict"]);

        var errors = new List<string>();
        if (!string.Equals(parsed!.Agent, request.Fixture.Agent, StringComparison.Ordinal))
            errors.Add($"the judge verdict names agent '{parsed.Agent}', not '{request.Fixture.Agent}'");
        if (!string.Equals(parsed.InputDigest, request.InputDigest, StringComparison.Ordinal))
            errors.Add($"the judge verdict is bound to {parsed.InputDigest}, not to the replayed stream {request.InputDigest}");
        if (!int.TryParse(parsed.TicketId, NumberStyles.None, CultureInfo.InvariantCulture, out var ticketId))
            errors.Add($"the judge verdict has a non-numeric ticketId '{parsed.TicketId}'");
        else if (ticketId != request.Fixture.Ticket.Id)
            errors.Add($"the judge verdict judges ticket {ticketId}, not fixture ticket {request.Fixture.Ticket.Id}");

        if (errors.Count > 0)
            return (null, model, errors);

        var verdict = new JudgeVerdict(
            SchemaVersion: 1,
            Agent: parsed.Agent,
            TicketId: ticketId,
            Verdict: parsed.Decision,
            Summary: parsed.Summary,
            Categories: parsed.Categories
                .Select(category => new JudgeCategory(category.Name, category.Score, category.Max, category.Notes))
                .ToArray(),
            VetoItems: parsed.VetoItems
                .Select(veto => new JudgeVetoItem(veto.Code, veto.Statement, veto.EvidenceRefs))
                .ToArray(),
            Evidence: parsed.Evidence
                .Select(evidence => new JudgeEvidence(evidence.Kind, evidence.Ref, evidence.Note))
                .ToArray(),
            ReviewedAtUtc: parsed.ReviewedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            InputDigest: parsed.InputDigest);

        return (verdict, model, []);
    }

    /// <summary>Compares the LLM verdict against the deterministic one for the same stream. Always
    /// informational: a difference is measured, not asserted away.</summary>
    public static JudgeTolerance Compare(JudgeVerdict deterministic, JudgeVerdict llm, double tolerancePercent)
    {
        var deterministicPercent = RubricJudge.Percent(deterministic);
        var llmPercent = RubricJudge.Percent(llm);
        var delta = Math.Round(Math.Abs(deterministicPercent - llmPercent), 1, MidpointRounding.AwayFromZero);
        return new JudgeTolerance(
            deterministicPercent,
            llmPercent,
            delta,
            tolerancePercent,
            delta <= tolerancePercent,
            string.Equals(deterministic.Verdict, llm.Verdict, StringComparison.Ordinal));
    }

    /// <summary>
    /// The prompt the model scores against. It carries the rubric verbatim, the normalized stream,
    /// and the exact identity fields the verdict must carry back, so a judge reply that drifts off
    /// the stream it was given is detectable rather than plausible.
    /// </summary>
    public static string BuildPrompt(
        ReplayFixture fixture,
        ReplayFixtureResult replay,
        AgentRubric rubric,
        string inputDigest)
    {
        var prompt = new StringBuilder();
        prompt.Append("# Eval judge\n\n");
        prompt.Append("You are scoring one agent run that has already happened. Judge only what the ")
              .Append("transcript below shows. Do not run tools, do not read files, and do not ")
              .Append("assume work that is not in the transcript.\n\n");

        prompt.Append("## Ticket the agent was given\n\n");
        prompt.Append(ReplayRunner.RenderTicket(fixture.Ticket)).Append('\n');

        prompt.Append("## Rubric (").Append(rubric.Agent).Append(", v").Append(rubric.Version).Append(")\n\n");
        prompt.Append(rubric.Description).Append("\n\n");
        foreach (var criterion in rubric.Criteria)
        {
            prompt.Append("- **").Append(criterion.Name).Append("** (max ")
                  .Append(criterion.Max.ToString(CultureInfo.InvariantCulture)).Append(')');
            if (!string.IsNullOrWhiteSpace(criterion.Statement))
                prompt.Append(" — ").Append(criterion.Statement);
            if (!string.IsNullOrWhiteSpace(criterion.Veto))
                prompt.Append(" — a total failure here is a veto item with code `").Append(criterion.Veto).Append('`');
            prompt.Append('\n');
        }

        prompt.Append("\n## Transcript\n\n");
        prompt.Append("Exit code: ").Append(replay.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none")
              .Append(" · run status: ").Append(replay.RunStatus).Append("\n\n");
        foreach (var captured in replay.Events)
        {
            prompt.Append('[').Append(captured.Index).Append("] ").Append(captured.Kind).Append('\n');
            prompt.Append(captured.Text).Append("\n\n");
        }

        prompt.Append("## Required answer\n\n");
        prompt.Append("Answer with a marker line followed by a fenced json block and nothing else:\n\n");
        prompt.Append("```text\nGIGACLAW-VERDICT v1 ").Append(fixture.Agent)
              .Append(" <SHIP|FIX|BLOCK> artifact-").Append(inputDigest).Append("\n```\n\n");
        prompt.Append("The json block is a v1 verdict with exactly these values:\n\n");
        prompt.Append("- `schemaVersion`: 1\n");
        prompt.Append("- `agent`: \"").Append(fixture.Agent).Append("\"\n");
        prompt.Append("- `ticketId`: ").Append(fixture.Ticket.Id.ToString(CultureInfo.InvariantCulture)).Append('\n');
        prompt.Append("- `inputDigest`: \"").Append(inputDigest).Append("\"\n");
        prompt.Append("- `evidence`: at least `[{\"kind\":\"hash\",\"ref\":\"").Append(inputDigest)
              .Append("\"}]`. Do not cite `path` evidence: you judged a captured stream, not a file.\n");
        prompt.Append("- `categories`: one entry per rubric line above, same names, same `max`.\n");
        prompt.Append("- `vetoItems`: `[]` unless a veto criterion failed outright. Any veto item forbids SHIP.\n");
        prompt.Append("- `reviewedAtUtc`: the current UTC instant, ending in `Z`.\n");
        return prompt.ToString();
    }

    /// <summary>
    /// Production dispatch: the real <c>claude</c> CLI, through the product's own runner. Gated
    /// twice — the caller's <c>--llm</c> flag plus <see cref="AllowVariable"/> — because it spends
    /// real tokens and must never be reachable from a plain CI invocation.
    /// </summary>
    public static LlmJudgeTranscript DispatchRealCli(LlmJudgeRequest request)
    {
        if (Environment.GetEnvironmentVariable(AllowVariable) != "1")
        {
            throw new ArgumentException(
                $"--llm asks a real model and spends real tokens. Set {AllowVariable}=1 to acknowledge the cost.");
        }

        var configured = Environment.GetEnvironmentVariable(BinaryVariable);
        var binary = !string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
            ? configured
            : OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        Environment.SetEnvironmentVariable("GIGACLAW_CLAUDE_BIN", binary);
        var model = Environment.GetEnvironmentVariable(ModelVariable);

        var workspace = Path.Combine(Path.GetTempPath(), "gigaclaw-judge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var context = new ClaudeRunContext
            {
                ProjectSlug = "gigaclaw-eval-judge",
                WorkspacePath = workspace,
                AgentName = "eval-judge",
                SkillFile = "eval-judge/SKILL.md",
                InlineSkillContent = request.Prompt,
                Model = model,
                MaxTurns = JudgeMaxTurns,
                ConcurrencyGroup = $"eval-judge:{request.Fixture.Id}",
                PersistSession = false,
                MaxRunDuration = TimeSpan.FromMinutes(5),
            };

            var run = new ClaudeRunner(
                new SessionRegistry(),
                new AgentRunRegistry(),
                new RunConcurrencyGate(maxConcurrent: 1),
                NullLogger<ClaudeRunner>.Instance)
                .RunAsync(context, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var events = run.SnapshotBuffer();
            var finalText = events
                .LastOrDefault(captured => string.Equals(captured.Kind, "assistant", StringComparison.Ordinal))
                ?.Text ?? "";
            var reported = events
                .FirstOrDefault(captured => string.Equals(captured.Kind, "system", StringComparison.Ordinal))
                ?.Text ?? "";

            return new LlmJudgeTranscript(
                finalText,
                binary,
                CliVersion(binary),
                model ?? "<cli default>",
                ReportedModel(reported),
                JudgeMaxTurns);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Pulls `model=…` out of the CLI's own init event so the record says what actually
    /// answered, not only what was asked for.</summary>
    internal static string ReportedModel(string systemEventText)
    {
        foreach (var token in systemEventText.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            if (token.StartsWith("model=", StringComparison.Ordinal))
                return token["model=".Length..];
        return "<unreported>";
    }

    private static string CliVersion(string binary)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(binary)
            {
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) return "<unknown>";
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10_000);
            return output.Length == 0 ? "<unknown>" : output;
        }
        catch (Exception)
        {
            return "<unknown>";
        }
    }
}
