using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GigaClaw.Core.Automation.Policy;

public sealed record PolicyHookObservation(
    long Sequence,
    DateTime ObservedAt,
    string Agent,
    string Tool,
    string? ToolUseId,
    PolicyToolOperation Operation,
    string? Target,
    PolicyDecisionKind Decision,
    string Reason);

/// <summary>
/// Run-scoped loopback HTTP bridge for Claude hooks. It is deliberately
/// shadow-only: every authenticated request receives HTTP 200 with the valid
/// empty hook result <c>{}</c>; policy decisions are observations, never denials.
/// </summary>
public sealed class PolicyHookTransport : IAsyncDisposable
{
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();
    private static readonly byte[] OkResponse = Encoding.ASCII.GetBytes(
        "HTTP/1.1 200 OK\r\n" +
        "Content-Type: application/json\r\n" +
        "Content-Length: 2\r\n" +
        "Connection: close\r\n\r\n{}");
    private static readonly byte[] NotFoundResponse = Encoding.ASCII.GetBytes(
        "HTTP/1.1 404 Not Found\r\n" +
        "Content-Type: application/json\r\n" +
        "Content-Length: 2\r\n" +
        "Connection: close\r\n\r\n{}");

    private readonly ContractPolicy _policy;
    private readonly TcpListener _listener;
    private readonly string _path;
    private readonly CancellationTokenSource _acceptShutdown = new();
    private readonly SemaphoreSlim _clientSlots = new(32, 32);
    private readonly ConcurrentQueue<PolicyHookObservation> _observations = new();
    private readonly ConcurrentDictionary<int, Task> _requests = new();
    private readonly Task _acceptLoop;
    private long _sequence;
    private long _acknowledgementCount;
    private int _requestId;
    private int _disposed;

