using System.IO;
using Xunit;

namespace GigaClaw.Core.Tests.Web;

public class ChatComposerResponsivenessTests
{
    private static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "GigaClaw.sln"))
                               && !File.Exists(Path.Combine(dir, "GigaClaw.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    [Fact]
    public void ChatTextarea_IsBrowserOwnedInsteadOfServerControlledPerKey()
    {
        var razor = Read("GigaClaw.Web/Components/ClaudeChatDrawer.razor");
        Assert.DoesNotContain("value=\"@_inputText\"", razor);
        Assert.DoesNotContain("@oninput=\"OnInput\"", razor);
        Assert.DoesNotContain("private void OnInput", razor);
        Assert.Contains("chatDrawerComposer.install", razor);
        Assert.Contains("SubmitComposerFromJs", razor);
    }

    [Fact]
    public void ChatComposerJs_SubmitsOnlyCompletedValues()
    {
        var js = Read("GigaClaw.Web/wwwroot/js/chat-drawer.js");
        Assert.Contains("window.chatDrawerComposer", js);
        Assert.Contains("SubmitComposerFromJs", js);
        Assert.Contains("takeValue", js);
        Assert.Contains("e.isComposing", js);
    }

    [Fact]
    public void ChatDrawer_CachesRenderedMarkdownForCompletedMessages()
    {
        var razor = Read("GigaClaw.Web/Components/ClaudeChatDrawer.razor");
        Assert.Contains("RenderedHtml", razor);
        Assert.Contains("msg.RenderedHtml", razor);
    }

    [Fact]
    public void AgentSse_BatchesStreamingTextDeltas()
    {
        var js = Read("GigaClaw.Web/wwwroot/js/agent-sse.js");
        Assert.Contains("_pendingDelta", js);
        Assert.Contains("content_block_delta", js);
        Assert.Contains("setTimeout", js);
        Assert.Contains("_flushPendingDelta", js);
    }
}
