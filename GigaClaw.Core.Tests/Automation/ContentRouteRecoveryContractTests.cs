using System.Text;
using System.Text.Json;
using GigaClaw.Core.Packs;

namespace GigaClaw.Core.Tests.Automation;

public sealed class ContentRouteRecoveryContractTests
{
    private static string Asset(string path) =>
        Encoding.UTF8.GetString(CorePack.Source().ReadAgentAsset(path));

    [Fact]
    public void Ticket_native_writer_emits_the_cms_taxonomy_contract()
    {
        var skill = Asset("content-writer/SKILL.md");

        Assert.Contains("categorySlug: <reviewer-validated CMS category slug>", skill);
        Assert.Contains("tags:\n  - <reviewer-validated-tag-slug>", skill);
        Assert.DoesNotContain("Never end your turn with the ticket in `InProgress`", skill);
        Assert.DoesNotContain("If you cannot finish, go to `Blocked`", skill);
        Assert.Contains("`content-writer-resume` retries this boundedly", skill);
        Assert.Contains("remain in `InProgress` for the bounded resume trigger", skill);
    }

    [Fact]
    public void Reviewer_uses_the_dispatch_field_name()
    {
        var skill = Asset("blog-reviewer/SKILL.md");

        Assert.Contains("frontmatter `categorySlug` and `tags`", skill);
        Assert.DoesNotContain("frontmatter `category` and `tags`", skill);
    }

    [Fact]
    public void Translator_digest_recovery_is_bounded_and_returns_to_seo_first()
    {
        var skill = Asset("blog-translator/SKILL.md");

        Assert.Contains("BLOG-TRANSLATION RETURN cycle 1/2", skill);
        Assert.Contains("BLOG-TRANSLATION RETURN cycle 2/2", skill);
        Assert.Contains("handoff --assignee blog-seo --status Todo", skill);
        Assert.Contains("Never start a third recovery loop", skill);
        Assert.Contains("specific question and enumerated options", skill);
        Assert.Contains("`groomer` in `Backlog`", skill);
    }

    [Fact]
    public void Content_and_translation_resume_triggers_are_bounded()
    {
        using var document = JsonDocument.Parse(Asset("automations.json"));
        var automations = document.RootElement.GetProperty("automations").EnumerateArray().ToArray();

        var content = Assert.Single(automations, item => item.GetProperty("id").GetString() == "content-writer-resume");
        Assert.Equal(5, content.GetProperty("trigger").GetProperty("maxConsecutiveFirings").GetInt32());
        Assert.Equal("Backlog", content.GetProperty("trigger").GetProperty("exhaustedStatus").GetString());

        var shared = Assert.Single(automations, item => item.GetProperty("id").GetString() == "assignee-resume");
        Assert.Equal(3, shared.GetProperty("trigger").GetProperty("maxConsecutiveFirings").GetInt32());
        Assert.Contains("blog-translator", shared.GetProperty("conditions")[0].GetProperty("slugs")
            .EnumerateArray().Select(item => item.GetString()));
    }
}
