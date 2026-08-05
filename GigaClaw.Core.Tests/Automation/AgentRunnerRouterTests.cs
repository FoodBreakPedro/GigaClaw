using GigaClaw.Core.Automation.Runners;

namespace GigaClaw.Core.Tests.Automation;

public sealed class AgentRunnerRouterTests
{
    [Theory]
    [InlineData("codex", AgentHarness.Codex)]
    [InlineData("CODEX", AgentHarness.Codex)]
    [InlineData("claude", AgentHarness.Claude)]
    public void EnvironmentOverride_IsCaseInsensitive(string configured, AgentHarness expected)
    {
        var previous = Environment.GetEnvironmentVariable("GIGACLAW_AGENT_HARNESS");
        Environment.SetEnvironmentVariable("GIGACLAW_AGENT_HARNESS", configured);
        try { Assert.Equal(expected, AgentRunnerRouter.ResolveOverride()); }
        finally { Environment.SetEnvironmentVariable("GIGACLAW_AGENT_HARNESS", previous); }
    }

    [Fact]
    public void UnknownEnvironmentOverride_IsIgnored()
    {
        var previous = Environment.GetEnvironmentVariable("GIGACLAW_AGENT_HARNESS");
        Environment.SetEnvironmentVariable("GIGACLAW_AGENT_HARNESS", "other");
        try { Assert.Null(AgentRunnerRouter.ResolveOverride()); }
        finally { Environment.SetEnvironmentVariable("GIGACLAW_AGENT_HARNESS", previous); }
    }
}
