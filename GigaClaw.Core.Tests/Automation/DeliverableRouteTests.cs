using GigaClaw.Core.Automation.Workflow;
using GigaClaw.Core.Models;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// The board's read-only view of a deliverable's declared route, resolved against the shipped
/// <c>ProjectTemplate/Agents/workflow.json</c> rather than a hand-built fixture — a route that only
/// works on a fixture would tell the owner nothing about the graph their workspace actually gets.
/// </summary>
public class DeliverableRouteTests
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

    private static WorkflowGraph Graph() =>
        WorkflowGraphFile.Read(Path.Combine(RepoRoot(), "ProjectTemplate", "Agents"))!;

    private static DeliverableDefinition Deliverable(string slug)
    {
        Assert.True(DeliverableCatalog.TryGet(slug, out var definition));
        return definition!;
    }

    [Fact]
    public void Blog_post_route_includes_the_reviewer_that_automation_derivation_would_drop()
    {
        // The regression this whole approach exists for: blog-reviewer-on-review uses runAgent and
        // never reassigns, so deriving the route from assignTicket edges silently omits it.
        var roles = DeliverableRoute.Resolve(Graph(), Deliverable("blog-post"))
            .Select(stage => stage.Role)
            .ToList();

        Assert.Equal(["blog-writer", "blog-reviewer", "blog-seo"], roles);
    }

    [Fact]
    public void Product_review_shares_the_blog_route()
    {
        var blog = DeliverableRoute.Resolve(Graph(), Deliverable("blog-post")).Select(s => s.Role);
        var review = DeliverableRoute.Resolve(Graph(), Deliverable("product-review")).Select(s => s.Role);
        Assert.Equal(blog, review);
    }

    [Theory]
    [InlineData("email-newsletter", "email-copywriter")]
    [InlineData("social-media-content", "growth-writer")]
    [InlineData("lead-magnet", "lead-magnet-creator")]
    public void Thin_deliverables_declare_their_entry_agent_then_owner_approval(string slug, string entryAgent)
    {
        var roles = DeliverableRoute.Resolve(Graph(), Deliverable(slug)).Select(s => s.Role).ToList();
        Assert.Equal([entryAgent, "approval-gatekeeper"], roles);
    }

    [Fact]
    public void Content_series_declares_the_single_stage_it_actually_has()
    {
        var roles = DeliverableRoute.Resolve(Graph(), Deliverable("content-series")).Select(s => s.Role);
        Assert.Equal(["content-series-planner"], roles);
    }

    [Fact]
    public void Every_catalog_deliverable_resolves_to_at_least_its_entry_agent()
    {
        var graph = Graph();
        foreach (var deliverable in DeliverableCatalog.GetAll())
        {
            var stages = DeliverableRoute.Resolve(graph, deliverable);
            Assert.True(stages.Count > 0, $"'{deliverable.Slug}' resolved to no stages.");
            Assert.Equal(deliverable.EntryAgent, stages[0].Role);
        }
    }

    [Fact]
    public void Progress_places_the_ticket_on_the_stage_its_assignee_works()
    {
        var progress = DeliverableRoute.Locate(Graph(), Deliverable("blog-post"), "blog-reviewer");
        Assert.True(progress.IsOnRoute);
        Assert.Equal(1, progress.CurrentIndex);
    }

    [Fact]
    public void An_assignee_outside_the_route_is_reported_off_route_not_as_stage_one()
    {
        // The groomer recovery hop and an owner-assigned specialist both land here. Rendering either
        // as "stage 1 of 3" would tell the owner the ticket restarted when it did not.
        var progress = DeliverableRoute.Locate(Graph(), Deliverable("blog-post"), "groomer");
        Assert.False(progress.IsOnRoute);
        Assert.Equal(3, progress.Stages.Count);
    }

    [Fact]
    public void An_unassigned_ticket_is_off_route_but_still_shows_its_stages()
    {
        var progress = DeliverableRoute.Locate(Graph(), Deliverable("blog-post"), null);
        Assert.False(progress.IsOnRoute);
        Assert.Equal(3, progress.Stages.Count);
    }

    [Fact]
    public void A_workspace_with_no_graph_resolves_to_no_stages_rather_than_throwing()
    {
        Assert.Empty(DeliverableRoute.Resolve(null, Deliverable("blog-post")));
        Assert.False(DeliverableRoute.Locate(null, Deliverable("blog-post"), "blog-writer").IsOnRoute);
    }
}
