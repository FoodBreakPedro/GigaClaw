using System.Diagnostics;
using System.Text.Json;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Automation.Runners;
using GigaClaw.Core.Models;
using Microsoft.Extensions.Logging;

namespace GigaClaw.Core.Automation;

/// <summary>Codex CLI implementation of the host-neutral agent runner contract.</summary>
public sealed class CodexRunner : IAgentRunner
{
    private static readonly TimeSpan PumpDrainGrace = TimeSpan.FromSeconds(10);

    private readonly SessionRegistry _sessions;
    private readonly AgentRunRegistry _runs;
    private readonly RunConcurrencyGate _gate;
    private readonly ILogger<CodexRunner> _logger;
    private readonly string _codexBinary;

    public CodexRunner(
        SessionRegistry sessions,
        AgentRunRegistry runs,
        RunConcurrencyGate gate,
        ILogger<CodexRunner> logger)
    {
        _sessions = sessions;
        _runs = runs;
        _gate = gate;
        _logger = logger;
        _codexBinary = ResolveCodexBinary();
    }

    public async Task<AgentRun> RunAsync(ClaudeRunContext ctx, CancellationToken ct)
    {
        var modelIsValid = CodexModelCatalog.TryResolve(ctx.Model, out var model);
        var run = new AgentRun
        {
            RunId = ctx.PresetRunId ?? Guid.NewGuid().ToString("N"),
            ProjectSlug = ctx.ProjectSlug,
            TicketId = ctx.TicketId,
            AgentName = ctx.AgentName,
            SkillFile = ctx.SkillFile,
            ConcurrencyGroup = string.IsNullOrEmpty(ctx.ConcurrencyGroup) ? ctx.AgentName : ctx.ConcurrencyGroup,
            StartedAt = DateTime.UtcNow,
            Model = model,
            ChatTarget = ctx.ChatTarget,
            LockTimeoutMinutes = ctx.LockTimeoutMinutes,
            Backend = "codex",
        };
        if (ctx.OnEventHook is not null) run.OnEvent += ctx.OnEventHook;
        _runs.Register(run);

        if (!modelIsValid)
        {
            run.Push(new StreamEvent(
                DateTime.UtcNow,
                "error",
                $"Invalid Codex model '{model}'. Choose a gpt-* model or a supported Claude tier."));
            _runs.Complete(run.RunId, AgentRunStatus.Failed, -1);
            return run;
        }

        if (ctx.OllamaValidationError is not null)
        {
            run.Push(new StreamEvent(DateTime.UtcNow, "error", ctx.OllamaValidationError));
            _runs.Complete(run.RunId, AgentRunStatus.Failed, -1);
            return run;
        }

        string skillContent;
        if (ctx.InlineSkillContent is not null)
        {
            skillContent = ctx.InlineSkillContent;
        }
        else
        {
            var skillPath = Path.IsPathRooted(ctx.SkillFile)
                ? ctx.SkillFile
                : Path.Combine(ctx.WorkspacePath, ".agents", ctx.SkillFile);
            if (!File.Exists(skillPath))
            {
                run.Push(new StreamEvent(DateTime.UtcNow, "error", $"Skill file not found: {skillPath}"));
                _runs.Complete(run.RunId, AgentRunStatus.Failed, -1);
                return run;
            }
            skillContent = await File.ReadAllTextAsync(skillPath, ct);
        }

        var scopedAgent = $"codex:{(ctx.SessionScope is null ? ctx.AgentName : $"{ctx.SessionScope}:{ctx.AgentName}")}";
        var sessionId = ctx.PersistSession
            ? _sessions.GetSessionId(ctx.WorkspacePath, scopedAgent, ctx.TicketId)
            : null;
        var isResume = sessionId is not null;
        run.SessionId = sessionId;
        run.ExternalRunId = sessionId;

        var isChat = ctx.SessionScope == "chat";
        IDisposable slot;
        var snapshot = _gate.Snapshot();
        if (!isChat && snapshot.Active >= snapshot.Max)
        {
            run.Push(new StreamEvent(DateTime.UtcNow, "queued",
                $"Waiting for a free agent slot ({snapshot.Active}/{snapshot.Max} active, {snapshot.Queued} queued ahead)"));
        }
        try
        {
            slot = await _gate.AcquireAsync(isChat, ctx.AgentName, run.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
            return run;
        }

        PolicyHookRunSession? policyHooks = null;
        var policyPublished = false;
        async Task PublishPolicyAsync()
        {
            if (policyHooks is null || policyPublished) return;
            await policyHooks.StopAndDrainAsync();
            PublishPolicyObservations(run, policyHooks);
            policyPublished = true;
        }

        try
        {
            var policy = await ContractPolicyLoader.LoadAsync(ctx.WorkspacePath, ctx.AgentName, ct);
            policyHooks = await PolicyHookRunSession.StartAsync(policy, run.RunId, ct);

            var attempt = await SpawnAndWaitAsync(
                ctx, run, skillContent, scopedAgent, sessionId, isResume, model, policyHooks, ct);

            if (attempt.Cancelled)
            {
                await PublishPolicyAsync();
                _runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
                return run;
            }

            if (ctx.RetryOnResumeFailure && isResume && attempt.Exit != 0 && attempt.AssistantEventCount == 0)
            {
                run.Push(new StreamEvent(DateTime.UtcNow, "reset", "Previous Codex thread expired, starting a new one"));
                _sessions.Clear(ctx.WorkspacePath, scopedAgent, ctx.TicketId);
                sessionId = null;
                run.SessionId = null;
                run.ExternalRunId = null;
                attempt = await SpawnAndWaitAsync(
                    ctx, run, skillContent, scopedAgent, sessionId, isResume: false, model, policyHooks, ct);
                if (attempt.Cancelled)
                {
                    await PublishPolicyAsync();
                    _runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
                    return run;
                }
            }

            while (ctx.SessionScope == "chat" && attempt.Exit == 0 && run.PendingSteerMessages.Count > 0)
            {
                var messages = run.DrainPendingSteerMessages();
                var steerText = string.Join("\n", messages.Select(m => $"[Steering message from previous turn]: {m}"));
                run.Push(new StreamEvent(DateTime.UtcNow, "steer_replay", $"Replaying {messages.Count} injected message(s)"));
                attempt = await SpawnAndWaitAsync(
                    ctx.WithChatReplay(steerText), run, skillContent, scopedAgent,
                    run.SessionId, isResume: true, model, policyHooks, ct);
                if (attempt.Cancelled)
                {
                    await PublishPolicyAsync();
                    _runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
                    return run;
                }
            }

            await PublishPolicyAsync();
            if (run.HasUsage && run.TotalCostUsd is null)
            {
                run.Push(new StreamEvent(
                    DateTime.UtcNow,
                    "cost_unavailable",
                    "Codex reported token usage but did not report priced cost"));
            }

            _runs.Complete(
                run.RunId,
                attempt.Exit == 0 ? AgentRunStatus.Completed : AgentRunStatus.Failed,
                attempt.Exit);
            return run;
        }
        catch (OperationCanceledException)
        {
            await PublishPolicyAsync();
            _runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in CodexRunner for {Agent} run={RunId}", ctx.AgentName, run.RunId);
            try { run.Push(new StreamEvent(DateTime.UtcNow, "error", $"Internal Codex runner error: {ex.Message}")); }
            catch { }
            await PublishPolicyAsync();
            _runs.Complete(run.RunId, AgentRunStatus.Failed, -1);
            return run;
        }
        finally
        {
            if (policyHooks is not null)
            {
                await PublishPolicyAsync();
                try { await policyHooks.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Codex policy hook cleanup failed"); }
            }
            _runs.Complete(run.RunId, AgentRunStatus.Failed, -1);
            slot.Dispose();
            ClaudeRunner.CleanupImageTempFiles(ctx);
        }
    }

    private readonly record struct SpawnResult(int Exit, int AssistantEventCount, bool Cancelled);

    private async Task<SpawnResult> SpawnAndWaitAsync(
        ClaudeRunContext ctx,
        AgentRun run,
        string skillContent,
        string scopedAgent,
        string? sessionId,
        bool isResume,
        string model,
        PolicyHookRunSession policyHooks,
        CancellationToken ct)
    {
        var acknowledgementBefore = policyHooks.AcknowledgementCount;
        var prompt = await ClaudeRunner.BuildPromptAsync(ctx, skillContent, isResume, ct);
        var args = BuildArguments(ctx, sessionId, isResume, model, policyHooks.Endpoint);
        var psi = ProcessLifecycleManager.BuildProcessStartInfo(
            ctx, args, _codexBinary, isClaude: false);

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            run.Push(new StreamEvent(DateTime.UtcNow, "error", $"Codex spawn failed: {ex.Message}"));
            return new SpawnResult(-1, 0, false);
        }

        run.Push(new StreamEvent(
            DateTime.UtcNow,
            "launch",
            $"{ctx.AgentName} {(isResume ? "(resume)" : "(new)")} cwd={ctx.ExecutionPath ?? ctx.WorkspacePath} skill={ctx.SkillFile}"));
        try { _runs.NoteProcessId(run.RunId, proc.Id); } catch { }

        var job = ProcessJobObject.TryCreateAndAssign(proc);
        try
        {
            var state = new CodexStreamState();
            void OnThreadStarted(string threadId)
            {
                run.SessionId = threadId;
                run.ExternalRunId = threadId;
                if (ctx.PersistSession)
                    _sessions.SetSessionId(ctx.WorkspacePath, scopedAgent, ctx.TicketId, threadId);
                _runs.Persist(run);
            }

            try
            {
                await proc.StandardInput.WriteAsync(prompt);
                await proc.StandardInput.FlushAsync(ct);
                proc.StandardInput.Close();
            }
            catch (Exception ex)
            {
                run.Push(new StreamEvent(DateTime.UtcNow, "error", $"Codex stdin write failed: {ex.Message}"));
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Cancellation.Token);
            using var timeout = ctx.MaxRunDuration is { } duration
                ? new CancellationTokenSource(duration)
                : new CancellationTokenSource();
            using var runToken = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, timeout.Token);

            using var resultGrace = new CancellationTokenSource();
            var resultOutcome = 0; // 0 = none yet, 1 = turn.completed, -1 = turn.failed
            Action<StreamEvent> resultWatch = ev =>
            {
                if (IsTerminalCodexEvent(ev, "turn.completed"))
                {
                    Interlocked.CompareExchange(ref resultOutcome, 1, 0);
                    resultGrace.CancelAfter(ResultExitGrace);
                }
                else if (IsTerminalCodexEvent(ev, "turn.failed"))
                {
                    Interlocked.Exchange(ref resultOutcome, -1);
                    resultGrace.CancelAfter(ResultExitGrace);
                }
            };
            run.OnEvent += resultWatch;
            using var waitToken = CancellationTokenSource.CreateLinkedTokenSource(runToken.Token, resultGrace.Token);

            var stdout = CodexStreamPump.PumpStdoutAsync(proc, run, state, OnThreadStarted, runToken.Token);
            var stderr = CodexStreamPump.PumpStderrAsync(proc, run, runToken.Token);
            var steering = CodexStreamPump.PumpSteeringAsync(run, runToken.Token);
            using var kill = runToken.Token.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            });

