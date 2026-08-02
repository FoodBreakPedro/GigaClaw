using System.Text.Json;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Data.Sqlite;
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

    [Fact]
    public async Task UpdateStageAsync_PersistsStageOnRunningJob()
    {
        using var tmp = new TempDir();
        var fixture = await BuildFixtureAsync(tmp);
        var job = await CreateQueuedJobAsync(fixture, "ticket-1-stage-persist");
        await MarkRunningAsync(fixture, job.Id);

        var updated = await fixture.Service.UpdateStageAsync(
            fixture.Slug,
            job.Id,
            new UpdateLocalMediaJobStageRequest("rendering", 1, 3, "local-media-compositor"));

        Assert.Equal("rendering", updated.Stage);
        Assert.Equal(1, updated.StageIndex);
        Assert.Equal(3, updated.StageCount);
    }

    [Fact]
    public async Task UpdateStageAsync_IsIdempotentForTheSameStageIndex()
    {
        using var tmp = new TempDir();
        var fixture = await BuildFixtureAsync(tmp);
        var job = await CreateQueuedJobAsync(fixture, "ticket-1-stage-idempotent");
        await MarkRunningAsync(fixture, job.Id);
        var request = new UpdateLocalMediaJobStageRequest("compositing", 2, 4, "local-media-compositor");

        await fixture.Service.UpdateStageAsync(fixture.Slug, job.Id, request);
        var replayed = await fixture.Service.UpdateStageAsync(fixture.Slug, job.Id, request);

        Assert.Equal(2, replayed.StageIndex);
        Assert.Equal(4, replayed.StageCount);
    }

    [Fact]
    public async Task UpdateStageAsync_RejectsAStageIndexRegression()
    {
        using var tmp = new TempDir();
        var fixture = await BuildFixtureAsync(tmp);
        var job = await CreateQueuedJobAsync(fixture, "ticket-1-stage-regress");
        await MarkRunningAsync(fixture, job.Id);
        await fixture.Service.UpdateStageAsync(
            fixture.Slug,
            job.Id,
            new UpdateLocalMediaJobStageRequest("render", 2, 4, "local-media-compositor"));

        var exception = await Assert.ThrowsAsync<LocalMediaConflictException>(() =>
            fixture.Service.UpdateStageAsync(
                fixture.Slug,
                job.Id,
                new UpdateLocalMediaJobStageRequest("retry-setup", 1, 4, "local-media-compositor")));

        Assert.Contains("regress", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateStageAsync_ConcurrentReportsCannotRewindProgress()
    {
        using var tmp = new TempDir();
        var fixture = await BuildFixtureAsync(tmp);
        var job = await CreateQueuedJobAsync(fixture, "ticket-1-stage-race");
        await MarkRunningAsync(fixture, job.Id);

        // Two checkpoint reports in flight at once, both reading the same StageIndex before either
        // writes. A read-compare-then-write guard lets the slower, lower report land last and rewind
        // the progress the UI already showed; the guard has to be the UPDATE's own predicate.
        var ahead = fixture.Service.UpdateStageAsync(
            fixture.Slug, job.Id,
            new UpdateLocalMediaJobStageRequest("encoding", 3, 4, "local-media-compositor"));
        var behind = fixture.Service.UpdateStageAsync(
            fixture.Slug, job.Id,
            new UpdateLocalMediaJobStageRequest("sampling", 1, 4, "local-media-compositor"));

        // Which one is rejected depends on the interleaving; that the end state never regresses does
        // not, and that is the whole claim.
        await Task.WhenAll(IgnoringConflict(ahead), IgnoringConflict(behind));

        var final = await fixture.Service.GetAsync(fixture.Slug, job.Id);
        Assert.NotNull(final);
        Assert.Equal(3, final!.StageIndex);
        Assert.Equal("encoding", final.Stage);
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(1, 0)]
    [InlineData(5, 4)]
    public async Task UpdateStageAsync_RejectsAnOutOfRangeStageReport(int stageIndex, int stageCount)
    {
        using var tmp = new TempDir();
        var fixture = await BuildFixtureAsync(tmp);
        var job = await CreateQueuedJobAsync(fixture, $"ticket-1-stage-range-{stageIndex}-{stageCount}");
        await MarkRunningAsync(fixture, job.Id);

        await Assert.ThrowsAsync<LocalMediaValidationException>(() =>
            fixture.Service.UpdateStageAsync(
                fixture.Slug, job.Id,
                new UpdateLocalMediaJobStageRequest("render", stageIndex, stageCount, "local-media-compositor")));
    }

    [Fact]
    public async Task UpdateStageAsync_RejectsAnOverlongStageName()
    {
        using var tmp = new TempDir();
        var fixture = await BuildFixtureAsync(tmp);
        var job = await CreateQueuedJobAsync(fixture, "ticket-1-stage-too-long");
        await MarkRunningAsync(fixture, job.Id);

        await Assert.ThrowsAsync<LocalMediaValidationException>(() =>
            fixture.Service.UpdateStageAsync(
                fixture.Slug, job.Id,
                new UpdateLocalMediaJobStageRequest(new string('x', 121), 1, 4, "local-media-compositor")));
    }

    [Fact]
    public async Task UpdateStageAsync_DistinguishesAMissingJobFromABadState()
    {
        using var tmp = new TempDir();
        var fixture = await BuildFixtureAsync(tmp);

        // Not-found is its own outcome, not a conflict — the endpoint answers 404 for one and 409
        // for the other, and it can only do that if the service says which happened.
        await Assert.ThrowsAsync<LocalMediaNotFoundException>(() =>
            fixture.Service.UpdateStageAsync(
                fixture.Slug, "media-does-not-exist",
                new UpdateLocalMediaJobStageRequest("render", 1, 4, "local-media-compositor")));
    }

    private static async Task IgnoringConflict(Task<LocalMediaJob> task)
    {
        try { await task; }
        catch (LocalMediaConflictException) { /* the losing report is supposed to be rejected */ }
    }

    [Fact]
    public async Task UpdateStageAsync_RejectsUpdatesOnANonRunningJob()
    {
        using var tmp = new TempDir();
        var fixture = await BuildFixtureAsync(tmp);
        var job = await CreateQueuedJobAsync(fixture, "ticket-1-stage-not-running");

        var exception = await Assert.ThrowsAsync<LocalMediaConflictException>(() =>
            fixture.Service.UpdateStageAsync(
                fixture.Slug,
                job.Id,
                new UpdateLocalMediaJobStageRequest("render", 0, 4, "local-media-compositor")));

        Assert.Contains("running", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<LocalMediaJob> CreateQueuedJobAsync(Fixture fixture, string idempotencyKey)
    {
        await WriteImageSpecAsync(fixture.Workspace, provider: "comfyui", approvalStatus: "approved");
        var created = await fixture.Service.CreateAsync(
            fixture.Slug,
            new CreateLocalMediaJobRequest(
                fixture.TicketId,
                "image",
                "comfyui",
                "media/specs/image.json",
                idempotencyKey,
                "local-image-artist"));
        return created.Job;
    }

    /// <summary>
    /// Test-only shortcut that mirrors the durable worker's ClaimAsync UPDATE (Queued -> Running)
    /// via a raw connection, since the service's own claim path is private and the background pump
    /// is not started in these unit tests.
    /// </summary>
    private static async Task MarkRunningAsync(Fixture fixture, string jobId)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={fixture.Projects.GetProjectDbPath(fixture.Slug)}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MediaJobs SET Status = 'running' WHERE Id = $id";
        command.Parameters.AddWithValue("$id", jobId);
        await command.ExecuteNonQueryAsync();
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