    private PolicyHookTransport(ContractPolicy policy)
    {
        _policy = policy;
        _path = $"/policy/{Guid.NewGuid():N}";
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}{_path}");
        _acceptLoop = AcceptLoopAsync();
    }

    public Uri Endpoint { get; }
    public long AcknowledgementCount => Interlocked.Read(ref _acknowledgementCount);
    public bool WasAcknowledged => AcknowledgementCount > 0;
    public IReadOnlyList<PolicyHookObservation> SnapshotObservations() =>
        _observations.OrderBy(item => item.Sequence).ToArray();

    public static PolicyHookTransport Start(ContractPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new PolicyHookTransport(policy);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _acceptShutdown.Cancel();
        _listener.Stop();
        try { await _acceptLoop; } catch (OperationCanceledException) { }
        var requests = _requests.Values.ToArray();
        if (requests.Length > 0)
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(6));
        _clientSlots.Dispose();
        _acceptShutdown.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_acceptShutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_acceptShutdown.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (_acceptShutdown.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await _clientSlots.WaitAsync(_acceptShutdown.Token);
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                break;
            }

            var id = Interlocked.Increment(ref _requestId);
            var task = HandleClientWithTimeoutAsync(client);
            _requests[id] = task;
            _ = task.ContinueWith(
                _ => _requests.TryRemove(id, out var ignored),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleClientWithTimeoutAsync(TcpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await HandleClientAsync(client, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // A stalled local client is abandoned without blocking run finalization.
        }
        finally
        {
            _clientSlots.Release();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            try
            {
                var request = await ReadRequestAsync(stream, cancellationToken);
                if (!string.Equals(request.Path, _path, StringComparison.Ordinal))
                {
                    await stream.WriteAsync(NotFoundResponse, cancellationToken);
                    return;
                }

                Observe(request.Body);
                await stream.WriteAsync(OkResponse, cancellationToken);
            }
            catch (Exception ex) when (
                ex is InvalidDataException or
                JsonException or
                IOException or
                DecoderFallbackException)
            {
                EnqueueMalformed(ex.Message);
                try { await stream.WriteAsync(OkResponse, cancellationToken); } catch { }
            }
        }
    }

    private void Observe(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Hook input root must be a JSON object.");

        var hookEvent = RequiredString(root, "hook_event_name");
        if (string.Equals(hookEvent, "UserPromptSubmit", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _acknowledgementCount);
            return;
        }
        if (!string.Equals(hookEvent, "PreToolUse", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Hook input hook_event_name must be 'UserPromptSubmit' or 'PreToolUse'.");
        }

        var tool = RequiredString(root, "tool_name");
        var toolUseId = OptionalString(root, "tool_use_id");
        if (!root.TryGetProperty("tool_input", out var toolInput) ||
            toolInput.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Hook input tool_input must be a JSON object.");
        }

        Interlocked.Increment(ref _acknowledgementCount);

        foreach (var evaluation in PolicyHookToolCallAdapter.Evaluate(_policy, tool, toolInput))
        {
            if (!evaluation.Decision.IsViolation)
                continue;

            _observations.Enqueue(new PolicyHookObservation(
                Interlocked.Increment(ref _sequence),
                DateTime.UtcNow,
                _policy.AgentName,
                tool,
                toolUseId,
                evaluation.Call.Operation,
                evaluation.Call.Target,
                evaluation.Decision.Kind,
                evaluation.Decision.Reason));
        }
    }

    private void EnqueueMalformed(string reason)
    {
        _observations.Enqueue(new PolicyHookObservation(
            Interlocked.Increment(ref _sequence),
            DateTime.UtcNow,
            _policy.AgentName,
            "unknown",
            null,
            PolicyToolOperation.HookTransport,
            null,
            PolicyDecisionKind.Block,
            $"Malformed policy hook transport/input: {reason}"));
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Hook input {name} must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<HttpRequest> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>(4096);
        var rented = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var headerOffset = -1;
            while (headerOffset < 0)
            {
                var read = await stream.ReadAsync(rented, cancellationToken);
                if (read == 0)
                    throw new InvalidDataException("Hook HTTP request ended before its headers.");
                writer.Write(rented.AsSpan(0, read));
                if (writer.WrittenCount > MaxHeaderBytes + MaxBodyBytes)
                    throw new InvalidDataException("Hook HTTP request exceeds the maximum size.");
                headerOffset = writer.WrittenSpan.IndexOf(HeaderTerminator);
                if (headerOffset < 0 && writer.WrittenCount > MaxHeaderBytes)
                    throw new InvalidDataException("Hook HTTP headers exceed the maximum size.");
            }

            var headerEnd = headerOffset + HeaderTerminator.Length;
            var headerText = Encoding.ASCII.GetString(writer.WrittenSpan[..headerOffset]);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestLine = lines.FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestLine is not { Length: >= 2 } ||
                !string.Equals(requestLine[0], "POST", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Hook HTTP request must use POST.");
            }

            var contentLength = ParseContentLength(lines);
            var isChunked = HasChunkedTransferEncoding(lines);
            if (contentLength is null && !isChunked)
                throw new InvalidDataException(
                    "Hook HTTP request requires Content-Length or chunked transfer encoding.");
            if (contentLength is < 0 or > MaxBodyBytes)
                throw new InvalidDataException("Hook HTTP Content-Length is invalid.");

            if (isChunked)
            {
                string? chunkedBody;
                while (!TryDecodeChunked(
                           writer.WrittenSpan[headerEnd..],
                           out chunkedBody))
                {
                    var read = await stream.ReadAsync(rented, cancellationToken);
                    if (read == 0)
                        throw new InvalidDataException("Chunked hook HTTP request body ended early.");
                    writer.Write(rented.AsSpan(0, read));
                    if (writer.WrittenCount > MaxHeaderBytes + MaxBodyBytes)
                        throw new InvalidDataException("Hook HTTP request exceeds the maximum size.");
                }
                return new HttpRequest(requestLine[1], chunkedBody!);
            }

            var requiredBytes = headerEnd + contentLength!.Value;
            while (writer.WrittenCount < requiredBytes)
            {
                var read = await stream.ReadAsync(rented, cancellationToken);
                if (read == 0)
                    throw new InvalidDataException("Hook HTTP request body ended early.");
                writer.Write(rented.AsSpan(0, read));
                if (writer.WrittenCount > MaxHeaderBytes + MaxBodyBytes)
                    throw new InvalidDataException("Hook HTTP request exceeds the maximum size.");
            }
            var body = Encoding.UTF8.GetString(
                writer.WrittenSpan.Slice(headerEnd, contentLength.Value));
            return new HttpRequest(requestLine[1], body);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int? ParseContentLength(string[] headerLines)
    {
        foreach (var line in headerLines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            if (!string.Equals(
                    line[..colon].Trim(),
                    "Content-Length",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return int.TryParse(line[(colon + 1)..].Trim(), out var length)
                ? length
                : -1;
        }
        return null;
    }

    private static bool HasChunkedTransferEncoding(string[] headerLines)
    {
        foreach (var line in headerLines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 ||
                !string.Equals(
                    line[..colon].Trim(),
                    "Transfer-Encoding",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return line[(colon + 1)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains("chunked", StringComparer.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool TryDecodeChunked(
        ReadOnlySpan<byte> encoded,
        out string? body)
    {
        body = null;
        using var decoded = new MemoryStream();
        var offset = 0;

        while (true)
        {
            var lineEnd = encoded[offset..].IndexOf("\r\n"u8);
            if (lineEnd < 0)
                return false;
            var sizeText = Encoding.ASCII.GetString(encoded.Slice(offset, lineEnd));
            var extension = sizeText.IndexOf(';');
            if (extension >= 0)
                sizeText = sizeText[..extension];
            if (!int.TryParse(
                    sizeText.Trim(),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var chunkSize) ||
                chunkSize < 0)
            {
                throw new InvalidDataException("Hook HTTP chunk size is invalid.");
            }

            offset += lineEnd + 2;
            if (chunkSize == 0)
            {
                if (encoded.Length < offset + 2)
                    return false;
                if (encoded[offset] != '\r' || encoded[offset + 1] != '\n')
                    throw new InvalidDataException("Hook HTTP chunk terminator is invalid.");
                body = Encoding.UTF8.GetString(decoded.ToArray());
                return true;
            }

            if (decoded.Length + chunkSize > MaxBodyBytes)
                throw new InvalidDataException("Hook HTTP chunked body exceeds the maximum size.");
            if (encoded.Length < offset + chunkSize + 2)
                return false;

            decoded.Write(encoded.Slice(offset, chunkSize));
            offset += chunkSize;
            if (encoded[offset] != '\r' || encoded[offset + 1] != '\n')
                throw new InvalidDataException("Hook HTTP chunk terminator is invalid.");
            offset += 2;
        }
    }

    private sealed record HttpRequest(string Path, string Body);
}

internal sealed record PolicyHookEvaluation(
    PolicyToolCall Call,
    PolicyDecision Decision);

internal static partial class PolicyHookToolCallAdapter
{
    private static readonly HashSet<string> GitWriteCommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "add", "am", "apply", "branch", "checkout", "cherry-pick", "clean",
            "commit", "merge", "mv", "push", "rebase", "reset", "restore",
            "revert", "rm", "switch", "tag",
        };

    public static IReadOnlyList<PolicyHookEvaluation> Evaluate(
        ContractPolicy policy,
        string tool,
        JsonElement toolInput)
    {
        var calls = tool switch
        {
            "Write" or "Edit" => SinglePath(toolInput, "file_path"),
            "NotebookEdit" => SinglePath(toolInput, "notebook_path"),
            "WebFetch" => [PolicyToolCall.Network(OptionalValue(toolInput, "url"))],
            "WebSearch" => [PolicyToolCall.Network(OptionalValue(toolInput, "query"))],
            "Bash" => ClassifyBash(OptionalValue(toolInput, "command")),
            _ => Array.Empty<PolicyToolCall>(),
        };

        return calls
            .Distinct()
            .Select(call => new PolicyHookEvaluation(call, policy.Evaluate(call)))
            .ToArray();
    }

    private static IReadOnlyList<PolicyToolCall> SinglePath(
        JsonElement toolInput,
        string name)
    {
        var value = OptionalValue(toolInput, name);
        return value is null
            ? [PolicyToolCall.FileWrite("")]
            : [PolicyToolCall.FileWrite(value)];
    }

    private static IReadOnlyList<PolicyToolCall> ClassifyBash(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return [PolicyToolCall.Bash(null)];

        var calls = new List<PolicyToolCall>();
        foreach (var segment in TokenizeSegments(command))
            ClassifySegment(segment, command, calls);

        foreach (Match match in UrlRegex().Matches(command))
        {
            var target = match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '"', '\'');
            if (IsLoopbackOrGigaClaw(target))
            {
                if (LooksLikeMutation(command))
                    calls.Add(PolicyToolCall.BoardWrite(target));
            }
            else
            {
                calls.Add(PolicyToolCall.Network(target));
            }
        }

        if (command.Contains("GIGACLAW_API_URL", StringComparison.Ordinal) &&
            LooksLikeMutation(command))
        {
            calls.Add(PolicyToolCall.BoardWrite("${GIGACLAW_API_URL}"));
        }

        foreach (Match match in RedirectRegex().Matches(command))
        {
            var path = FirstGroupValue(match, "double", "single", "bare");
            if (!string.IsNullOrWhiteSpace(path) && !IsNullDevice(path))
                calls.Add(PolicyToolCall.FileWrite(path));
        }
        foreach (Match match in DirectFileWriteRegex().Matches(command))
        {
            var path = FirstGroupValue(match, "double", "single", "bare");
            if (!string.IsNullOrWhiteSpace(path) && !IsNullDevice(path))
                calls.Add(PolicyToolCall.FileWrite(path));
        }

        if (calls.Count == 0)
            calls.Add(PolicyToolCall.Bash(command));
        return calls;
    }

    private static void ClassifySegment(
        IReadOnlyList<string> tokens,
        string command,
        List<PolicyToolCall> calls)
    {
        var index = SkipWrappersAndAssignments(tokens, 0);
        if (index >= tokens.Count)
            return;

        var executable = Path.GetFileName(tokens[index]);
        if (executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            executable = executable[..^4];
        executable = executable.ToLowerInvariant();
        var args = tokens.Skip(index + 1).ToArray();

        if (string.Equals(executable, "git", StringComparison.OrdinalIgnoreCase))
        {
            var verb = FindGitVerb(args);
            if (verb is not null && GitWriteCommands.Contains(verb))
                calls.Add(PolicyToolCall.GitWrite(command));
            return;
        }

        if (executable is "cp" or "mv" or "install")
        {
            var target = LastPositional(args);
            if (target is not null)
                calls.Add(PolicyToolCall.FileWrite(target));
            return;
        }

        if (string.Equals(executable, "sed", StringComparison.OrdinalIgnoreCase) &&
            args.Any(arg => arg == "-i" || arg.StartsWith("-i", StringComparison.Ordinal)))
        {
            var target = LastPositional(args);
            if (target is not null)
                calls.Add(PolicyToolCall.FileWrite(target));
            return;
        }

        if (executable is "touch" or "rm" or "tee")
        {
            foreach (var target in args.Where(arg => !arg.StartsWith('-')))
                if (!IsNullDevice(target))
                    calls.Add(PolicyToolCall.FileWrite(target));
            return;
        }

        if (executable is "curl" or "wget")
        {
            var target = args.FirstOrDefault(IsNetworkTarget) ?? command;
            if (IsLoopbackOrGigaClaw(target))
            {
                if (LooksLikeMutation(command))
                    calls.Add(PolicyToolCall.BoardWrite(target));
            }
            else
            {
                calls.Add(PolicyToolCall.Network(target));
            }
            return;
        }

        if (executable is "ssh" or "scp" or "sftp")
        {
            calls.Add(PolicyToolCall.Network(args.FirstOrDefault() ?? command));
            return;
        }

        if (IsPackageManagerNetworkCall(executable, args))
        {
            calls.Add(PolicyToolCall.Network(command));
        }
    }

    private static int SkipWrappersAndAssignments(
        IReadOnlyList<string> tokens,
        int start)
    {
        var index = start;
        while (index < tokens.Count)
        {
            var token = Path.GetFileName(tokens[index]);
            if (IsEnvironmentAssignment(tokens[index]))
            {
                index++;
                continue;
            }
            if (string.Equals(token, "env", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                while (index < tokens.Count &&
                       (tokens[index].StartsWith('-') ||
                        IsEnvironmentAssignment(tokens[index])))
                {
                    index++;
                }
                continue;
            }
            if (string.Equals(token, "sudo", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                while (index < tokens.Count && tokens[index].StartsWith('-'))
                {
                    var option = tokens[index++];
                    if ((option is "-u" or "-g" or "-h" or "-p") &&
                        index < tokens.Count)
                    {
                        index++;
                    }
                }
                continue;
            }
            break;
        }
        return index;
    }

    private static string? FindGitVerb(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace")
            {
                i++;
                continue;
            }
            if (arg.StartsWith("--git-dir=", StringComparison.Ordinal) ||
                arg.StartsWith("--work-tree=", StringComparison.Ordinal) ||
                arg.StartsWith("--namespace=", StringComparison.Ordinal) ||
                arg.StartsWith("-c", StringComparison.Ordinal) && arg.Length > 2)
            {
                continue;
            }
            if (arg.StartsWith('-'))
                continue;
            return arg;
        }
        return null;
    }

    private static string? LastPositional(IReadOnlyList<string> args)
    {
        for (var i = args.Count - 1; i >= 0; i--)
        {
            if (!args[i].StartsWith('-'))
                return args[i];
        }
        return null;
    }

    private static bool IsPackageManagerNetworkCall(
        string executable,
        IReadOnlyList<string> args)
    {
        var verb = (args.FirstOrDefault(arg => !arg.StartsWith('-')) ?? "")
            .ToLowerInvariant();
        return executable switch
        {
            "npm" or "pnpm" or "yarn" or "bun" =>
                verb is "add" or "install" or "update" or "upgrade",
            "pip" or "pip3" or "uv" =>
                verb is "install" or "sync",
            "dotnet" =>
                verb is "restore" ||
                verb == "add" && args.Contains("package", StringComparer.OrdinalIgnoreCase),
            "nuget" =>
                verb is "install" or "restore" or "update",
            "apt" or "apt-get" or "apk" or "dnf" or "yum" or "brew" =>
                verb is "add" or "install" or "update" or "upgrade",
            "gem" or "cargo" or "go" =>
                verb is "install" or "get",
            _ => false,
        };
    }

    private static bool IsEnvironmentAssignment(string token)
    {
        var equals = token.IndexOf('=');
        if (equals <= 0)
            return false;
        return token[..equals].All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static bool IsNetworkTarget(string token) =>
        token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        token.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        token.StartsWith('$') ||
        token.Contains("GIGACLAW_API_URL", StringComparison.Ordinal);

    private static IReadOnlyList<IReadOnlyList<string>> TokenizeSegments(string command)
    {
        var segments = new List<IReadOnlyList<string>>();
        var tokens = new List<string>();
        var token = new StringBuilder();
        char quote = '\0';
        var escaped = false;

        void FlushToken()
        {
            if (token.Length == 0)
                return;
            tokens.Add(token.ToString());
            token.Clear();
        }

        void FlushSegment()
        {
            FlushToken();
            if (tokens.Count == 0)
                return;
            segments.Add(tokens.ToArray());
            tokens.Clear();
        }

        foreach (var c in command)
        {
            if (escaped)
            {
                token.Append(c);
                escaped = false;
                continue;
            }
            if (c == '\\' && quote != '\'')
            {
                escaped = true;
                continue;
            }
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    token.Append(c);
                continue;
            }
            if (c is '\'' or '"')
            {
                quote = c;
                continue;
            }
            if (c is ';' or '|' or '&' or '\n')
            {
                FlushSegment();
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                FlushToken();
                continue;
            }
            token.Append(c);
        }
        if (escaped)
            token.Append('\\');
        FlushSegment();
        return segments;
    }

    private static string? OptionalValue(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FirstGroupValue(Match match, params string[] names)
    {
        foreach (var name in names)
        {
            if (match.Groups[name].Success)
                return match.Groups[name].Value;
        }
        return "";
    }

    private static bool LooksLikeMutation(string command) =>
        DataFlagRegex().IsMatch(command) ||
        MethodFlagRegex().IsMatch(command);

    private static bool IsLoopbackOrGigaClaw(string target) =>
        target.Contains("GIGACLAW_API_URL", StringComparison.Ordinal) ||
        Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private static bool IsNullDevice(string path) =>
        string.Equals(path, "/dev/null", StringComparison.Ordinal) ||
        string.Equals(path, "NUL", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"https?://[^\s<>]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?:^|[\s;&|])(?:\d*>>?|&>)\s*(?:""(?<double>[^""]+)""|'(?<single>[^']+)'|(?<bare>[^\s;&|]+))",
        RegexOptions.CultureInvariant)]
    private static partial Regex RedirectRegex();

    [GeneratedRegex(@"(?:^|[\s;&|])(?:touch|rm|tee)\s+(?:--\s+)?(?:""(?<double>[^""]+)""|'(?<single>[^']+)'|(?<bare>[^\s;&|]+))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DirectFileWriteRegex();

    [GeneratedRegex(@"(?:^|\s)(?:-d|--data(?:-raw|-binary|-urlencode)?)\s",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DataFlagRegex();

    [GeneratedRegex(@"(?:^|\s)(?:-X|--request)\s*(?:POST|PUT|PATCH|DELETE)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MethodFlagRegex();
}

public sealed class PolicyHookRunSession : IAsyncDisposable
{
    private readonly PolicyHookTransport _transport;
    private int _transportStopped;

    private PolicyHookRunSession(PolicyHookTransport transport, string settingsPath)
    {
        _transport = transport;
        SettingsPath = settingsPath;
    }

    public string SettingsPath { get; }
    public long AcknowledgementCount => _transport.AcknowledgementCount;
    public bool WasAcknowledged => _transport.WasAcknowledged;
    public IReadOnlyList<PolicyHookObservation> SnapshotObservations() =>
        _transport.SnapshotObservations();

    public static async Task<PolicyHookRunSession> StartAsync(
        ContractPolicy policy,
        string runId,
        CancellationToken cancellationToken = default)
    {
        var transport = PolicyHookTransport.Start(policy);
        try
        {
            var settingsPath = await ClaudeHookSettings.WriteValidatedFileAsync(
                runId,
                transport.Endpoint,
                cancellationToken);
            return new PolicyHookRunSession(transport, settingsPath);
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAndDrainAsync();
        try
        {
            if (File.Exists(SettingsPath))
                File.Delete(SettingsPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task StopAndDrainAsync()
    {
        if (Interlocked.Exchange(ref _transportStopped, 1) == 1)
            return;
        await _transport.DisposeAsync();
    }
}
