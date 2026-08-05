using Xunit;

namespace GigaClaw.Core.Tests.Web;

public sealed class AgentTemplateSyncUiTests
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
    public void AgentTemplatesSection_FollowsWorkspaceAndPrecedesFallbackSettings()
    {
        var source = Read("GigaClaw.Web/Components/Pages/ProjectSettings.razor");
        var workspace = source.IndexOf("@L[\"WorkspacePath\"]", StringComparison.Ordinal);
        var sync = source.IndexOf("class=\"settings-section agent-template-sync\"", StringComparison.Ordinal);
        var fallback = source.IndexOf("@L[\"FallbackModel\"]", StringComparison.Ordinal);

        Assert.True(workspace >= 0);
        Assert.True(sync > workspace);
        Assert.True(fallback > sync);
    }

    [Fact]
    public void AgentTemplatesUi_UsesPreviewAndCurrentPlanTokenForApply()
    {
        var source = Read("GigaClaw.Web/Components/Pages/ProjectSettings.razor");

        Assert.Contains("AgentTemplateSyncService", source);
        Assert.Contains("PreviewAsync(_workspacePath.Trim())", source);
        Assert.Contains("ApplyAsync(_workspacePath.Trim(), plan.PlanToken)", source);
        Assert.Contains("_agentTemplatePlan is { CanApply: true, HasApplicableChanges: true }", source);
        Assert.Contains("AgentTemplateSyncPlanChangedException", source);
        Assert.Contains("AgentTemplatesStalePreview", source);
    }

    [Fact]
    public void AgentTemplatesUi_RendersCountsAndExactConflictDetails()
    {
        var source = Read("GigaClaw.Web/Components/Pages/ProjectSettings.razor");

        foreach (var property in new[] { "Additions", "Updates", "Removals", "Conflicts", "DeletedByOwner", "SkippedMemory" })
            Assert.Contains($"_agentTemplatePlan.{property}", source);

        Assert.Contains("change.RelativePath", source);
        Assert.Contains("change.Detail", source);
        Assert.Contains("AgentTemplateSyncChangeKind.Conflict", source);
        Assert.Contains("AgentTemplateSyncChangeKind.ManualReviewRequired", source);
    }

    [Fact]
    public void AgentTemplatesUi_HasLocalizedLabelsAndResponsiveControls()
    {
        var localization = Read("GigaClaw.Core/Services/LocalizationService.cs");
        var css = Read("GigaClaw.Web/wwwroot/app.css");

        foreach (var key in new[]
        {
            "AgentTemplates", "CheckForUpdates", "ApplySafeUpdates", "AgentTemplatesAdditions",
            "AgentTemplatesUpdates", "AgentTemplatesRemovals", "AgentTemplatesRetainedCustomizations",
            "AgentTemplatesRetainedDeletions", "AgentTemplatesMemorySkipped", "AgentTemplatesStalePreview",
        })
            Assert.Contains($"[\"{key}\"]", localization);

        Assert.Contains(".agent-template-summary", css);
        Assert.Contains(".agent-template-conflicts", css);
        Assert.Contains("@media (max-width: 620px)", css);
        Assert.Contains(".agent-template-actions .settings-primary", css);
    }
}
