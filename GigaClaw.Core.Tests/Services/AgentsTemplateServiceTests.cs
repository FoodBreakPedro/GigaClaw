using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// Covers AD-9 model seeding: <see cref="AgentsTemplateService.DefaultModels"/> reads the
/// embedded <c>ProjectTemplate/Agents/models.json</c> map, and
/// <see cref="AgentsTemplateService.EnsureAgentMembersAsync"/> is the single shared "create any
/// missing agent member, seeded with its AD-9 default model" step used by both Home.razor's
/// project-creation flow and <c>POST /api/projects/{slug}/initialize</c>.
/// </summary>
public sealed class AgentsTemplateServiceTests
{
    [Fact]
    public void DefaultModels_ReturnsRealClaudeModelIdsFromTheAD9Table()
    {
        var template = new AgentsTemplateService();
        var models = template.DefaultModels();

        // Haiku tier (mechanical, high-volume, low-judgment).
        Assert.Equal("claude-haiku-4-5", models["committer"]);
        Assert.Equal("claude-haiku-4-5", models["groomer"]);
        Assert.Equal("claude-haiku-4-5", models["documentalist"]);

        // Sonnet tier (the bulk of the work).
        Assert.Equal("claude-sonnet-4-6", models["blog-researcher"]);
        Assert.Equal("claude-sonnet-4-6", models["growth-writer"]);
        Assert.Equal("claude-sonnet-4-6", models["qa-tester"]);
        Assert.Equal("claude-sonnet-4-6", models["content-writer"]);

        // Opus tier (judgment gates the pipeline).
        Assert.Equal("claude-opus-4-8", models["blog-reviewer"]);
        Assert.Equal("claude-opus-4-8", models["decision-engine"]);
        Assert.Equal("claude-opus-4-8", models["approval-gatekeeper"]);
        Assert.Equal("claude-opus-4-8", models["evaluator"]);

        // Every seeded id must be one GigaClaw actually offers (ClaudeModelCatalog is the source
        // of truth the model selectors use — a typo here would silently seed an unusable model).
        foreach (var (slug, model) in models)
        {
            Assert.Contains(model, GigaClaw.Core.Models.ClaudeModelCatalog.Models);
        }

        // The models.json "_comment" documentation key must never surface as a pseudo-agent slug.
        Assert.DoesNotContain("_comment", models.Keys);
    }

    [Fact]
    public async Task EnsureAgentMembersAsync_SeedsDefaultModelForKnownAgentsAndLeavesOthersNull()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("model-seed-test");
        var members = new MemberService(projects);
        var template = new AgentsTemplateService();

        var created = await template.EnsureAgentMembersAsync(project.Slug, members);

        Assert.Contains("content-writer", created);
        Assert.Contains("blog-reviewer", created);

        var writer = await members.GetMemberBySlugAsync(project.Slug, "content-writer");
        Assert.NotNull(writer);
        Assert.Equal("claude-sonnet-4-6", writer!.DefaultModel);

        var reviewer = await members.GetMemberBySlugAsync(project.Slug, "blog-reviewer");
        Assert.NotNull(reviewer);
        Assert.Equal("claude-opus-4-8", reviewer!.DefaultModel);

        // An agent with no entry in models.json (e.g. producer) gets no explicit DefaultModel —
        // it falls back to the project's FallbackModel per AD-9's three-level resolution, it is
        // not silently defaulted to some hardcoded value here.
        var producer = await members.GetMemberBySlugAsync(project.Slug, "producer");
        Assert.NotNull(producer);
        Assert.Null(producer!.DefaultModel);
    }

    [Fact]
    public async Task EnsureAgentMembersAsync_IsIdempotent_DoesNotRecreateOrOverwriteExistingMembers()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("model-seed-idempotent-test");
        var members = new MemberService(projects);
        var template = new AgentsTemplateService();

        var first = await template.EnsureAgentMembersAsync(project.Slug, members);
        Assert.NotEmpty(first);

        // Simulate an operator override after the first seed.
        var writer = await members.GetMemberBySlugAsync(project.Slug, "content-writer");
        Assert.NotNull(writer);
        await members.UpdateMemberAsync(project.Slug, writer!.Id, defaultModel: "claude-fable-5");

        var second = await template.EnsureAgentMembersAsync(project.Slug, members);
        Assert.Empty(second);

        var writerAfter = await members.GetMemberBySlugAsync(project.Slug, "content-writer");
        Assert.Equal("claude-fable-5", writerAfter!.DefaultModel);
    }
}
