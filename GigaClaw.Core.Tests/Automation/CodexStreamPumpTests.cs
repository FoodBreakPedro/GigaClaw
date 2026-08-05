namespace GigaClaw.Core.Tests.Automation;

public sealed class CodexStreamPumpTests
{
    [Fact]
    public void NewRunArguments_PinNonInteractiveSafetyAndPolicyHooks()
    {
        var context = new ClaudeRunContext
        {
            ProjectSlug = "fixture",
            WorkspacePath = "/workspace",
            AgentName = "programmer",
            SkillFile = "programmer/SKILL.md",
        };

        var arguments = CodexRunner.BuildArguments(
            context,
            sessionId: null,
            isResume: false,
            model: "gpt-5.6-terra",
            new Uri("http://127.0.0.1:1234/policy/abc"));

        Assert.Equal("exec", arguments[0]);
        Assert.Contains("workspace-write", arguments);
        Assert.Contains("approval_policy=\"never\"", arguments);
        Assert.Contains("sandbox_workspace_write.network_access=true", arguments);
        Assert.Contains("web_search=\"disabled\"", arguments);
        Assert.Contains(arguments, value => value.StartsWith("hooks.UserPromptSubmit=", StringComparison.Ordinal));
        Assert.Contains(arguments, value => value.StartsWith("hooks.PreToolUse=", StringComparison.Ordinal));
        Assert.Equal("-", arguments[^1]);
    }

    [Fact]
    public void ResumeArguments_TargetExactThreadWithoutNewRunOnlyFlags()
    {
        var context = new ClaudeRunContext
        {
            ProjectSlug = "fixture",
            WorkspacePath = "/workspace",
            AgentName = "programmer",
            SkillFile = "programmer/SKILL.md",
        };

        var arguments = CodexRunner.BuildArguments(
            context,
            "019fd288-363e-7093-92f5-cba942f8eb57",
            isResume: true,
            model: "gpt-5.6-sol",
            new Uri("http://127.0.0.1:1234/policy/abc"));

        Assert.Equal(
            ["exec", "resume", "019fd288-363e-7093-92f5-cba942f8eb57"],
            arguments.Take(3).ToArray());
        Assert.DoesNotContain("--sandbox", arguments);
        Assert.DoesNotContain("--cd", arguments);
    }

    [Fact]
    public void RealJsonlFixture_NormalizesAssistantThreadAndUsage()
    {
        var run = NewRun();
        var state = new CodexStreamState();
        string? threadId = null;
        var fixture = FindFixture("real-minimal.jsonl");

        foreach (var line in File.ReadLines(fixture))
            CodexStreamPump.ParseLine(line, run, state, id => threadId = id);

        Assert.Equal("019fd288-363e-7093-92f5-cba942f8eb57", threadId);
        Assert.Equal(1, state.AssistantEventCount);
        Assert.Equal(1, state.TerminalOutcome);
        Assert.Equal(14528, run.InputTokens);
        Assert.Equal(9984, run.CacheReadTokens);
        Assert.Equal(10, run.OutputTokens);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "assistant" && e.Text == "CODEX_HARNESS_OK");
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "result");
    }

    [Fact]
    public void FailedTurn_ForcesFailedTerminalOutcome()
    {
        var run = NewRun();
        var state = new CodexStreamState();

        CodexStreamPump.ParseLine(
            """{"type":"turn.failed","error":{"message":"usage limit reached"}}""",
            run,
            state,
            _ => { });

        Assert.Equal(-1, state.TerminalOutcome);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "error" && e.Text == "usage limit reached");
    }

    [Fact]
    public void TransientError_DoesNotOverrideLaterCompletedTurn()
    {
        var run = NewRun();
        var state = new CodexStreamState();

        CodexStreamPump.ParseLine(
            """{"type":"error","message":"retrying transport"}""",
            run,
            state,
            _ => { });
        CodexStreamPump.ParseLine(
            """{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}""",
            run,
            state,
            _ => { });

        Assert.Equal(1, state.TerminalOutcome);
    }

    private static AgentRun NewRun() => new()
    {
        RunId = "fixture",
        ProjectSlug = "fixture",
        TicketId = 1,
        AgentName = "programmer",
        SkillFile = "programmer/SKILL.md",
        ConcurrencyGroup = "programmer",
        StartedAt = DateTime.UtcNow,
        Backend = "codex",
    };

    private static string FindFixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "GigaClaw.Core.Tests", "Fixtures", "codex", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(name);
    }
}
