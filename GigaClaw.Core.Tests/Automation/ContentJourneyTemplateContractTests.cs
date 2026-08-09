using System.Text;
using GigaClaw.Core.Packs;

namespace GigaClaw.Core.Tests.Automation;

public sealed class ContentJourneyTemplateContractTests
{
    private static string Asset(string path) =>
        Encoding.UTF8.GetString(CorePack.Source().ReadAgentAsset(path));

    [Fact]
    public void BlogWriter_UsesRequestedTypeAsALoadBearingContract()
    {
        var skill = Asset("blog-writer/SKILL.md");

        Assert.Contains("Requested Content Type", skill, StringComparison.Ordinal);
        Assert.Contains("Product Review", skill, StringComparison.Ordinal);
        Assert.Contains("Never invent hands-on testing", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void BlogWriter_AlwaysEmitsAPortableImagePrompt()
    {
        var skill = Asset("blog-writer/SKILL.md");

        Assert.Contains("imagePrompt:", skill, StringComparison.Ordinal);
        Assert.Contains("Pexels", skill, StringComparison.Ordinal);
        Assert.Contains("local ComfyUI", skill, StringComparison.Ordinal);
        Assert.Contains("manual generation and upload", skill, StringComparison.Ordinal);
        Assert.Contains("non-blocking unless", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void BlogReviewer_EnforcesTheProductReviewEvidenceContract()
    {
        var skill = Asset("blog-reviewer/SKILL.md");
        var rubric = Asset("blog-reviewer/references/scoring-rubric-details.md");
        var productReview = Asset("blog-reviewer/references/product-review-contract.md");

        Assert.Contains("Product Review Contract", skill, StringComparison.Ordinal);
        Assert.Contains("references/product-review-contract.md", skill, StringComparison.Ordinal);
        Assert.Contains("evaluation method", productReview, StringComparison.Ordinal);
        Assert.Contains("hands-on testing", productReview, StringComparison.Ordinal);
        Assert.Contains("affiliate", rubric, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("P0 trust failure", rubric, StringComparison.Ordinal);
    }
}
