using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

public sealed class TicketLabelTests
{
    private static (TicketService tickets, LabelService labels, string slug) BuildSut(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = projects.CreateProjectAsync("label-test").GetAwaiter().GetResult();
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var labels = new LabelService(projects);
        return (tickets, labels, project.Slug);
    }

    [Fact]
    public async Task SetTicketLabels_RemovesLabel_WhenCalledWithSubset()
    {
        using var tmp = new TempDir();
        var (svc, lblSvc, slug) = BuildSut(tmp);

        var label1 = await lblSvc.CreateLabelAsync(slug, "bug", "#ff0000");
        var label2 = await lblSvc.CreateLabelAsync(slug, "feature", "#00ff00");
        var ticket = await svc.CreateTicketAsync(slug, "T1", labelIds: [label1.Id, label2.Id]);

        var ok = await svc.SetTicketLabelsAsync(slug, ticket.Id, [label1.Id]);
        Assert.True(ok);

        var refreshed = await svc.GetTicketAsync(slug, ticket.Id);
        Assert.NotNull(refreshed);
        Assert.Single(refreshed.Labels);
        Assert.Equal(label1.Id, refreshed.Labels[0].Id);
    }

    [Fact]
    public async Task SetTicketLabels_RemovesAllLabels_WhenCalledWithEmptyList()
    {
        using var tmp = new TempDir();
        var (svc, lblSvc, slug) = BuildSut(tmp);

        var label = await lblSvc.CreateLabelAsync(slug, "bug", "#ff0000");
        var ticket = await svc.CreateTicketAsync(slug, "T1", labelIds: [label.Id]);

        var ok = await svc.SetTicketLabelsAsync(slug, ticket.Id, []);
        Assert.True(ok);

        var refreshed = await svc.GetTicketAsync(slug, ticket.Id);
        Assert.NotNull(refreshed);
        Assert.Empty(refreshed.Labels);
    }

    [Fact]
    public async Task PatchTicketLabels_AddsAndRemovesWithoutDroppingUnrelatedLabels()
    {
        using var tmp = new TempDir();
        var (svc, lblSvc, slug) = BuildSut(tmp);

        var keep = await lblSvc.CreateLabelAsync(slug, "keep", "#111111");
        var remove = await lblSvc.CreateLabelAsync(slug, "remove", "#222222");
        var add = await lblSvc.CreateLabelAsync(slug, "add", "#333333");
        var ticket = await svc.CreateTicketAsync(slug, "T1", labelIds: [keep.Id, remove.Id]);

        var labels = await svc.PatchTicketLabelsAsync(
            slug,
            ticket.Id,
            addLabelIds: [add.Id],
            removeLabelIds: [remove.Id],
            author: "approval-gatekeeper");

        Assert.NotNull(labels);
        Assert.Equal(["add", "keep"], labels.Select(label => label.Name).OrderBy(name => name));
    }

    [Fact]
    public async Task PatchTicketLabels_RequiresAuthor()
    {
        using var tmp = new TempDir();
        var (svc, _, slug) = BuildSut(tmp);
        var ticket = await svc.CreateTicketAsync(slug, "T1");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PatchTicketLabelsAsync(slug, ticket.Id, [], [], ""));
    }

    [Fact]
    public async Task PatchTicketLabels_RejectsUnknownAddWithoutPartialRemoval()
    {
        using var tmp = new TempDir();
        var (svc, lblSvc, slug) = BuildSut(tmp);
        var keep = await lblSvc.CreateLabelAsync(slug, "keep", "#111111");
        var ticket = await svc.CreateTicketAsync(slug, "T1", labelIds: [keep.Id]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PatchTicketLabelsAsync(
                slug, ticket.Id, addLabelIds: [int.MaxValue],
                removeLabelIds: [keep.Id], author: "approval-gatekeeper"));

        var unchanged = await svc.GetTicketAsync(slug, ticket.Id);
        Assert.NotNull(unchanged);
        Assert.Contains(unchanged.Labels, label => label.Id == keep.Id);
    }
}
