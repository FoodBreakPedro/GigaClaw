using GigaClaw.Core.Models;

namespace GigaClaw.Core.Tests.Models;

public sealed class DeliverableCatalogTests
{
    [Fact]
    public void GetAll_ReturnsTheCanonicalDeliverablesWithCompleteDefinitions()
    {
        var definitions = DeliverableCatalog.GetAll();

        Assert.Equal(
            [
                "blog-post",
                "email-newsletter",
                "social-media-content",
                "product-review",
                "lead-magnet",
                "content-series",
            ],
            definitions.Select(definition => definition.Slug));
        Assert.All(definitions, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Name));
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
            Assert.False(string.IsNullOrWhiteSpace(definition.EntryAgent));
            Assert.False(string.IsNullOrWhiteSpace(definition.OutputCategory));
        });
        Assert.True(DeliverableCatalog.Validate(definitions).IsValid);
    }

    [Theory]
    [InlineData("blog-post", "blog-post")]
    [InlineData(" Blog Post ", "blog-post")]
    [InlineData("EMAIL_NEWSLETTER", "email-newsletter")]
    [InlineData("social   media-content", "social-media-content")]
    public void TryNormalizeSlug_NormalizesApiFriendlyInput(string value, string expected)
    {
        var normalized = DeliverableCatalog.TryNormalizeSlug(value, out var slug);

        Assert.True(normalized);
        Assert.Equal(expected, slug);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("blog/post")]
    [InlineData("blog.post")]
    public void TryNormalizeSlug_RejectsInvalidInput(string? value)
    {
        var normalized = DeliverableCatalog.TryNormalizeSlug(value, out var slug);

        Assert.False(normalized);
        Assert.Null(slug);
    }

    [Theory]
    [InlineData("Blog Post", "blog-writer")]
    [InlineData("email-newsletter", "email-copywriter")]
    [InlineData("social_media_content", "growth-writer")]
    [InlineData("product review", "blog-writer")]
    [InlineData("lead-magnet", "lead-magnet-creator")]
    [InlineData("content series", "content-series-planner")]
    public void TryGet_ResolvesKnownDeliverables(string value, string expectedEntryAgent)
    {
        var found = DeliverableCatalog.TryGet(value, out var definition);

        Assert.True(found);
        Assert.NotNull(definition);
        Assert.Equal(expectedEntryAgent, definition.EntryAgent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown-output")]
    [InlineData("blog/post")]
    public void TryGet_RejectsUnknownOrInvalidDeliverables(string? value)
    {
        var found = DeliverableCatalog.TryGet(value, out var definition);

        Assert.False(found);
        Assert.Null(definition);
    }

    [Fact]
    public void Validate_RejectsDuplicateInvalidAndIncompleteDefinitions()
    {
        var result = DeliverableCatalog.Validate(
        [
            new DeliverableDefinition("blog-post", "Blog Post", "Article", "blog-writer", "Article"),
            new DeliverableDefinition("blog-post", "", "", "", ""),
            new DeliverableDefinition("not/a-slug", "Bad", "Bad", "writer", "Article"),
        ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("duplicates slug", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("invalid slug", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("requires a name", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("requires a description", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("requires an entry agent", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("requires an output category", StringComparison.Ordinal));
    }
}
