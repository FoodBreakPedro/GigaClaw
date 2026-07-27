using System.Text.Json;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Services;

public sealed class LocalMediaJobServiceTests
{
    [Fact]
    public async Task CreateAsync_PersistsQueuedJobAndDeduplicatesByIdempotencyKey()
    {
        using var tmp = new TempDir();
        var fixture = await BuildFixtureAsync(tmp);
        await WriteImageSpecAsync(fixture.Workspace, provider: "comfyui", approvalStatus: "approved");
        var request = new CreateLocalMediaJobRequest(
            fixture.TicketId,
            "image",
            "comfyui",
            "media/specs/image.json",
            "ticket-1-image-v1",
            "local-image-artist");

        var first = await fixture.Service.CreateAsync(fixture.Slug, request);
        var second = await fixture.Service.CreateAsync(fixture.Slug, request);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Job.Id, second.Job.Id);
        Assert.Equal(LocalMediaJobStatuses.Queued, first.Job.Status);
        Assert.Equal("gpu:comfyui", first.Job.ResourceClass);
        Assert.Equal(64, first.Job.ExecutionSpecSha256.Length);
        Assert.Single(await fixture.Service.ListAsync(fixture.Slug));
    }

    [Fact]
    public async Task CreateAsync_RejectsProviderAuto()
    {
        using var tmp = new TempDir();
        var fixture = await BuildFixtureAsync(tmp);
        await WriteImageSpecAsync(fixture.Workspace, provider: "auto", approvalStatus: "approved");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateAsync(
                fixture.Slug,
                new CreateLocalMediaJobRequest(
                    fixture.TicketId,
                    "image",
                    "auto",
                    "media/specs/image.json",
                    "ticket-1-image-auto",
                    "local-image-artist")));

        Assert.Contains("forbidden", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsUnapprovedExecutionSpec()
    {
        using var tmp = new TempDir();
        var fixture = await BuildFixtureAsync(tmp);
        await WriteImageSpecAsync(fixture.Workspace, provider: "comfyui", approvalStatus: "draft");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateAsync(
                fixture.Slug,
                new CreateLocalMediaJobRequest(
                    fixture.TicketId,
                    "image",
                    "comfyui",
                    "media/specs/image.json",
                    "ticket-1-image-draft",
                    "local-image-artist")));

        Assert.Contains("approved governance", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsClipWithoutOpenMontageContract()
    {
        using var tmp = new TempDir();
        var fixture = await BuildFixtureAsync(tmp);
        var specPath = Path.Combine(fixture.Workspace, "media", "specs", "clip.json");
        Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
        await File.WriteAllTextAsync(specPath, JsonSerializer.Serialize(new
        {
            version = 1,
            kind = "clip",
            provider = "phosphene",
            prompt = "A slow camera move",
            outputPath = "media/renders/clip.mp4",
            governance = new
            {
                approvalStatus = "approved",
                approvedBy = "owner",
                licenseNotes = "Owned source material",
                layer3SkillsRead = new[] { "phosphene-video" }
            }
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateAsync(
                fixture.Slug,
                new CreateLocalMediaJobRequest(
                    fixture.TicketId,
                    "clip",
                    "phosphene",
                    "media/specs/clip.json",
                    "ticket-1-clip-no-om",
                    "local-motion-artist")));

        Assert.Contains("OpenMontage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelAsync_AtomicallyCancelsQueuedJob()
    {
        using var tmp = new TempDir();
        var fixture = await BuildFixtureAsync(tmp);
        await WriteImageSpecAsync(fixture.Workspace, provider: "comfyui", approvalStatus: "approved");
        var created = await fixture.Service.CreateAsync(
            fixture.Slug,
            new CreateLocalMediaJobRequest(
                fixture.TicketId,
                "image",
                "comfyui",
                "media/specs/image.json",
                "ticket-1-image-cancel",
                "local-image-artist"));

        var cancelled = await fixture.Service.CancelAsync(
            fixture.Slug,
            created.Job.Id,
            "local-image-artist");

        Assert.Equal(LocalMediaJobStatuses.Cancelled, cancelled.Status);
        var ticket = await new TicketService(
            fixture.Projects,
            new MemberService(fixture.Projects)).GetTicketAsync(fixture.Slug, fixture.TicketId);
        Assert.NotNull(ticket);
        Assert.Equal("Todo", ticket.Status);
    }

    private static async Task<Fixture> BuildFixtureAsync(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("local-media-test");
        var workspace = Path.Combine(tmp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        await projects.UpdateProjectAsync(project.Slug, workspace);
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var ticket = await tickets.CreateTicketAsync(
            project.Slug,
            "Create local media",
            status: "Todo");
        var service = new LocalMediaJobService(
            projects,
            tickets,
            NullLogger<LocalMediaJobService>.Instance);
        return new Fixture(service, projects, project.Slug, workspace, ticket.Id);
    }

    private static async Task WriteImageSpecAsync(
        string workspace,
        string provider,
        string approvalStatus)
    {
        var specPath = Path.Combine(workspace, "media", "specs", "image.json");
        Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
        await File.WriteAllTextAsync(specPath, JsonSerializer.Serialize(new
        {
            version = 1,
            kind = "image",
            provider,
            prompt = "A geometric blue poster",
            outputPath = "media/renders/image.png",
            governance = new
            {
                approvalStatus,
                approvedBy = "owner",
                licenseNotes = "Original prompt",
                layer3SkillsRead = new[] { "comfyui-image" }
            }
        }));
    }

    private sealed record Fixture(
        LocalMediaJobService Service,
        ProjectService Projects,
        string Slug,
        string Workspace,
        int TicketId);
}
