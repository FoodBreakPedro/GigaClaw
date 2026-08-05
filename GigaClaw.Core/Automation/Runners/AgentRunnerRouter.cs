using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using Microsoft.Extensions.Logging;

namespace GigaClaw.Core.Automation.Runners;

/// <summary>Resolves the execution host without coupling run consumers to a provider.</summary>
public sealed class AgentRunnerRouter : IAgentRunner
{
    private const string HarnessOverrideVariable = "GIGACLAW_AGENT_HARNESS";

    private readonly ClaudeRunner _claude;
    private readonly CodexRunner _codex;
    private readonly MemberService _members;
    private readonly ILogger<AgentRunnerRouter> _logger;

    public AgentRunnerRouter(
        ClaudeRunner claude,
        CodexRunner codex,
        MemberService members,
        ILogger<AgentRunnerRouter> logger)
    {
        _claude = claude;
        _codex = codex;
        _members = members;
        _logger = logger;
    }

    public async Task<AgentRun> RunAsync(ClaudeRunContext ctx, CancellationToken ct)
    {
        var harness = ResolveOverride()
            ?? ctx.Harness
            ?? (await _members.GetMemberBySlugAsync(ctx.ProjectSlug, ctx.AgentName))?.Harness
            ?? AgentHarness.Claude;

        if (harness == AgentHarness.Codex)
        {
            _logger.LogInformation("Routing {Agent} to Codex", ctx.AgentName);
            return await _codex.RunAsync(ctx, ct);
        }

        return await _claude.RunAsync(ctx, ct);
    }

    internal static AgentHarness? ResolveOverride()
    {
        var configured = Environment.GetEnvironmentVariable(HarnessOverrideVariable);
        if (string.IsNullOrWhiteSpace(configured)) return null;
        return Enum.TryParse<AgentHarness>(configured, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }
}
