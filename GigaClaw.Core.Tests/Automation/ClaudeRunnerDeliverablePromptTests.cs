using GigaClaw.Core.Automation;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Automation;

public class ClaudeRunnerDeliverablePromptTests
{
    [Fact]
    public async Task FreshAutomationPrompt_UsesCanonicalDeliverableNameAndSlug()
    {
        using var tmp = new TempDir();
        var prompt = await ClaudeRunner.BuildPromptAsync(
            new ClaudeRunContext
            {
                ProjectSlug = "proj",
                WorkspacePath = tmp.Path,
                AgentName = "blog-writer",
                SkillFile = "(inline)",
                TicketId = 42,
                TicketTitle = "Review the gadget",
                RequestedDeliverableType = "product-review",
            },
            "Write carefully.",
            isResume: false,
            CancellationToken.None);

        Assert.Contains("Focus on ticket #42: Review the gadget", prompt);
        Assert.Contains("Requested content type: Product Review (product-review).", prompt);
    }

    [Fact]
    public async Task ResumeAutomationPrompt_UsesCanonicalDeliverableNameAndSlug()
    {
        using var tmp = new TempDir();
        var prompt = await ClaudeRunner.BuildPromptAsync(
            new ClaudeRunContext
            {
                ProjectSlug = "proj",
                WorkspacePath = tmp.Path,
                AgentName = "blog-writer",
                SkillFile = "(inline)",
                TicketId = 7,
                TicketTitle = "Finish the draft",
                RequestedDeliverableType = "blog-post",
            },
            "Ignored on automation resume.",
            isResume: true,
            CancellationToken.None);

        Assert.Contains("You have been re-dispatched on ticket #7: Finish the draft", prompt);
        Assert.Contains("Requested content type: Blog Post (blog-post).", prompt);
    }

    [Fact]
    public async Task PromptFallsBackToStoredDeliverableWhenCatalogDoesNotKnowIt()
    {
        using var tmp = new TempDir();
        var prompt = await ClaudeRunner.BuildPromptAsync(
            new ClaudeRunContext
            {
                ProjectSlug = "proj",
                WorkspacePath = tmp.Path,
                AgentName = "writer",
                SkillFile = "(inline)",
                TicketId = 9,
                TicketTitle = "Custom content",
                RequestedDeliverableType = "field-note-special",
            },
            "Write carefully.",
            isResume: false,
            CancellationToken.None);

        Assert.Contains("Requested content type: field-note-special.", prompt);
    }

    [Fact]
    public void DeliverableLine_IsOmittedWhenUnset()
    {
        Assert.Null(ClaudeRunner.FormatRequestedDeliverableLine(null));
        Assert.Null(ClaudeRunner.FormatRequestedDeliverableLine("  "));
    }
}
