using Microsoft.Data.Sqlite;
using GigaClaw.Core.Data;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

public sealed class TicketDeliverableTypePersistenceTests
{
    [Theory]
    [InlineData("blog-post", ImageSourcePreference.Pexels)]
    [InlineData("product-review", ImageSourcePreference.Pexels)]
    [InlineData("lead-magnet", ImageSourcePreference.Pexels)]
    [InlineData("social-media-content", ImageSourcePreference.Pexels)]
    [InlineData("email-newsletter", ImageSourcePreference.None)]
    [InlineData("content-series", ImageSourcePreference.None)]
    [InlineData(null, ImageSourcePreference.None)]
    public void DeliverableCatalog_UsesConservativeImageDefaults(string? deliverableType, ImageSourcePreference expected)
    {
        Assert.Equal(expected, DeliverableCatalog.DefaultImageSource(deliverableType));
        Assert.Equal(VideoSourcePreference.None, DeliverableCatalog.DefaultVideoSource(deliverableType));
    }

    [Fact]
    public async Task CreateAndUpdateMediaPreferences_PreserveCustomChoicesAndRederiveUncustomizedDefaults()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("media-preferences");
        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "blog-writer");
        await members.CreateMemberAsync(project.Slug, "email-copywriter");
        var tickets = new TicketService(projects, members);

        var created = await tickets.CreateTicketAsync(project.Slug, "Article", deliverableType: "Email Newsletter");
        Assert.Equal(ImageSourcePreference.None, created.ImageSource);
        Assert.Equal(VideoSourcePreference.None, created.VideoSource);
        Assert.False(created.RequireMediaBeforeDelivery);

        var rederived = await tickets.UpdateTicketAsync(
            project.Slug, created.Id, author: "owner", deliverableType: "Blog Post");
        Assert.Equal(ImageSourcePreference.Pexels, rederived!.ImageSource);

        var customized = await tickets.UpdateTicketAsync(
            project.Slug, created.Id, author: "owner",
            imageSource: ImageSourcePreference.LocalGeneration,
            requireMediaBeforeDelivery: true);
        Assert.Equal(ImageSourcePreference.LocalGeneration, customized!.ImageSource);
        Assert.True(customized.RequireMediaBeforeDelivery);

        var preserved = await tickets.UpdateTicketAsync(
            project.Slug, created.Id, author: "owner", deliverableType: "Email Newsletter");
        Assert.Equal(ImageSourcePreference.LocalGeneration, preserved!.ImageSource);
        Assert.True(preserved.RequireMediaBeforeDelivery);
    }

    [Fact]
    public async Task RequireMediaWithoutAnySource_IsRejectedOnCreateAndUpdate()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("media-validation");
        var tickets = new TicketService(projects, new MemberService(projects));

        var createError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tickets.CreateTicketAsync(project.Slug, "Invalid", requireMediaBeforeDelivery: true));
        Assert.Contains("both imageSource and videoSource are None", createError.Message, StringComparison.Ordinal);

        var ticket = await tickets.CreateTicketAsync(project.Slug, "Valid");
        var updateError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tickets.UpdateTicketAsync(project.Slug, ticket.Id, author: "owner", requireMediaBeforeDelivery: true));
        Assert.Contains("both imageSource and videoSource are None", updateError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUpdateAndListTickets_PersistDeliverableTypeAndAllowClearing()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("deliverable-type");
        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "email-copywriter");
        var tickets = new TicketService(projects, members);

        var created = await tickets.CreateTicketAsync(
            project.Slug,
            "Launch newsletter",
            deliverableType: "Email Newsletter");

        Assert.Equal("email-newsletter", created.DeliverableType);
        Assert.Equal("email-copywriter", created.AssignedTo);

        var detail = await tickets.GetTicketAsync(project.Slug, created.Id);
        Assert.NotNull(detail);
        Assert.Equal("email-newsletter", detail.DeliverableType);

        var summaries = await tickets.ListTicketsAsync(project.Slug);
        var summary = Assert.Single(summaries);
        Assert.Equal("email-newsletter", summary.DeliverableType);

        var updated = await tickets.UpdateTicketAsync(
            project.Slug,
            created.Id,
            author: "owner",
            deliverableType: "");

        Assert.NotNull(updated);
        Assert.Null(updated.DeliverableType);
        Assert.Null((await tickets.GetTicketAsync(project.Slug, created.Id))!.DeliverableType);
    }

    [Fact]
    public async Task LegacyTicketDatabase_IsMigratedIdempotentlyForDeliverableType()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("legacy-deliverable");
        var dbPath = projects.GetProjectDbPath(project.Slug);
        SqliteConnection.ClearAllPools();
        File.Delete(dbPath);
        MigrationGate.Invalidate(dbPath);

        await using (var connection = new SqliteConnection($"Data Source={dbPath};Foreign Keys=True"))
        {
            await connection.OpenAsync();

            var schema = connection.CreateCommand();
            schema.CommandText = """
                CREATE TABLE Tickets (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    Description TEXT NOT NULL DEFAULT '',
                    Status TEXT NOT NULL DEFAULT 'Backlog',
                    Priority INTEGER NOT NULL DEFAULT 1,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    CreatedBy TEXT NOT NULL DEFAULT 'owner',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE TABLE Comments (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    TicketId INTEGER NOT NULL,
                    Content TEXT NOT NULL,
                    Author TEXT NOT NULL DEFAULT 'owner',
                    CreatedAt TEXT NOT NULL
                );
                CREATE TABLE ActivityEntries (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    TicketId INTEGER NOT NULL,
                    Author TEXT NOT NULL,
                    Text TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );
                """;
            await schema.ExecuteNonQueryAsync();
        }

        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "email-copywriter");
        var tickets = new TicketService(projects, members);
        var created = await tickets.CreateTicketAsync(
            project.Slug,
            "Backfill me",
            deliverableType: "email-newsletter");

        Assert.Equal("email-newsletter", created.DeliverableType);
        Assert.Equal("email-newsletter", (await tickets.GetTicketAsync(project.Slug, created.Id))!.DeliverableType);
        Assert.Equal("email-newsletter", Assert.Single(await tickets.ListTicketsAsync(project.Slug)).DeliverableType);

        await using var verify = new SqliteConnection($"Data Source={dbPath};Foreign Keys=True");
        await verify.OpenAsync();
        var pragma = verify.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(Tickets)";
        await using var reader = await pragma.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        Assert.Contains("DeliverableType", columns);
    }

    [Fact]
    public async Task LegacyTicketDatabase_BackfillsDefaultImagePreferenceFromDeliverable()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("legacy-media");
        var dbPath = projects.GetProjectDbPath(project.Slug);
        SqliteConnection.ClearAllPools();
        File.Delete(dbPath);
        MigrationGate.Invalidate(dbPath);

        await using (var connection = new SqliteConnection($"Data Source={dbPath};Foreign Keys=True"))
        {
            await connection.OpenAsync();
            var schema = connection.CreateCommand();
            schema.CommandText = """
                CREATE TABLE Tickets (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    Description TEXT NOT NULL DEFAULT '',
                    Status TEXT NOT NULL DEFAULT 'Backlog',
                    Priority INTEGER NOT NULL DEFAULT 1,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    CreatedBy TEXT NOT NULL DEFAULT 'owner',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    DeliverableType TEXT NULL
                );
                CREATE TABLE Comments (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, TicketId INTEGER NOT NULL, Content TEXT NOT NULL, Author TEXT NOT NULL DEFAULT 'owner', CreatedAt TEXT NOT NULL);
                CREATE TABLE ActivityEntries (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, TicketId INTEGER NOT NULL, Author TEXT NOT NULL, Text TEXT NOT NULL, CreatedAt TEXT NOT NULL);
                INSERT INTO Tickets (Title, DeliverableType, CreatedAt, UpdatedAt) VALUES ('Legacy post', 'blog-post', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                """;
            await schema.ExecuteNonQueryAsync();
        }

        var tickets = new TicketService(projects, new MemberService(projects));
        var detail = await tickets.GetTicketAsync(project.Slug, 1);
        Assert.NotNull(detail);
        Assert.Equal(ImageSourcePreference.Pexels, detail.ImageSource);
        Assert.Equal(VideoSourcePreference.None, detail.VideoSource);
        Assert.False(detail.RequireMediaBeforeDelivery);

        await tickets.ListTicketsAsync(project.Slug);
        await tickets.GetTicketAsync(project.Slug, 1);
        await using var verify = new SqliteConnection($"Data Source={dbPath};Foreign Keys=True");
        await verify.OpenAsync();
        var command = verify.CreateCommand();
        command.CommandText = "SELECT ImageSource, VideoSource, RequireMediaBeforeDelivery, MediaPreferencesCustomized FROM Tickets WHERE Id = 1";
        await using var row = await command.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal("Pexels", row.GetString(0));
        Assert.Equal("None", row.GetString(1));
        Assert.Equal(0, row.GetInt32(2));
        Assert.Equal(0, row.GetInt32(3));
    }

    [Fact]
    public async Task CreateTicket_PreservesAnExplicitAssigneeAndRejectsUnknownDeliverables()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("deliverable-routing");
        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "blog-writer");
        await members.CreateMemberAsync(project.Slug, "custom-editor");
        var tickets = new TicketService(projects, members);

        var created = await tickets.CreateTicketAsync(
            project.Slug,
            "Editorial exception",
            assignedTo: "custom-editor",
            deliverableType: "Blog Post");

        Assert.Equal("blog-post", created.DeliverableType);
        Assert.Equal("custom-editor", created.AssignedTo);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tickets.CreateTicketAsync(project.Slug, "Unknown", deliverableType: "podcast-episode"));
        Assert.Contains("Unknown deliverable type", error.Message, StringComparison.Ordinal);
        Assert.Single(await tickets.ListTicketsAsync(project.Slug));
    }

    [Fact]
    public async Task ClassifyingAnUnassignedBacklogTicket_DerivesTheEntryAgent()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("classify-backlog");
        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "blog-writer");
        var tickets = new TicketService(projects, members);
        var created = await tickets.CreateTicketAsync(project.Slug, "n8n intake");

        var updated = await tickets.UpdateTicketAsync(
            project.Slug,
            created.Id,
            author: "owner",
            deliverableType: "Product Review");

        Assert.NotNull(updated);
        Assert.Equal("product-review", updated.DeliverableType);
        Assert.Equal("blog-writer", updated.AssignedTo);
        var activity = (await tickets.GetTicketAsync(project.Slug, created.Id))!.Activities;
        Assert.Contains(activity, entry =>
            entry.Text.Contains("from deliverable type", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Backlog", "custom-editor")]
    [InlineData("Todo", null)]
    [InlineData("InProgress", null)]
    public async Task ClassifyingAssignedOrActiveTickets_DoesNotReplaceOrDeriveTheWorker(
        string status,
        string? assignedTo)
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync($"classify-{status}-{assignedTo ?? "none"}");
        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "blog-writer");
        if (assignedTo is not null)
            await members.CreateMemberAsync(project.Slug, assignedTo);
        var tickets = new TicketService(projects, members);
        var created = await tickets.CreateTicketAsync(
            project.Slug,
            "Existing work",
            status: status,
            assignedTo: assignedTo);

        var updated = await tickets.UpdateTicketAsync(
            project.Slug,
            created.Id,
            author: "owner",
            deliverableType: "Blog Post");

        Assert.NotNull(updated);
        Assert.Equal("blog-post", updated.DeliverableType);
        Assert.Equal(assignedTo, updated.AssignedTo);
    }
}
