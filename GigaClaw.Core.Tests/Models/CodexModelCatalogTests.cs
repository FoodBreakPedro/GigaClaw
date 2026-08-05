using GigaClaw.Core.Models;

namespace GigaClaw.Core.Tests.Models;

public sealed class CodexModelCatalogTests
{
    [Theory]
    [InlineData(null, "gpt-5.6-sol")]
    [InlineData("", "gpt-5.6-sol")]
    [InlineData("gpt-5.5", "gpt-5.5")]
    [InlineData("claude-haiku-4-5", "gpt-5.6-luna")]
    [InlineData("claude-sonnet-4-6", "gpt-5.6-terra")]
    [InlineData("claude-opus-4-6", "gpt-5.6-sol")]
    public void TryResolve_AcceptsSupportedModels(string? configured, string expected)
    {
        var valid = CodexModelCatalog.TryResolve(configured, out var resolved);

        Assert.True(valid);
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("qwen3-coder:30b")]
    [InlineData("codex-best")]
    [InlineData("claude-unknown-4")]
    public void TryResolve_RejectsUnsupportedModels(string configured)
    {
        var valid = CodexModelCatalog.TryResolve(configured, out var resolved);

        Assert.False(valid);
        Assert.Equal(configured, resolved);
    }
}
