using GigaClaw.Core.Automation;
using GigaClaw.Core.Models;
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
                RequestedImageSource = ImageSourcePreference.Pexels,
            },
            "Write carefully.",
            isResume: false,
            CancellationToken.None);

        Assert.Contains("Focus on ticket #42: Review the gadget", prompt);
        Assert.Contains("Requested content type: Product Review (product-review).", prompt);
        Assert.Contains("Media contract: image source Pexels; video source None; media required before delivery: no.", prompt);
        Assert.Contains("Include a portable media brief in the ticket artifact", prompt);
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
                RequestedImageSource = ImageSourcePreference.Pexels,
            },
            "Ignored on automation resume.",
            isResume: true,
            CancellationToken.None);

        Assert.Contains("You have been re-dispatched on ticket #7: Finish the draft", prompt);
        Assert.Contains("Requested content type: Blog Post (blog-post).", prompt);
        Assert.Contains("Media contract: image source Pexels; video source None; media required before delivery: no.", prompt);
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
    public async Task PromptIncludesLocalGenerationFallbackWhenSelected()
    {
        using var tmp = new TempDir();
        var prompt = await ClaudeRunner.BuildPromptAsync(
            new ClaudeRunContext
            {
                ProjectSlug = "proj",
                WorkspacePath = tmp.Path,
                AgentName = "blog-writer",
                SkillFile = "(inline)",
                TicketId = 10,
                TicketTitle = "Need media",
                RequestedDeliverableType = "product-review",
                RequestedImageSource = ImageSourcePreference.LocalGeneration,
                RequestedVideoSource = VideoSourcePreference.OpenMontage,
            },
            "Write carefully.",
            isResume: false,
            CancellationToken.None);

        Assert.Contains("Media contract: image source ComfyUI local; video source OpenMontage local; media required before delivery: no.", prompt);
        Assert.Contains("If local generation is unavailable here, produce a portable prompt/upload handoff instead.", prompt);
        Assert.Contains("Never move the ticket to Blocked for local hardware availability.", prompt);
        Assert.Contains("When media is required, leave delivery incomplete until the handoff is fulfilled.", prompt);
    }

    [Fact]
    public async Task PromptIncludesPromptAndUploadContractAndRequiredFlag()
    {
        using var tmp = new TempDir();
        var prompt = await ClaudeRunner.BuildPromptAsync(
            new ClaudeRunContext
            {
                ProjectSlug = "proj",
                WorkspacePath = tmp.Path,
                AgentName = "lead-magnet-creator",
                SkillFile = "(inline)",
                TicketId = 11,
                TicketTitle = "Owner wants manual asset upload",
                RequestedDeliverableType = "lead-magnet",
                RequestedImageSource = ImageSourcePreference.PromptAndUpload,
                RequestedVideoSource = VideoSourcePreference.PromptAndUpload,
                RequireMediaBeforeDelivery = true,
            },
            "Write carefully.",
            isResume: false,
            CancellationToken.None);

        Assert.Contains("Media contract: image source prompt and upload; video source prompt and upload; media required before delivery: yes.", prompt);
        Assert.Contains("search terms or generation prompt", prompt);
        Assert.DoesNotContain("Never move the ticket to Blocked for local hardware availability", prompt);
    }

    [Fact]
    public void DeliverableLine_IsOmittedWhenUnset()
    {
        Assert.Null(ClaudeRunner.FormatRequestedDeliverableLine(null));
        Assert.Null(ClaudeRunner.FormatRequestedDeliverableLine("  "));
    }

    [Fact]
    public void MediaContract_IsOmittedWhenUnset()
    {
        Assert.Null(ClaudeRunner.FormatMediaContractBlock(
            ImageSourcePreference.None,
            VideoSourcePreference.None,
            requireMediaBeforeDelivery: false));
    }
}
