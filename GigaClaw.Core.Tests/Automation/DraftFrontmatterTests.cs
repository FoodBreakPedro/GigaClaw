using GigaClaw.Core.Automation;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// Covers the AD-7 draft frontmatter parser: a hand-rolled, tolerant reader for the
/// <c>---</c>-fenced header + markdown body format a ticket description holds once a draft is
/// finished. No YAML package involved — see <see cref="DraftFrontmatter"/> remarks.
/// </summary>
public class DraftFrontmatterTests
{
    private const string FullExample = """
        ---
        title: How to Ship Faster
        slug: how-to-ship-faster
        excerpt: A short teaser.
        contentType: article
        seo:
          title: How to Ship Faster | ZabalaZone
          description: Practical tips for shipping faster.
          primaryKeyword: ship faster
        imagePrompt: a rocket launching from a laptop
        ---
        # How to Ship Faster

        Body content goes here, with **markdown**.
        """;

    [Fact]
    public void Parses_all_flat_and_nested_fields()
    {
        Assert.True(DraftFrontmatter.TryParse(FullExample, out var draft, out var error));
        Assert.Null(error);
        Assert.Equal("How to Ship Faster", draft!.Title);
        Assert.Equal("how-to-ship-faster", draft.Slug);
        Assert.Equal("A short teaser.", draft.Excerpt);
        Assert.Equal("article", draft.ContentType);
        Assert.Equal("a rocket launching from a laptop", draft.ImagePrompt);
        Assert.Equal("How to Ship Faster | ZabalaZone", draft.SeoTitle);
        Assert.Equal("Practical tips for shipping faster.", draft.SeoDescription);
        Assert.Equal("ship faster", draft.SeoPrimaryKeyword);
        Assert.StartsWith("# How to Ship Faster", draft.Body);
        Assert.Contains("Body content goes here, with **markdown**.", draft.Body);
    }

    [Fact]
    public void Body_is_everything_after_the_closing_fence()
    {
        const string description = """
            ---
            title: T
            ---
            Line one
            Line two
            """;
        Assert.True(DraftFrontmatter.TryParse(description, out var draft, out _));
        Assert.Equal("Line one\nLine two", draft!.Body);
    }

    [Fact]
    public void Missing_optional_fields_default_to_empty_string()
    {
        const string description = """
            ---
            title: Only A Title
            ---
            body
            """;
        Assert.True(DraftFrontmatter.TryParse(description, out var draft, out var error));
        Assert.Null(error);
        Assert.Equal("Only A Title", draft!.Title);
        Assert.Equal("", draft.Slug);
        Assert.Equal("", draft.Excerpt);
        Assert.Equal("", draft.ContentType);
        Assert.Equal("", draft.ImagePrompt);
        Assert.Equal("", draft.SeoTitle);
        Assert.Equal("", draft.SeoDescription);
        Assert.Equal("", draft.SeoPrimaryKeyword);
    }

    [Fact]
    public void Quoted_values_have_their_quotes_stripped()
    {
        const string description = """
            ---
            title: "Quoted Title"
            slug: 'quoted-slug'
            ---
            body
            """;
        Assert.True(DraftFrontmatter.TryParse(description, out var draft, out _));
        Assert.Equal("Quoted Title", draft!.Title);
        Assert.Equal("quoted-slug", draft.Slug);
    }

    [Fact]
    public void Tolerates_leading_blank_lines_before_the_opening_fence()
    {
        const string description = "\n\n---\ntitle: T\n---\nbody";
        Assert.True(DraftFrontmatter.TryParse(description, out var draft, out _));
        Assert.Equal("T", draft!.Title);
    }

    // ── Failure modes ──────────────────────────────────────────────────────

    [Fact]
    public void Fails_when_description_is_null_or_empty()
    {
        Assert.False(DraftFrontmatter.TryParse(null, out var draft1, out var error1));
        Assert.Null(draft1);
        Assert.NotNull(error1);

        Assert.False(DraftFrontmatter.TryParse("", out var draft2, out var error2));
        Assert.Null(draft2);
        Assert.NotNull(error2);
    }

    [Fact]
    public void Fails_when_opening_fence_is_missing()
    {
        const string description = "title: T\n---\nbody";
        Assert.False(DraftFrontmatter.TryParse(description, out var draft, out var error));
        Assert.Null(draft);
        Assert.Contains("opening", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fails_when_closing_fence_is_missing()
    {
        const string description = "---\ntitle: T\nno closing fence here";
        Assert.False(DraftFrontmatter.TryParse(description, out var draft, out var error));
        Assert.Null(draft);
        Assert.Contains("closing", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fails_when_title_is_missing()
    {
        const string description = "---\nslug: no-title\n---\nbody";
        Assert.False(DraftFrontmatter.TryParse(description, out var draft, out var error));
        Assert.Null(draft);
        Assert.Contains("title", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fails_when_title_is_blank()
    {
        const string description = "---\ntitle:   \n---\nbody";
        Assert.False(DraftFrontmatter.TryParse(description, out var draft, out var error));
        Assert.Null(draft);
    }

    // ── JSON-safe rendering ───────────────────────────────────────────────

    [Fact]
    public void ToJsonEscapedValues_escapes_quotes_backslashes_and_newlines()
    {
        const string description = """
            ---
            title: A "quoted" title with a \backslash\
            ---
            Line one
            Line two with "quotes" and a \ backslash
            """;
        Assert.True(DraftFrontmatter.TryParse(description, out var draft, out _));
        var values = draft!.ToJsonEscapedValues();

        // The escaped value must be safe to splice directly between a literal pair of quotes.
        var jsonBody = $$"""{"title":"{{values["draft.title"]}}","body":"{{values["draft.body"]}}"}""";
        using var doc = System.Text.Json.JsonDocument.Parse(jsonBody);
        Assert.Equal("""A "quoted" title with a \backslash\""", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("Line one\nLine two with \"quotes\" and a \\ backslash", doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public void ToJsonEscapedValues_maps_every_documented_placeholder_key()
    {
        Assert.True(DraftFrontmatter.TryParse(FullExample, out var draft, out _));
        var values = draft!.ToJsonEscapedValues();

        foreach (var key in new[]
        {
            "draft.title", "draft.slug", "draft.excerpt", "draft.contentType", "draft.imagePrompt",
            "draft.seo.title", "draft.seo.description", "draft.seo.primaryKeyword", "draft.body",
        })
        {
            Assert.True(values.ContainsKey(key), $"missing key {key}");
        }
    }
}
