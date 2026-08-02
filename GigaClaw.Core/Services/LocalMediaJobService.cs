using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GigaClaw.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GigaClaw.Core.Services;

/// <summary>The addressed project or job does not exist. The API edge answers 404.</summary>
public sealed class LocalMediaNotFoundException(string message) : InvalidOperationException(message);

/// <summary>The request itself is malformed — a missing author, an out-of-range stage index. The
/// API edge answers 400. This is the single source of request validation: the endpoint re-checks
/// nothing, so the two can never drift apart.</summary>
public sealed class LocalMediaValidationException(string message) : InvalidOperationException(message);

/// <summary>The job's persisted state refuses the operation — it is not running, or the report
/// would rewind progress. The API edge answers 409.</summary>
public sealed class LocalMediaConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// Durable control plane for governed local media generation.
///
/// Creative decisions stay in the execution spec and OpenMontage project. This service only
/// persists, schedules, cancels, and reports deterministic worker executions.
/// </summary>
public sealed class LocalMediaJobService : BackgroundService
{
    private const int DefaultMaxConcurrent = 2;
    /// <summary>Upper bound on a reported stage name. Long enough for any human-readable checkpoint
    /// label, short enough that a runaway worker cannot write a novel into the board.</summary>
    private const int MaxStageLength = 120;
    private static readonly HashSet<string> FinalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        LocalMediaJobStatuses.AwaitingReview,
        LocalMediaJobStatuses.Failed,
        LocalMediaJobStatuses.Cancelled,
        LocalMediaJobStatuses.Interrupted,
    };

    private readonly ProjectService _projects;
    private readonly TicketService _tickets;
    private readonly ILogger<LocalMediaJobService> _logger;
    private readonly ConcurrentDictionary<string, Task> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Process> _processes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _resources = new(StringComparer.Ordinal);
    private readonly int _maxConcurrent;

    public LocalMediaJobService(
        ProjectService projects,
        TicketService tickets,
        ILogger<LocalMediaJobService> logger)
    {
        _projects = projects;
        _tickets = tickets;
        _logger = logger;
        _maxConcurrent = int.TryParse(
            Environment.GetEnvironmentVariable("GIGACLAW_MAX_CONCURRENT_MEDIA_JOBS"),
            out var parsed) && parsed > 0
                ? parsed
                : DefaultMaxConcurrent;

        _tickets.TicketStatusChanged += OnTicketStatusChanged;
    }

    public async Task<CreateLocalMediaJobResult> CreateAsync(
        string projectSlug,
        CreateLocalMediaJobRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAuthor(request.Author);
        var project = await RequireProjectAsync(projectSlug);
        var workspace = Path.GetFullPath(_projects.ResolveWorkspacePath(project));
        var specPath = ResolveWorkspaceFile(workspace, request.ExecutionSpecPath, "execution spec");
        if (!File.Exists(specPath))
            throw new FileNotFoundException("The local-media execution spec does not exist.", specPath);

        var ticket = await _tickets.GetTicketAsync(projectSlug, request.TicketId)
            ?? throw new InvalidOperationException($"Ticket #{request.TicketId} does not exist.");
        if (string.Equals(ticket.Status, "Done", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ticket.Status, "Review", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A media job cannot be added to a ticket already in Review or Done.");

        var specBytes = await File.ReadAllBytesAsync(specPath, cancellationToken);
        var specHash = Convert.ToHexString(SHA256.HashData(specBytes)).ToLowerInvariant();
        var validated = ValidateExecutionSpec(specBytes, workspace, request);
        var idempotencyKey = request.IdempotencyKey.Trim();
        if (idempotencyKey.Length is < 8 or > 240)
            throw new InvalidOperationException("IdempotencyKey must be between 8 and 240 characters.");

        await EnsureTableAsync(projectSlug, cancellationToken);
        var existing = await FindByIdempotencyKeyAsync(projectSlug, idempotencyKey, cancellationToken);
        if (existing is not null)
            return new CreateLocalMediaJobResult(existing, false);

        var id = $"media-{Guid.NewGuid():N}";
        var receiptRelative = Path.Combine("media", "receipts", $"{id}.json").Replace('\\', '/');
        Directory.CreateDirectory(Path.Combine(workspace, "media", "receipts"));
        var now = DateTime.UtcNow;
        var job = new LocalMediaJob(
            id,
            request.TicketId,
            validated.Kind,
            validated.Provider,
            validated.ResourceClass,
            LocalMediaJobStatuses.Queued,
            Path.GetRelativePath(workspace, specPath).Replace('\\', '/'),
            specHash,
            receiptRelative,
            null,
            null,
            null,
            idempotencyKey,
            now,
            now,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        try
        {
            await InsertAsync(projectSlug, job, cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            existing = await FindByIdempotencyKeyAsync(projectSlug, idempotencyKey, cancellationToken);
            if (existing is not null)
                return new CreateLocalMediaJobResult(existing, false);
            throw;
        }

        await _tickets.AddCommentAsync(
            projectSlug,
            request.TicketId,
            $"MEDIA-JOB v1 id:{id} spec-sha256:{specHash}\nSubmitted by `{request.Author}` with locked provider `{validated.Provider}`. The ticket will return to Review when the durable worker finishes.",
            "local-media-worker");

        if (!string.Equals(ticket.Status, "Backlog", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await _tickets.TransitionTicketAsync(
                    projectSlug,
                    request.TicketId,
                    "Backlog",
                    assignedTo: null,
                    author: "local-media-worker",
                    expectedStatus: ticket.Status);
            }
            catch (TicketTransitionConflictException)
            {
                // The owner or another agent moved it while the job was being created. The job
                // remains valid; completion reconciliation will comment without forcing status.
            }
        }

        return new CreateLocalMediaJobResult(job, true);
    }

    public async Task<IReadOnlyList<LocalMediaJob>> ListAsync(
        string projectSlug,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(projectSlug);
        await EnsureTableAsync(projectSlug, cancellationToken);
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM MediaJobs ORDER BY CreatedAt DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var jobs = new List<LocalMediaJob>();
        while (await reader.ReadAsync(cancellationToken))
            jobs.Add(ReadJob(reader));
        return jobs;
    }

    public async Task<LocalMediaJob?> GetAsync(
        string projectSlug,
        string id,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(projectSlug);
        await EnsureTableAsync(projectSlug, cancellationToken);
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM MediaJobs WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    public async Task<LocalMediaJob> CancelAsync(
        string projectSlug,
        string id,
        string author,
        CancellationToken cancellationToken = default)
    {
        RequireAuthor(author);
        var job = await GetAsync(projectSlug, id, cancellationToken)
            ?? throw new LocalMediaNotFoundException("Local media job not found.");
        if (job.Status is not (LocalMediaJobStatuses.Queued or LocalMediaJobStatuses.Running))
            throw new LocalMediaConflictException("Only queued or running local media jobs can be cancelled.");

        if (!await TryCancelStateAsync(projectSlug, id, cancellationToken))
            throw new LocalMediaConflictException("The local media job finished before cancellation could be applied.");

        if (_processes.TryGetValue(JobKey(projectSlug, id), out var process))
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* the process may have exited between the check and kill */ }
        }

        await NotifyTicketAsync(projectSlug, id, cancellationToken);
        return (await GetAsync(projectSlug, id, cancellationToken))!;
    }

    public async Task<LocalMediaJob> UpdateStageAsync(
        string projectSlug,
        string id,
        UpdateLocalMediaJobStageRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAuthor(request.Author);
        var stage = ValidateStage(request);
        await RequireProjectAsync(projectSlug);
        await EnsureTableAsync(projectSlug, cancellationToken);
        var job = await GetAsync(projectSlug, id, cancellationToken)
            ?? throw new LocalMediaNotFoundException("Local media job not found.");

        // Fast path only. The authoritative running/monotonicity check lives in TrySetStageAsync's
        // UPDATE predicate: everything read here is already stale by the time the UPDATE executes,
        // so this exists to return the precise message cheaply, not to guarantee anything.
        if (!string.Equals(job.Status, LocalMediaJobStatuses.Running, StringComparison.OrdinalIgnoreCase))
            throw new LocalMediaConflictException("Stage progress can only be reported while the job is running.");
        if (request.StageIndex is int newIndex && job.StageIndex is int currentIndex && newIndex < currentIndex)
            throw new LocalMediaConflictException(
                $"StageIndex {newIndex} would regress the current StageIndex {currentIndex}.");

        if (await TrySetStageAsync(projectSlug, id, stage, request.StageIndex, request.StageCount, cancellationToken))
            return (await GetAsync(projectSlug, id, cancellationToken))!;

        // Zero rows updated: the row stopped satisfying the predicate between the read and the write.
        // One follow-up read tells the two causes apart so the caller gets the true reason rather
        // than whichever guess the stale snapshot suggested.
        var current = await GetAsync(projectSlug, id, cancellationToken)
            ?? throw new LocalMediaNotFoundException("Local media job not found.");
        throw string.Equals(current.Status, LocalMediaJobStatuses.Running, StringComparison.OrdinalIgnoreCase)
            ? new LocalMediaConflictException(
                $"StageIndex {request.StageIndex} would regress the current StageIndex {current.StageIndex}.")
            : new LocalMediaConflictException("The local media job is no longer running.");
    }

    /// <summary>Rejects a malformed stage report before it reaches SQLite. Endpoint-side validation
    /// was removed in favour of this one copy — two copies drift, and the drift is silent.</summary>
    private static string ValidateStage(UpdateLocalMediaJobStageRequest request)
    {
        var stage = request.Stage?.Trim();
        if (string.IsNullOrWhiteSpace(stage))
            throw new LocalMediaValidationException("The 'stage' field is required.");
        if (stage.Length > MaxStageLength)
            throw new LocalMediaValidationException(
                $"The 'stage' field must be at most {MaxStageLength} characters.");
        if (request.StageIndex is int index && index < 0)
            throw new LocalMediaValidationException("StageIndex must be zero or greater.");
        if (request.StageCount is int count && count <= 0)
            throw new LocalMediaValidationException("StageCount must be greater than zero.");
        if (request.StageIndex is int reported && request.StageCount is int total && reported > total)
            throw new LocalMediaValidationException(
                $"StageIndex {reported} must not exceed StageCount {total}.");
        return stage;
    }

    public async Task<LocalMediaJob> ReviewAsync(
        string projectSlug,
        string id,
        string decision,
        string author,
        CancellationToken cancellationToken = default)
    {
        RequireAuthor(author);
        var normalized = decision.Trim().ToLowerInvariant();
        if (normalized is not ("approved" or "rejected"))
            throw new LocalMediaValidationException("Decision must be approved or rejected.");
        var job = await GetAsync(projectSlug, id, cancellationToken)
            ?? throw new LocalMediaNotFoundException("Local media job not found.");
        if (!string.Equals(job.Status, LocalMediaJobStatuses.AwaitingReview, StringComparison.OrdinalIgnoreCase))
            throw new LocalMediaConflictException("Only a job awaiting review can be approved or rejected.");

        var status = normalized == "approved"
            ? LocalMediaJobStatuses.Approved
            : LocalMediaJobStatuses.Rejected;
        await SetReviewAsync(projectSlug, id, status, normalized, cancellationToken);
        await _tickets.AddCommentAsync(
            projectSlug,
            job.TicketId,
            $"MEDIA-REVIEW-DECISION v1 id:{id} decision:{normalized}",
            author);
        return (await GetAsync(projectSlug, id, cancellationToken))!;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedJobsAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileNotificationsAsync(stoppingToken);
                await DispatchQueuedJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Local media job pump failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task DispatchQueuedJobsAsync(CancellationToken cancellationToken)
    {
        if (_active.Count >= _maxConcurrent) return;
        var projects = await _projects.ListProjectsAsync();
        foreach (var project in projects)
        {
            if (_active.Count >= _maxConcurrent) break;
            await EnsureTableAsync(project.Slug, cancellationToken);
            var queued = await FindOldestQueuedAsync(project.Slug, cancellationToken);
            if (queued is null) continue;
            var key = JobKey(project.Slug, queued.Id);
            if (_active.ContainsKey(key)) continue;

            var task = RunQueuedJobAsync(project.Slug, queued, cancellationToken);
            if (!_active.TryAdd(key, task)) continue;
            _ = task.ContinueWith(
                completed => _active.TryRemove(key, out var ignored),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RunQueuedJobAsync(
        string projectSlug,
        LocalMediaJob queued,
        CancellationToken hostCancellation)
    {
        var resource = _resources.GetOrAdd(queued.ResourceClass, _ => new SemaphoreSlim(1, 1));
        await resource.WaitAsync(hostCancellation);
        try
        {
            if (!await ClaimAsync(projectSlug, queued.Id, hostCancellation)) return;
            var project = await RequireProjectAsync(projectSlug);
            var workspace = Path.GetFullPath(_projects.ResolveWorkspacePath(project));
            var script = Path.Combine(workspace, ".agents", "scripts", "media_generate.py");
            if (!File.Exists(script))
                throw new FileNotFoundException(
                    "The local-media worker is not installed. Re-initialize the project's agent template.",
                    script);

            var specPath = ResolveWorkspaceFile(workspace, queued.ExecutionSpecPath, "execution spec");
            var receiptPath = ResolveWorkspaceFile(workspace, queued.ReceiptPath, "receipt");
            var python = ResolvePythonExecutable(specPath);
            var timeout = ResolveTimeout(specPath, queued.Kind, queued.Provider);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(hostCancellation);
            timeoutCts.CancelAfter(timeout);
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add("execute");
            startInfo.ArgumentList.Add("--spec");
            startInfo.ArgumentList.Add(specPath);
            startInfo.ArgumentList.Add("--receipt");
            startInfo.ArgumentList.Add(receiptPath);
            startInfo.ArgumentList.Add("--workspace-root");
            startInfo.ArgumentList.Add(workspace);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("The local-media worker process did not start.");
            var key = JobKey(projectSlug, queued.Id);
            _processes[key] = process;
            await SetProcessIdAsync(projectSlug, queued.Id, process.Id, hostCancellation);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(hostCancellation);
            var stderrTask = process.StandardError.ReadToEndAsync(hostCancellation);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!hostCancellation.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch {}
                throw new TimeoutException($"Local media job timed out after {timeout.TotalMinutes:F0} minutes.");
            }
            finally
            {
                _processes.TryRemove(key, out _);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (!File.Exists(receiptPath))
                throw new InvalidOperationException(
                    $"Worker exited with code {process.ExitCode} without a receipt. {Compact(stderr, stdout)}");

            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath, hostCancellation));
            var root = receipt.RootElement;
            var success = root.TryGetProperty("success", out var successElement) && successElement.GetBoolean();
            if (!success)
            {
                var workerError = root.TryGetProperty("error", out var errorElement)
                    ? errorElement.GetString()
                    : null;
                throw new InvalidOperationException(workerError ?? Compact(stderr, stdout));
            }

            var output = root.TryGetProperty("output_path", out var outputElement)
                ? outputElement.GetString()
                : null;
            var provenance = root.TryGetProperty("provenance", out var provenanceElement)
                ? provenanceElement.GetRawText()
                : null;
            await TryUpdateRunningStateAsync(
                projectSlug,
                queued.Id,
                LocalMediaJobStatuses.AwaitingReview,
                error: null,
                outputPath: output,
                provenanceJson: provenance,
                processId: null,
                hostCancellation);
        }
        catch (OperationCanceledException) when (hostCancellation.IsCancellationRequested)
        {
            await TryUpdateRunningStateAsync(
                projectSlug,
                queued.Id,
                LocalMediaJobStatuses.Interrupted,
                "GigaClaw stopped while the local media job was running.",
                null,
                null,
                null,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Local media job {JobId} failed", queued.Id);
            await TryUpdateRunningStateAsync(
                projectSlug,
                queued.Id,
                LocalMediaJobStatuses.Failed,
                exception.Message,
                null,
                null,
                null,
                CancellationToken.None);
        }
        finally
        {
            resource.Release();
            await NotifyTicketAsync(projectSlug, queued.Id, CancellationToken.None);
        }
    }

    private async void OnTicketStatusChanged(
        string projectSlug,
        int ticketId,
        string fromStatus,
        string toStatus)
    {
        try
        {
            if (string.Equals(toStatus, "Done", StringComparison.OrdinalIgnoreCase))
                await ReviewAwaitingJobsForTicketAsync(projectSlug, ticketId, "approved");
            else if (string.Equals(fromStatus, "Review", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(toStatus, "Todo", StringComparison.OrdinalIgnoreCase))
                await ReviewAwaitingJobsForTicketAsync(projectSlug, ticketId, "rejected");
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to synchronize media approval for ticket {TicketId}",
                ticketId);
        }
    }

    private async Task ReviewAwaitingJobsForTicketAsync(
        string projectSlug,
        int ticketId,
        string decision)
    {
        await EnsureTableAsync(projectSlug, CancellationToken.None);
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE MediaJobs
               SET Status = $status,
                   ReviewDecision = $decision,
                   ReviewedAt = $now,
                   UpdatedAt = $now
             WHERE TicketId = $ticketId AND Status = $awaiting
            """;
        command.Parameters.AddWithValue("$status", decision == "approved"
            ? LocalMediaJobStatuses.Approved
            : LocalMediaJobStatuses.Rejected);
        command.Parameters.AddWithValue("$decision", decision);
        command.Parameters.AddWithValue("$now", Iso(DateTime.UtcNow));
        command.Parameters.AddWithValue("$ticketId", ticketId);
        command.Parameters.AddWithValue("$awaiting", LocalMediaJobStatuses.AwaitingReview);
        await command.ExecuteNonQueryAsync();
    }

    private async Task NotifyTicketAsync(
        string projectSlug,
        string id,
        CancellationToken cancellationToken)
    {
        var job = await GetAsync(projectSlug, id, cancellationToken);
        if (job is null || job.BoardNotifiedAt is not null || !FinalStatuses.Contains(job.Status))
            return;
        var ticket = await _tickets.GetTicketAsync(projectSlug, job.TicketId);
        if (ticket is null)
        {
            await MarkNotifiedAsync(projectSlug, id, cancellationToken);
            return;
        }

        var message = job.Status switch
        {
            LocalMediaJobStatuses.AwaitingReview =>
                $"MEDIA-RESULT v1 id:{job.Id} spec-sha256:{job.ExecutionSpecSha256}\nGeneration completed with locked provider `{job.Provider}`.\nArtifact: `{job.OutputPath}`\nReceipt: `{job.ReceiptPath}`\nIndependent media review is required before owner approval.",
            LocalMediaJobStatuses.Cancelled =>
                $"MEDIA-RESULT v1 id:{job.Id}\nThe local media job was cancelled.",
            LocalMediaJobStatuses.Interrupted =>
                $"MEDIA-RESULT v1 id:{job.Id}\nThe local media job was interrupted by a GigaClaw shutdown. Review the provider state before retrying; do not resubmit blindly.",
            _ =>
                $"MEDIA-RESULT v1 id:{job.Id}\nGeneration failed with locked provider `{job.Provider}`: {job.Error}",
        };
        await _tickets.AddCommentAsync(projectSlug, job.TicketId, message, "local-media-worker");

        var target = job.Status == LocalMediaJobStatuses.AwaitingReview
            ? "Review"
            : job.Status == LocalMediaJobStatuses.Cancelled
                ? "Todo"
                : "Blocked";
        if (string.Equals(ticket.Status, "Backlog", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await _tickets.TransitionTicketAsync(
                    projectSlug,
                    job.TicketId,
                    target,
                    assignedTo: null,
                    author: "local-media-worker",
                    expectedStatus: "Backlog");
            }
            catch (TicketTransitionConflictException)
            {
                // A concurrent owner move wins; the receipt comment still reports the outcome.
            }
        }
        await MarkNotifiedAsync(projectSlug, id, cancellationToken);
    }

    private async Task ReconcileNotificationsAsync(CancellationToken cancellationToken)
    {
        foreach (var project in await _projects.ListProjectsAsync())
        {
            await EnsureTableAsync(project.Slug, cancellationToken);
            var ids = await FindUnnotifiedFinalIdsAsync(project.Slug, cancellationToken);
            foreach (var id in ids)
                await NotifyTicketAsync(project.Slug, id, cancellationToken);
        }
    }

    private async Task RecoverInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        foreach (var project in await _projects.ListProjectsAsync())
        {
            await EnsureTableAsync(project.Slug, cancellationToken);
            await using var connection = OpenConnection(project.Slug);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE MediaJobs
                   SET Status = $interrupted,
                       Error = 'GigaClaw restarted while the local media job was running.',
                       ProcessId = NULL,
                       UpdatedAt = $now,
                       BoardNotifiedAt = NULL
                 WHERE Status = $running
                """;
            command.Parameters.AddWithValue("$interrupted", LocalMediaJobStatuses.Interrupted);
            command.Parameters.AddWithValue("$running", LocalMediaJobStatuses.Running);
            command.Parameters.AddWithValue("$now", Iso(DateTime.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<Project> RequireProjectAsync(string projectSlug) =>
        await _projects.GetProjectAsync(projectSlug)
        ?? throw new LocalMediaNotFoundException($"Project '{projectSlug}' does not exist.");

    private static string ResolveWorkspaceFile(string workspace, string relativePath, string description)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException($"The {description} path must be relative to the workspace.");
        var resolved = Path.GetFullPath(Path.Combine(workspace, relativePath));
        if (!IsUnder(resolved, workspace))
            throw new InvalidOperationException($"The {description} path escapes the workspace.");
        return resolved;
    }

    private static ValidatedSpec ValidateExecutionSpec(
        byte[] bytes,
        string workspace,
        CreateLocalMediaJobRequest request)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out var version) ||
            version.GetInt32() != 1)
            throw new InvalidOperationException("Execution spec must be an object with version 1.");

        var kind = RequiredString(root, "kind").ToLowerInvariant();
        var provider = RequiredString(root, "provider").ToLowerInvariant();
        if (!string.Equals(kind, request.Kind.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(provider, request.Provider.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Request kind/provider must match the locked execution spec.");
        if (provider == "auto")
            throw new InvalidOperationException("Provider 'auto' is forbidden; the execution spec must lock one provider.");
        if (kind == "image" && provider != "comfyui")
            throw new InvalidOperationException("Local image jobs currently require the explicit comfyui provider.");
        if (kind == "clip" && provider is not ("comfyui" or "phosphene"))
            throw new InvalidOperationException("Local clip jobs require an explicit comfyui or phosphene provider.");
        if (kind is not ("image" or "clip"))
            throw new InvalidOperationException("Local media kind must be image or clip.");
        _ = RequiredString(root, "prompt");

        if (!root.TryGetProperty("governance", out var governance) ||
            !string.Equals(RequiredString(governance, "approvalStatus"), "approved", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(RequiredString(governance, "approvedBy")))
            throw new InvalidOperationException("Execution spec must record an explicit approved governance decision.");
        _ = RequiredString(governance, "licenseNotes");
        if (!governance.TryGetProperty("layer3SkillsRead", out var skills) ||
            skills.ValueKind != JsonValueKind.Array ||
            skills.GetArrayLength() == 0 ||
            skills.EnumerateArray().Any(skill =>
                skill.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(skill.GetString())))
            throw new InvalidOperationException("Execution spec must record the provider Layer 3 skills read.");

        var outputPath = RequiredString(root, "outputPath");
        var openMontage = root.TryGetProperty("openMontage", out var om) ? om : default;
        var openMontagePath = openMontage.ValueKind == JsonValueKind.Object
            ? RequiredString(openMontage, "path")
            : "";
        var allowedOutputRoot = workspace;
        if (!string.IsNullOrWhiteSpace(openMontagePath))
        {
            openMontagePath = Path.GetFullPath(openMontagePath);
            var projectId = RequiredString(openMontage, "projectId");
            allowedOutputRoot = Path.Combine(openMontagePath, "projects", projectId);
        }
        var resolvedOutput = Path.IsPathRooted(outputPath)
            ? Path.GetFullPath(outputPath)
            : Path.GetFullPath(Path.Combine(workspace, outputPath));
        if (!IsUnder(resolvedOutput, workspace) && !IsUnder(resolvedOutput, allowedOutputRoot))
            throw new InvalidOperationException("Output path must stay inside the workspace or locked OpenMontage project.");
        var extension = Path.GetExtension(resolvedOutput).ToLowerInvariant();
        if (kind == "image" && extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            throw new InvalidOperationException("Image output must use png, jpg, jpeg, or webp.");
        if (kind == "clip" && extension is not (".mp4" or ".mov" or ".webm"))
            throw new InvalidOperationException("Clip output must use mp4, mov, or webm.");

        if (kind == "clip")
        {
            if (openMontage.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Clip jobs require an OpenMontage project contract.");
            var projectId = RequiredString(openMontage, "projectId");
            var pipeline = RequiredString(openMontage, "pipeline");
            if (!string.Equals(RequiredString(openMontage, "stage"), "assets", StringComparison.Ordinal))
                throw new InvalidOperationException("Clip jobs may execute only at the OpenMontage assets stage.");
            var checkpoint = RequiredString(openMontage, "checkpointPath");
            var checkpointPath = Path.IsPathRooted(checkpoint)
                ? Path.GetFullPath(checkpoint)
                : Path.GetFullPath(Path.Combine(openMontagePath, checkpoint));
            if (!IsUnder(checkpointPath, Path.Combine(openMontagePath, "projects", projectId)))
                throw new InvalidOperationException("OpenMontage checkpoint must be inside the locked project directory.");
            if (!File.Exists(checkpointPath))
                throw new InvalidOperationException("OpenMontage checkpoint does not exist.");

            using var checkpointDocument = JsonDocument.Parse(File.ReadAllText(checkpointPath));
            var checkpointRoot = checkpointDocument.RootElement;
            if (!string.Equals(RequiredString(checkpointRoot, "status"), "completed", StringComparison.Ordinal) ||
                !checkpointRoot.TryGetProperty("human_approved", out var humanApproved) ||
                humanApproved.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Clip jobs require a completed, human-approved OpenMontage checkpoint.");
            if (!string.Equals(RequiredString(checkpointRoot, "pipeline_type"), pipeline, StringComparison.Ordinal) ||
                !string.Equals(RequiredString(checkpointRoot, "project_id"), projectId, StringComparison.Ordinal))
                throw new InvalidOperationException("OpenMontage checkpoint does not match the locked project and pipeline.");
        }

        return new ValidatedSpec(kind, provider, provider == "comfyui" ? "gpu:comfyui" : "mlx:phosphene");
    }

    private static string ResolvePythonExecutable(string specPath)
    {
        var configured = Environment.GetEnvironmentVariable("OPENMONTAGE_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        using var document = JsonDocument.Parse(File.ReadAllText(specPath));
        if (document.RootElement.TryGetProperty("runtime", out var runtime) &&
            runtime.TryGetProperty("pythonExecutable", out var python) &&
            python.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(python.GetString()))
            return python.GetString()!;
        if (document.RootElement.TryGetProperty("openMontage", out var openMontage))
        {
            var path = RequiredString(openMontage, "path");
            var venvPython = OperatingSystem.IsWindows()
                ? Path.Combine(path, ".venv", "Scripts", "python.exe")
                : Path.Combine(path, ".venv", "bin", "python");
            if (File.Exists(venvPython)) return venvPython;
        }
        return OperatingSystem.IsWindows() ? "python" : "python3";
    }

    private static TimeSpan ResolveTimeout(string specPath, string kind, string provider)
    {
        if (kind == "image") return TimeSpan.FromMinutes(12);
        using var document = JsonDocument.Parse(File.ReadAllText(specPath));
        var quality = document.RootElement.TryGetProperty("parameters", out var parameters) &&
                      parameters.TryGetProperty("quality", out var qualityElement)
            ? qualityElement.GetString()
            : null;
        if (provider == "phosphene")
        {
            return quality switch
            {
                "high" => TimeSpan.FromMinutes(35),
                "standard" => TimeSpan.FromMinutes(25),
                "quick" => TimeSpan.FromMinutes(8),
                _ => TimeSpan.FromMinutes(20),
            };
        }
        return TimeSpan.FromMinutes(17);
    }

    private async Task EnsureTableAsync(string projectSlug, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS MediaJobs (
                Id TEXT NOT NULL PRIMARY KEY,
                TicketId INTEGER NOT NULL,
                Kind TEXT NOT NULL,
                Provider TEXT NOT NULL,
                ResourceClass TEXT NOT NULL,
                Status TEXT NOT NULL,
                ExecutionSpecPath TEXT NOT NULL,
                ExecutionSpecSha256 TEXT NOT NULL,
                ReceiptPath TEXT NOT NULL,
                OutputPath TEXT NULL,
                ProvenanceJson TEXT NULL,
                Error TEXT NULL,
                IdempotencyKey TEXT NOT NULL UNIQUE,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                ReviewedAt TEXT NULL,
                ReviewDecision TEXT NULL,
                ProcessId INTEGER NULL,
                BoardNotifiedAt TEXT NULL,
                Stage TEXT NULL,
                StageIndex INTEGER NULL,
                StageCount INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS IX_MediaJobs_StatusCreatedAt
                ON MediaJobs(Status, CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_MediaJobs_TicketId
                ON MediaJobs(TicketId);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // CREATE TABLE IF NOT EXISTS above only shapes a brand-new database; a table created before
        // this field set was added still lacks these columns, so add them here in the repo's inline
        // ALTER TABLE ADD COLUMN convention (tolerating "already exists" on a table that already
        // migrated).
        await AddColumnIfMissingAsync(connection, "ALTER TABLE MediaJobs ADD COLUMN Stage TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, "ALTER TABLE MediaJobs ADD COLUMN StageIndex INTEGER NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, "ALTER TABLE MediaJobs ADD COLUMN StageCount INTEGER NULL", cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        string alterSql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = alterSql;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (
            exception.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists — expected on an already-migrated database.
        }
    }

    private SqliteConnection OpenConnection(string projectSlug) =>
        new($"Data Source={_projects.GetProjectDbPath(projectSlug)}");

    private async Task InsertAsync(
        string projectSlug,
        LocalMediaJob job,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO MediaJobs (
                Id, TicketId, Kind, Provider, ResourceClass, Status,
                ExecutionSpecPath, ExecutionSpecSha256, ReceiptPath,
                OutputPath, ProvenanceJson, Error, IdempotencyKey,
                CreatedAt, UpdatedAt, ReviewedAt, ReviewDecision, ProcessId, BoardNotifiedAt
            ) VALUES (
                $id, $ticketId, $kind, $provider, $resourceClass, $status,
                $executionSpecPath, $executionSpecSha256, $receiptPath,
                NULL, NULL, NULL, $idempotencyKey,
                $createdAt, $updatedAt, NULL, NULL, NULL, NULL
            )
            """;
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$ticketId", job.TicketId);
        command.Parameters.AddWithValue("$kind", job.Kind);
        command.Parameters.AddWithValue("$provider", job.Provider);
        command.Parameters.AddWithValue("$resourceClass", job.ResourceClass);
        command.Parameters.AddWithValue("$status", job.Status);
        command.Parameters.AddWithValue("$executionSpecPath", job.ExecutionSpecPath);
        command.Parameters.AddWithValue("$executionSpecSha256", job.ExecutionSpecSha256);
        command.Parameters.AddWithValue("$receiptPath", job.ReceiptPath);
        command.Parameters.AddWithValue("$idempotencyKey", job.IdempotencyKey);
        command.Parameters.AddWithValue("$createdAt", Iso(job.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Iso(job.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<LocalMediaJob?> FindByIdempotencyKeyAsync(
        string projectSlug,
        string key,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM MediaJobs WHERE IdempotencyKey = $key";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    private async Task<LocalMediaJob?> FindOldestQueuedAsync(
        string projectSlug,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM MediaJobs WHERE Status = $status ORDER BY CreatedAt LIMIT 1";
        command.Parameters.AddWithValue("$status", LocalMediaJobStatuses.Queued);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    private async Task<bool> ClaimAsync(
        string projectSlug,
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE MediaJobs
               SET Status = $running, UpdatedAt = $now
             WHERE Id = $id AND Status = $queued
            """;
        command.Parameters.AddWithValue("$running", LocalMediaJobStatuses.Running);
        command.Parameters.AddWithValue("$queued", LocalMediaJobStatuses.Queued);
        command.Parameters.AddWithValue("$now", Iso(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<bool> TryUpdateRunningStateAsync(
        string projectSlug,
        string id,
        string status,
        string? error,
        string? outputPath,
        string? provenanceJson,
        int? processId,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE MediaJobs
               SET Status = $status,
                   Error = $error,
                   OutputPath = $outputPath,
                   ProvenanceJson = $provenanceJson,
                   ProcessId = $processId,
                   UpdatedAt = $now,
                   BoardNotifiedAt = NULL
             WHERE Id = $id AND Status = $running
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$outputPath", (object?)outputPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$provenanceJson", (object?)provenanceJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$processId", (object?)processId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Iso(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$running", LocalMediaJobStatuses.Running);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<bool> TrySetStageAsync(
        string projectSlug,
        string id,
        string stage,
        int? stageIndex,
        int? stageCount,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // The monotonic guard is the UPDATE's own predicate, not a C# read-then-write: two workers
        // reporting checkpoints concurrently both read the same StageIndex, so a guard outside the
        // statement lets the slower report win and rewind the progress the UI already showed.
        // COALESCE keeps a report that omits an index from erasing one, which is the same rewind by
        // another name.
        command.CommandText = """
            UPDATE MediaJobs
               SET Stage = $stage,
                   StageIndex = COALESCE($stageIndex, StageIndex),
                   StageCount = COALESCE($stageCount, StageCount),
                   UpdatedAt = $now
             WHERE Id = $id
               AND Status = $running
               AND (StageIndex IS NULL OR $stageIndex IS NULL OR StageIndex <= $stageIndex)
            """;
        command.Parameters.AddWithValue("$stage", stage);
        command.Parameters.AddWithValue("$stageIndex", (object?)stageIndex ?? DBNull.Value);
        command.Parameters.AddWithValue("$stageCount", (object?)stageCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Iso(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$running", LocalMediaJobStatuses.Running);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<bool> TryCancelStateAsync(
        string projectSlug,
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE MediaJobs
               SET Status = $cancelled,
                   Error = NULL,
                   OutputPath = NULL,
                   ProvenanceJson = NULL,
                   ProcessId = NULL,
                   UpdatedAt = $now,
                   BoardNotifiedAt = NULL
             WHERE Id = $id AND Status IN ($queued, $running)
            """;
        command.Parameters.AddWithValue("$cancelled", LocalMediaJobStatuses.Cancelled);
        command.Parameters.AddWithValue("$queued", LocalMediaJobStatuses.Queued);
        command.Parameters.AddWithValue("$running", LocalMediaJobStatuses.Running);
        command.Parameters.AddWithValue("$now", Iso(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task SetProcessIdAsync(
        string projectSlug,
        string id,
        int processId,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MediaJobs SET ProcessId = $processId, UpdatedAt = $now WHERE Id = $id";
        command.Parameters.AddWithValue("$processId", processId);
        command.Parameters.AddWithValue("$now", Iso(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SetReviewAsync(
        string projectSlug,
        string id,
        string status,
        string decision,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE MediaJobs
               SET Status = $status,
                   ReviewDecision = $decision,
                   ReviewedAt = $now,
                   UpdatedAt = $now
             WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$decision", decision);
        command.Parameters.AddWithValue("$now", Iso(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> FindUnnotifiedFinalIdsAsync(
        string projectSlug,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id FROM MediaJobs
             WHERE BoardNotifiedAt IS NULL
               AND Status IN ($awaiting, $failed, $cancelled, $interrupted)
             ORDER BY UpdatedAt
            """;
        command.Parameters.AddWithValue("$awaiting", LocalMediaJobStatuses.AwaitingReview);
        command.Parameters.AddWithValue("$failed", LocalMediaJobStatuses.Failed);
        command.Parameters.AddWithValue("$cancelled", LocalMediaJobStatuses.Cancelled);
        command.Parameters.AddWithValue("$interrupted", LocalMediaJobStatuses.Interrupted);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetString(0));
        return ids;
    }

    private async Task MarkNotifiedAsync(
        string projectSlug,
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(projectSlug);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MediaJobs SET BoardNotifiedAt = $now WHERE Id = $id";
        command.Parameters.AddWithValue("$now", Iso(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static LocalMediaJob ReadJob(SqliteDataReader reader)
    {
        string? NullableString(string name) =>
            reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));
        DateTime? NullableDate(string name)
        {
            var raw = NullableString(name);
            return raw is null ? null : DateTime.Parse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind);
        }

        return new LocalMediaJob(
            reader.GetString(reader.GetOrdinal("Id")),
            reader.GetInt32(reader.GetOrdinal("TicketId")),
            reader.GetString(reader.GetOrdinal("Kind")),
            reader.GetString(reader.GetOrdinal("Provider")),
            reader.GetString(reader.GetOrdinal("ResourceClass")),
            reader.GetString(reader.GetOrdinal("Status")),
            reader.GetString(reader.GetOrdinal("ExecutionSpecPath")),
            reader.GetString(reader.GetOrdinal("ExecutionSpecSha256")),
            reader.GetString(reader.GetOrdinal("ReceiptPath")),
            NullableString("OutputPath"),
            NullableString("ProvenanceJson"),
            NullableString("Error"),
            reader.GetString(reader.GetOrdinal("IdempotencyKey")),
            DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt")), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTime.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt")), null, System.Globalization.DateTimeStyles.RoundtripKind),
            NullableDate("ReviewedAt"),
            NullableString("ReviewDecision"),
            reader.IsDBNull(reader.GetOrdinal("ProcessId")) ? null : reader.GetInt32(reader.GetOrdinal("ProcessId")),
            NullableDate("BoardNotifiedAt"),
            NullableString("Stage"),
            reader.IsDBNull(reader.GetOrdinal("StageIndex")) ? null : reader.GetInt32(reader.GetOrdinal("StageIndex")),
            reader.IsDBNull(reader.GetOrdinal("StageCount")) ? null : reader.GetInt32(reader.GetOrdinal("StageCount")));
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException($"Execution spec field '{name}' is required.");
        return value.GetString()!;
    }

    private static void RequireAuthor(string? author)
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new LocalMediaValidationException("The 'author' field is required.");
    }

    private static bool IsUnder(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return normalizedPath.StartsWith(normalizedRoot, comparison);
    }

    private static string Iso(DateTime value) => value.ToUniversalTime().ToString("O");
    private static string JobKey(string projectSlug, string id) => $"{projectSlug}:{id}";
    private static string Compact(params string[] values)
    {
        var text = string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        return text.Length <= 1200 ? text : text[..1200];
    }

    private sealed record ValidatedSpec(string Kind, string Provider, string ResourceClass);
}