            int exit;
            try
            {
                await proc.WaitForExitAsync(waitToken.Token);
                exit = proc.ExitCode;
            }
            catch (OperationCanceledException)
            {
                if (proc.HasExited)
                {
                    exit = proc.ExitCode;
                }
                else if (timeout.IsCancellationRequested)
                {
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                    job?.Dispose();
                    run.Push(new StreamEvent(DateTime.UtcNow, "error",
                        $"Codex run exceeded maximum duration of {ctx.MaxRunDuration?.TotalMinutes:F0} minutes and was killed"));
                    run.OnEvent -= resultWatch;
                    return new SpawnResult(-1, state.AssistantEventCount, false);
                }
                else if (linked.IsCancellationRequested)
                {
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                    job?.Dispose();
                    run.OnEvent -= resultWatch;
                    return new SpawnResult(-1, state.AssistantEventCount, true);
                }
                else
                {
                    _logger.LogWarning(
                        "{Agent} run={RunId} emitted a terminal Codex turn event but did not exit within {Grace}s; killing the process tree",
                        ctx.AgentName, run.RunId, ResultExitGrace.TotalSeconds);
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                    exit = resultOutcome == 1 ? 0 : 1;
                }
            }

            run.OnEvent -= resultWatch;
            job?.Dispose();
            var drain = Task.WhenAll(stdout, stderr);
            if (await Task.WhenAny(drain, Task.Delay(PumpDrainGrace, CancellationToken.None)) != drain)
            {
                runToken.Cancel();
                try { await drain; } catch { }
            }
            if (!runToken.IsCancellationRequested) runToken.Cancel();
            try { await steering; } catch { }
            while (run.SteeringQueue.Reader.TryRead(out var queued)) run.AddPendingSteerMessage(queued);

