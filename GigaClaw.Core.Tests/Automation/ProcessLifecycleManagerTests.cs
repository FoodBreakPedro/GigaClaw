using GigaClaw.Core.Automation;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// R5: proves the plumbing between <see cref="ClaudeRunContext.ExecutionPath"/> and the claude
/// subprocess's actual working directory — the piece that makes worktree isolation more than a
/// recorded path. Exercised directly (no subprocess, no mock CLI needed) since
/// <see cref="ProcessLifecycleManager.BuildProcessStartInfo"/> only builds a <c>ProcessStartInfo</c>.
/// </summary>
public class ProcessLifecycleManagerTests
{
    private static ClaudeRunContext MakeContext(string workspacePath, string? executionPath) => new()
    {
        ProjectSlug = "demo",
        WorkspacePath = workspacePath,
        ExecutionPath = executionPath,
        AgentName = "programmer",
        SkillFile = "programmer/SKILL.md",
    };

    [Fact]
    public void ExecutionPath_when_set_overrides_the_process_working_directory()
    {
        var ctx = MakeContext(workspacePath: "/workspace/demo", executionPath: "/workspace/demo.worktrees/ticket-7");

        var psi = ProcessLifecycleManager.BuildProcessStartInfo(ctx, new List<string>());

        Assert.Equal("/workspace/demo.worktrees/ticket-7", psi.WorkingDirectory);
    }

    [Fact]
    public void ExecutionPath_when_null_falls_back_to_WorkspacePath_the_pre_R5_default()
    {
        var ctx = MakeContext(workspacePath: "/workspace/demo", executionPath: null);

        var psi = ProcessLifecycleManager.BuildProcessStartInfo(ctx, new List<string>());

        Assert.Equal("/workspace/demo", psi.WorkingDirectory);
    }
}