            if (state.TerminalOutcome < 0) exit = exit == 0 ? 1 : exit;
            if (state.TerminalOutcome == 0 && exit == 0)
            {
                run.Push(new StreamEvent(DateTime.UtcNow, "error", "Codex exited without a terminal turn event"));
                exit = -1;
            }
            if (policyHooks.AcknowledgementCount <= acknowledgementBefore)
            {
                run.Push(new StreamEvent(DateTime.UtcNow, "error",
                    "Codex did not acknowledge the generated policy hooks; failing the attempt closed"));
                exit = -1;
            }
            return new SpawnResult(exit, state.AssistantEventCount, false);
        }
        finally
        {
            job?.Dispose();
            proc.Dispose();
        }
    }

    private TimeSpan _resultExitGrace = TimeSpan.FromSeconds(15);
    internal TimeSpan ResultExitGrace { get => _resultExitGrace; set => _resultExitGrace = value; }

    private static bool IsTerminalCodexEvent(StreamEvent ev, string type) =>
        ev.Detail?.Contains($"\"type\":\"{type}\"", StringComparison.OrdinalIgnoreCase) == true ||
        ev.Detail?.Contains($"\"type\": \"{type}\"", StringComparison.OrdinalIgnoreCase) == true;

    internal static List<string> BuildArguments(
        ClaudeRunContext ctx,
        string? sessionId,
        bool isResume,
        string model,
        Uri hookEndpoint)
    {
        var args = new List<string> { "exec" };
        if (isResume)
        {
            args.Add("resume");
            args.Add(sessionId ?? throw new ArgumentNullException(nameof(sessionId)));
        }

        args.Add("--json");
        args.Add("--ignore-user-config");
        args.Add("--dangerously-bypass-hook-trust");
        args.Add("--model");
        args.Add(model);

        if (!isResume)
        {
            args.Add("--color");
            args.Add("never");
            args.Add("--sandbox");
            args.Add("workspace-write");
            args.Add("--cd");
            args.Add(ctx.ExecutionPath ?? ctx.WorkspacePath);
        }

        AddConfig(args, "approval_policy=\"never\"");
        AddConfig(args, "sandbox_workspace_write.network_access=true");
        AddConfig(args, "web_search=\"disabled\"");

        var command = $"curl -fsS -H \"Content-Type: application/json\" --data-binary @- {hookEndpoint}";
        AddConfig(args, $"hooks.UserPromptSubmit=[{{hooks=[{{type=\"command\",command='{command}'}}]}}]");
        AddConfig(args, $"hooks.PreToolUse=[{{matcher=\".*\",hooks=[{{type=\"command\",command='{command}'}}]}}]");

        if (ctx.ImagePaths is not null)
        {
            foreach (var path in ctx.ImagePaths.Where(File.Exists))
            {
                args.Add("--image");
                args.Add(path);
            }
        }

        args.Add("-");
        return args;
    }

    private static void AddConfig(List<string> args, string value)
    {
        args.Add("--config");
        args.Add(value);
    }

    private void PublishPolicyObservations(AgentRun run, PolicyHookRunSession policyHooks)
    {
        foreach (var observation in policyHooks.SnapshotObservations())
        {
            var detail = JsonSerializer.Serialize(new
            {
                schema = "policy-violation/v1",
                runId = run.RunId,
                agent = observation.Agent,
                tool = observation.Tool,
                toolUseId = observation.ToolUseId,
                operation = observation.Operation.ToString(),
                target = observation.Target,
                decision = observation.Decision.ToString(),
                reason = observation.Reason,
                enforcementMode = "warn",
                persistence = "run-log",
            });
            run.Push(new StreamEvent(
                observation.ObservedAt,
                "policy-violation",
                $"{observation.Agent} {observation.Tool}: {observation.Reason}",
                detail));
            _logger.LogWarning(
                "POLICY VIOLATION run={RunId} agent={Agent} tool={Tool} operation={Operation} target={Target} decision={Decision} reason={Reason}",
                run.RunId,
                observation.Agent,
                observation.Tool,
                observation.Operation,
                observation.Target,
                observation.Decision,
                observation.Reason);
        }
    }

    private static string ResolveCodexBinary()
    {
        var configured = Environment.GetEnvironmentVariable("GIGACLAW_CODEX_BIN");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var executable = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        var sibling = Path.Combine(AppContext.BaseDirectory, executable);
        if (File.Exists(sibling)) return sibling;
        var tools = Path.Combine(AppContext.BaseDirectory, "tools", executable);
        return File.Exists(tools) ? tools : "codex";
    }
}
