using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GigaClaw.Core.Automation.Policy;

/*
 * Runtime contract capability map (the JSON manifest currently has no separate
 * capability or network field):
 *
 *   code-write              -> code-write, board-write
 *   test-write              -> code-write, board-write
 *   board-write             -> board-write
 *   git-write               -> git-write, board-write
 *   docs-git-write          -> content-write, memory-write, git-write, board-write
 *   memory-write            -> memory-write
 *   content-write/review    -> content-write, board-write
 *   outbound-content        -> content-write, board-write
 *   design-write/review     -> content-write, board-write
 *   research                -> content-write, board-write
 *   read-only-data          -> content-write, board-write
 *   media-* / local-media-* -> content-write, board-write
 *   health-content          -> content-write, board-write
 *   approval                -> board-write
 *   monitoring              -> memory-write, board-write
 *
 * "network" means non-GigaClaw outbound access. Calls to the local GigaClaw API
 * are classified as board writes by the hook adapter. No current contract field
 * grants outbound network access; that capability remains denied until lane CL
 * adds an explicit schema field. Unknown risk classes fail closed; additions to
 * contracts.json must make an intentional mapping change.
 */

[Flags]
public enum ContractCapability
{
    None = 0,
    CodeWrite = 1 << 0,
    BoardWrite = 1 << 1,
    GitWrite = 1 << 2,
    ContentWrite = 1 << 3,
    MemoryWrite = 1 << 4,
    Network = 1 << 5,
}

public enum PolicyToolOperation
{
    FileWrite,
    BoardWrite,
    GitWrite,
    TicketExit,
    Network,
    Bash,
    HookTransport,
}

public enum PolicyDecisionKind
{
    Allow,
    Warn,
    Block,
}

/// <summary>
/// Explicit path comparison mode. The loader defaults conservatively to ordinal
/// matching; it does not claim to detect filesystem format or Git core.ignoreCase.
/// The R2 hook adapter must supply Insensitive only from known repository/filesystem
/// configuration.
/// </summary>
public enum PathCaseSensitivity
{
    Sensitive,
    Insensitive,
}

public sealed record PolicyToolCall(PolicyToolOperation Operation, string? Target = null)
{
    public static PolicyToolCall FileWrite(string path) => new(PolicyToolOperation.FileWrite, path);
    public static PolicyToolCall BoardWrite(string? target = null) => new(PolicyToolOperation.BoardWrite, target);
    public static PolicyToolCall GitWrite(string? command = null) => new(PolicyToolOperation.GitWrite, command);
    public static PolicyToolCall TicketExit(string status) => new(PolicyToolOperation.TicketExit, status);
    public static PolicyToolCall Network(string? target = null) => new(PolicyToolOperation.Network, target);
    public static PolicyToolCall Bash(string? command = null) => new(PolicyToolOperation.Bash, command);
}

public sealed record PolicyDecision(PolicyDecisionKind Kind, string Reason)
{
    public bool IsViolation => Kind != PolicyDecisionKind.Allow;
}

public sealed record ContractPolicyDefaults(
    int MaxDispatchAttempts,
    int RetryBackoffSeconds,
    bool RequireAtomicHandoff,
    bool RequireAuthorOnBoardWrites);

/// <summary>
/// An immutable, per-agent view of the shared contract manifest. A policy can
/// exist in an invalid state so callers always have an evaluator and malformed
/// or missing configuration produces a diagnostic block rather than an
/// exception-driven allow.
/// </summary>
public sealed class ContractPolicy
{
    private const ContractCapability FileWriteCapabilities =
        ContractCapability.CodeWrite |
        ContractCapability.ContentWrite |
        ContractCapability.MemoryWrite;

    private static readonly IReadOnlyDictionary<string, ContractCapability> RiskCapabilities =
        new ReadOnlyDictionary<string, ContractCapability>(
            new Dictionary<string, ContractCapability>(StringComparer.Ordinal)
            {
                ["approval"] = ContractCapability.BoardWrite,
                ["board-write"] = ContractCapability.BoardWrite,
                ["code-write"] = ContractCapability.CodeWrite | ContractCapability.BoardWrite,
                ["content-review"] = ContractCapability.ContentWrite | ContractCapability.BoardWrite,
                ["content-write"] = ContractCapability.ContentWrite | ContractCapability.BoardWrite,
                ["design-review"] = ContractCapability.ContentWrite | ContractCapability.BoardWrite,
                ["design-write"] = ContractCapability.ContentWrite | ContractCapability.BoardWrite,
                ["docs-git-write"] =
                    ContractCapability.ContentWrite |
                    ContractCapability.MemoryWrite |
                    ContractCapability.GitWrite |
                    ContractCapability.BoardWrite,
                ["git-write"] = ContractCapability.GitWrite | ContractCapability.BoardWrite,
                ["health-content"] = ContractCapability.ContentWrite | ContractCapability.BoardWrite,
                ["local-media-composition"] = ContractCapability.ContentWrite | ContractCapability.BoardWrite,
                ["local-media-execution"] = ContractCapability.ContentWrite | ContractCapability.BoardWrite,
                ["local-media-review"] = ContractCapability.ContentWrite | ContractCapability.BoardWrite,
                ["media-direction"] = ContractCapability.ContentWrite | ContractCapability.BoardWrite,
                ["memory-write"] = ContractCapability.MemoryWrite,
                ["monitoring"] =
                    ContractCapability.MemoryWrite |
                    ContractCapability.BoardWrite,
                ["outbound-content"] = ContractCapability.ContentWrite | ContractCapability.BoardWrite,
                ["read-only-data"] = ContractCapability.ContentWrite | ContractCapability.BoardWrite,
                ["research"] =
                    ContractCapability.ContentWrite |
                    ContractCapability.BoardWrite,
                ["test-write"] = ContractCapability.CodeWrite | ContractCapability.BoardWrite,
            });

    private readonly GitIgnoreGlobSet? _writeGlobs;
    private readonly HashSet<string> _ticketExit;
    private readonly string _workspacePath;
    private readonly PathCaseSensitivity _caseSensitivity;

    private ContractPolicy(
        string workspacePath,
        string agentName,
        int? manifestVersion,
        ContractPolicyDefaults? defaults,
        IReadOnlyList<string> dispatches,
        string? riskClass,
        IReadOnlyList<string> allowedWriteGlobs,
        IReadOnlyList<string> ticketExit,
        int? maxReviewCycles,
        ContractCapability capabilities,
        string? diagnostic,
        PathCaseSensitivity caseSensitivity)
    {
        _workspacePath = Path.GetFullPath(workspacePath);
        _caseSensitivity = caseSensitivity;
        AgentName = agentName;
        ManifestVersion = manifestVersion;
        Defaults = defaults;
        Dispatches = dispatches;
        RiskClass = riskClass;
        AllowedWriteGlobs = allowedWriteGlobs;
        TicketExit = ticketExit;
        MaxReviewCycles = maxReviewCycles;
        Capabilities = capabilities;
        Diagnostic = diagnostic;
        _ticketExit = new HashSet<string>(ticketExit, StringComparer.OrdinalIgnoreCase);

        if (diagnostic is null)
            _writeGlobs = new GitIgnoreGlobSet(allowedWriteGlobs, caseSensitivity);
    }

    public string AgentName { get; }
    public int? ManifestVersion { get; }
    public ContractPolicyDefaults? Defaults { get; }
    public IReadOnlyList<string> Dispatches { get; }
    public string? RiskClass { get; }
    public IReadOnlyList<string> AllowedWriteGlobs { get; }
    public IReadOnlyList<string> TicketExit { get; }
    public int? MaxReviewCycles { get; }
    public ContractCapability Capabilities { get; }
    public string? Diagnostic { get; }
    public bool IsValid => Diagnostic is null;

    public PolicyDecision Evaluate(PolicyToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (!IsValid)
            return Block($"Contract policy for agent '{AgentName}' is unavailable: {Diagnostic}");

        return toolCall.Operation switch
        {
            PolicyToolOperation.FileWrite => EvaluateFileWrite(toolCall.Target),
            PolicyToolOperation.BoardWrite => RequireCapability(ContractCapability.BoardWrite, "board write"),
            PolicyToolOperation.GitWrite => RequireCapability(ContractCapability.GitWrite, "git write"),
            PolicyToolOperation.TicketExit => EvaluateTicketExit(toolCall.Target),
            PolicyToolOperation.Network => RequireCapability(ContractCapability.Network, "outbound network access"),
            PolicyToolOperation.Bash => string.IsNullOrWhiteSpace(toolCall.Target)
                ? Block("Bash tool call did not include a command.")
                : Warn("Bash command did not map to a narrower governed capability and requires shadow review."),
            PolicyToolOperation.HookTransport => Block("Hook transport errors are not agent capabilities."),
            _ => Block($"Unknown policy operation '{toolCall.Operation}'."),
        };
    }

    internal static ContractPolicy Invalid(
        string workspacePath,
        string agentName,
        string diagnostic,
        PathCaseSensitivity caseSensitivity) =>
        new(
            workspacePath,
            agentName,
            manifestVersion: null,
            defaults: null,
            Array.Empty<string>(),
            riskClass: null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            maxReviewCycles: null,
            ContractCapability.None,
            diagnostic,
            caseSensitivity);

    internal static ContractPolicy Create(
        string workspacePath,
        string agentName,
        int manifestVersion,
        ContractPolicyDefaults defaults,
        IReadOnlyList<string> dispatches,
        string riskClass,
        IReadOnlyList<string> allowedWriteGlobs,
        IReadOnlyList<string> ticketExit,
        int? maxReviewCycles,
        PathCaseSensitivity caseSensitivity)
    {
        if (!RiskCapabilities.TryGetValue(riskClass, out var capabilities))
        {
            return Invalid(
                workspacePath,
                agentName,
                $"Contract has unknown riskClass '{riskClass}'.",
                caseSensitivity);
        }

        try
        {
            _ = new GitIgnoreGlobSet(allowedWriteGlobs, caseSensitivity);
        }
        catch (ArgumentException ex)
        {
            return Invalid(
                workspacePath,
                agentName,
                $"Contract has an invalid allowedWriteGlobs entry: {ex.Message}",
                caseSensitivity);
        }

        return new(
            workspacePath,
            agentName,
            manifestVersion,
            defaults,
            dispatches,
            riskClass,
            allowedWriteGlobs,
            ticketExit,
            maxReviewCycles,
            capabilities,
            diagnostic: null,
            caseSensitivity);
    }

    private PolicyDecision EvaluateFileWrite(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return Block("File-write tool call did not include a target path.");

        var resolved = WorkspacePathResolver.Resolve(_workspacePath, target, _caseSensitivity);
        if (!resolved.Success)
            return Block(resolved.Diagnostic!);

        var requiredCapability = IsUnder(resolved.RelativePath!, ".git")
            ? ContractCapability.GitWrite
            : IsAgentMemoryPath(resolved.RelativePath!)
                ? ContractCapability.MemoryWrite
                : FileWriteCapabilities;

        if ((Capabilities & requiredCapability) == ContractCapability.None)
        {
            return Warn(
                $"Risk class '{RiskClass}' does not grant a write capability for '{resolved.RelativePath}'.");
        }

        if (!_writeGlobs!.IsMatch(resolved.RelativePath!))
        {
            return Warn(
                $"Path '{resolved.RelativePath}' is outside allowedWriteGlobs for agent '{AgentName}'.");
        }

        return Allow($"Path '{resolved.RelativePath}' matches the agent write contract.");
    }

    private PolicyDecision EvaluateTicketExit(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return Block("Ticket-exit tool call did not include a target status.");

        var board = RequireCapability(ContractCapability.BoardWrite, "board write");
        if (board.Kind != PolicyDecisionKind.Allow)
            return board;

        return _ticketExit.Contains(target)
            ? Allow($"Ticket status '{target}' is permitted by ticketExit.")
            : Warn($"Ticket status '{target}' is outside ticketExit for agent '{AgentName}'.");
    }

    private PolicyDecision RequireCapability(ContractCapability capability, string description) =>
        Capabilities.HasFlag(capability)
            ? Allow($"Risk class '{RiskClass}' permits {description}.")
            : Warn($"Risk class '{RiskClass}' does not permit {description}.");

    private static bool IsUnder(string relativePath, string directory) =>
        string.Equals(relativePath, directory, StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAgentMemoryPath(string relativePath) =>
        relativePath.StartsWith(".agents/", StringComparison.OrdinalIgnoreCase) &&
        relativePath.Contains("/memory/", StringComparison.OrdinalIgnoreCase);

    private static PolicyDecision Allow(string reason) => new(PolicyDecisionKind.Allow, reason);
    private static PolicyDecision Warn(string reason) => new(PolicyDecisionKind.Warn, reason);
    private static PolicyDecision Block(string reason) => new(PolicyDecisionKind.Block, reason);
}

/// <summary>Loads one agent entry without exposing contracts belonging to other agents.</summary>
public static class ContractPolicyLoader
{
    public static Task<ContractPolicy> LoadAsync(
        string workspacePath,
        string agentName,
        CancellationToken cancellationToken = default,
        PathCaseSensitivity caseSensitivity = PathCaseSensitivity.Sensitive) =>
        LoadManifestAsync(
            Path.Combine(workspacePath, ".agents", "contracts.json"),
            workspacePath,
            agentName,
            cancellationToken,
            caseSensitivity);

    public static async Task<ContractPolicy> LoadManifestAsync(
        string manifestPath,
        string workspacePath,
        string agentName,
        CancellationToken cancellationToken = default,
        PathCaseSensitivity caseSensitivity = PathCaseSensitivity.Sensitive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        if (!File.Exists(manifestPath))
        {
            return ContractPolicy.Invalid(
                workspacePath,
                agentName,
                $"Contract manifest was not found at '{manifestPath}'.",
                caseSensitivity);
        }

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            return Parse(
                document.RootElement,
                manifestPath,
                workspacePath,
                agentName,
                caseSensitivity);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            return ContractPolicy.Invalid(
                workspacePath,
                agentName,
                $"Contract manifest '{manifestPath}' is malformed JSON: {ex.Message}",
                caseSensitivity);
        }
        catch (IOException ex)
        {
            return ContractPolicy.Invalid(
                workspacePath,
                agentName,
                $"Contract manifest '{manifestPath}' could not be read: {ex.Message}",
                caseSensitivity);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ContractPolicy.Invalid(
                workspacePath,
                agentName,
                $"Contract manifest '{manifestPath}' could not be read: {ex.Message}",
                caseSensitivity);
        }
    }

    private static ContractPolicy Parse(
        JsonElement root,
        string manifestPath,
        string workspacePath,
        string agentName,
        PathCaseSensitivity caseSensitivity)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Invalid("manifest root must be a JSON object");

        if (!TryReadRequiredInt(root, "version", out var version, out var error, minimum: 1))
            return Invalid(error!);
        if (version != 1)
            return Invalid($"unsupported manifest version '{version}'");

        if (!TryGetPropertyIgnoreCase(root, "defaults", out var defaultsElement))
            return Invalid("required property 'defaults' is missing");
        if (defaultsElement.ValueKind != JsonValueKind.Object)
            return Invalid("required property 'defaults' must be a JSON object");
        if (!TryReadRequiredInt(
                defaultsElement,
                "maxDispatchAttempts",
                out var maxDispatchAttempts,
                out error,
                minimum: 1))
            return Invalid(error!);
        if (!TryReadRequiredInt(
                defaultsElement,
                "retryBackoffSeconds",
                out var retryBackoffSeconds,
                out error,
                minimum: 0))
            return Invalid(error!);
        if (!TryReadRequiredBoolean(
                defaultsElement,
                "requireAtomicHandoff",
                out var requireAtomicHandoff,
                out error))
            return Invalid(error!);
        if (!TryReadRequiredBoolean(
                defaultsElement,
                "requireAuthorOnBoardWrites",
                out var requireAuthorOnBoardWrites,
                out error))
            return Invalid(error!);
        var defaults = new ContractPolicyDefaults(
            maxDispatchAttempts,
            retryBackoffSeconds,
            requireAtomicHandoff,
            requireAuthorOnBoardWrites);

        if (!TryGetPropertyIgnoreCase(root, "agents", out var agents))
            return Invalid("required property 'agents' is missing");
        if (agents.ValueKind != JsonValueKind.Object)
            return Invalid("'agents' must be a JSON object");

        if (!TryGetPropertyIgnoreCase(agents, agentName, out var entry))
            return Invalid($"entry for agent '{agentName}' is missing");
        if (entry.ValueKind != JsonValueKind.Object)
            return Invalid($"entry for agent '{agentName}' must be a JSON object");

        if (!TryReadRequiredStringArray(
                entry,
                "dispatches",
                out var dispatches,
                out error,
                requireNonEmpty: true))
            return Invalid(error!);
        if (!TryReadRequiredString(entry, "riskClass", out var riskClass, out error))
            return Invalid(error!);
        if (!TryReadRequiredStringArray(entry, "allowedWriteGlobs", out var globs, out error))
            return Invalid(error!);
        if (!TryReadRequiredStringArray(entry, "ticketExit", out var ticketExit, out error))
            return Invalid(error!);

        int? maxReviewCycles = null;
        if (TryGetPropertyIgnoreCase(entry, "maxReviewCycles", out var maxReviewCyclesElement))
        {
            if (maxReviewCyclesElement.ValueKind != JsonValueKind.Number ||
                !maxReviewCyclesElement.TryGetInt32(out var parsedMaxReviewCycles) ||
                parsedMaxReviewCycles < 1)
            {
                return Invalid("optional property 'maxReviewCycles' must be a positive integer");
            }
            maxReviewCycles = parsedMaxReviewCycles;
        }

        return ContractPolicy.Create(
            workspacePath,
            agentName,
            version,
            defaults,
            dispatches!,
            riskClass!,
            globs!,
            ticketExit!,
            maxReviewCycles,
            caseSensitivity);

        ContractPolicy Invalid(string diagnostic) =>
            ContractPolicy.Invalid(
                workspacePath,
                agentName,
                $"Contract manifest '{manifestPath}': {diagnostic}.",
                caseSensitivity);
    }

    private static bool TryReadRequiredString(
        JsonElement entry,
        string name,
        out string? value,
        out string? error)
    {
        value = null;
        if (!TryGetPropertyIgnoreCase(entry, name, out var property))
        {
            error = $"required property '{name}' is missing";
            return false;
        }
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            error = $"required property '{name}' must be a non-empty string";
            return false;
        }

        value = property.GetString()!;
        error = null;
        return true;
    }

    private static bool TryReadRequiredStringArray(
        JsonElement entry,
        string name,
        out IReadOnlyList<string>? values,
        out string? error,
        bool requireNonEmpty = false)
    {
        values = null;
        if (!TryGetPropertyIgnoreCase(entry, name, out var property))
        {
            error = $"required property '{name}' is missing";
            return false;
        }
        if (property.ValueKind != JsonValueKind.Array)
        {
            error = $"required property '{name}' must be an array";
            return false;
        }

        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                error = $"property '{name}' must contain only non-empty strings";
                return false;
            }
            result.Add(item.GetString()!);
        }

        if (requireNonEmpty && result.Count == 0)
        {
            error = $"property '{name}' must contain at least one value";
            return false;
        }

        values = result.AsReadOnly();
        error = null;
        return true;
    }

    private static bool TryReadRequiredInt(
        JsonElement entry,
        string name,
        out int value,
        out string? error,
        int minimum)
    {
        value = default;
        if (!TryGetPropertyIgnoreCase(entry, name, out var property))
        {
            error = $"required property '{name}' is missing";
            return false;
        }
        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out value) ||
            value < minimum)
        {
            error = $"required property '{name}' must be an integer greater than or equal to {minimum}";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadRequiredBoolean(
        JsonElement entry,
        string name,
        out bool value,
        out string? error)
    {
        value = default;
        if (!TryGetPropertyIgnoreCase(entry, name, out var property))
        {
            error = $"required property '{name}' is missing";
            return false;
        }
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = $"required property '{name}' must be a boolean";
            return false;
        }

        value = property.GetBoolean();
        error = null;
        return true;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

internal sealed class GitIgnoreGlobSet
{
    private readonly IReadOnlyList<GlobRule> _rules;

    public GitIgnoreGlobSet(
        IEnumerable<string> patterns,
        PathCaseSensitivity caseSensitivity)
    {
        _rules = patterns.Select(pattern => GlobRule.Parse(pattern, caseSensitivity)).ToArray();
    }

    /// <summary>
    /// Matches the gitignore features used by contracts: slash-rooted paths,
    /// basename-only patterns, *, ?, **, character classes, directory suffixes,
    /// ordered rules, and ! exclusions. This deliberately does not implement
    /// gitignore's whitespace escaping because JSON array entries preserve their
    /// boundaries and contract globs may not contain leading/trailing whitespace.
    /// </summary>
    public bool IsMatch(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var allowed = false;
        foreach (var rule in _rules)
        {
            if (rule.Regex.IsMatch(normalized))
                allowed = !rule.Negated;
        }
        return allowed;
    }

    private sealed record GlobRule(bool Negated, Regex Regex)
    {
        public static GlobRule Parse(string rawPattern, PathCaseSensitivity caseSensitivity)
        {
            if (string.IsNullOrWhiteSpace(rawPattern))
                throw new ArgumentException("Glob may not be empty.");
            if (!string.Equals(rawPattern, rawPattern.Trim(), StringComparison.Ordinal))
                throw new ArgumentException($"Glob '{rawPattern}' has leading or trailing whitespace.");

            var negated = rawPattern.StartsWith('!');
            var pattern = negated ? rawPattern[1..] : rawPattern;
            if (pattern.Length == 0)
                throw new ArgumentException("Negated glob may not be empty.");

            pattern = pattern.Replace('\\', '/');
            var anchored = pattern.StartsWith('/');
            if (anchored)
                pattern = pattern[1..];
            if (pattern.Length == 0)
                throw new ArgumentException("Root-only glob is not a writable file pattern.");

            var directoryOnly = pattern.EndsWith('/');
            if (directoryOnly)
                pattern += "**";

            var segments = pattern.Split('/');
            if (segments.Any(segment => segment == ".."))
                throw new ArgumentException($"Glob '{rawPattern}' may not contain '..' path segments.");

            var hasSlash = pattern.Contains('/');
            var prefix = anchored || hasSlash ? "^" : "^(?:.*/)?";
            var regex = prefix + ToRegexBody(pattern) + "$";
            var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
            if (caseSensitivity == PathCaseSensitivity.Insensitive)
                options |= RegexOptions.IgnoreCase;
            return new GlobRule(negated, new Regex(regex, options, TimeSpan.FromMilliseconds(25)));
        }

        private static string ToRegexBody(string pattern)
        {
            var result = new System.Text.StringBuilder();
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                switch (c)
                {
                    case '*':
                    {
                        var isDouble = i + 1 < pattern.Length && pattern[i + 1] == '*';
                        if (!isDouble)
                        {
                            result.Append("[^/]*");
                            break;
                        }

                        i++;
                        while (i + 1 < pattern.Length && pattern[i + 1] == '*')
                            i++;

                        if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                        {
                            i++;
                            result.Append("(?:.*/)?");
                        }
                        else
                        {
                            result.Append(".*");
                        }
                        break;
                    }
                    case '?':
                        result.Append("[^/]");
                        break;
                    case '[':
                    {
                        var close = pattern.IndexOf(']', i + 1);
                        if (close < 0)
                            throw new ArgumentException($"Glob '{pattern}' has an unclosed character class.");
                        var content = pattern[(i + 1)..close];
                        if (content.Length == 0)
                            throw new ArgumentException($"Glob '{pattern}' has an empty character class.");
                        result.Append('[');
                        if (content[0] == '!')
                        {
                            result.Append('^');
                            content = content[1..];
                        }
                        result.Append(content.Replace("\\", "\\\\", StringComparison.Ordinal));
                        result.Append(']');
                        i = close;
                        break;
                    }
                    default:
                        result.Append(Regex.Escape(c.ToString()));
                        break;
                }
            }
            return result.ToString();
        }

    }
}

internal sealed record WorkspacePathResolution(
    bool Success,
    string? RelativePath,
    string? Diagnostic);

internal static class WorkspacePathResolver
{
    public static WorkspacePathResolution Resolve(
        string workspacePath,
        string target,
        PathCaseSensitivity caseSensitivity)
    {
        try
        {
            if (target.IndexOf('\0') >= 0)
                return Failure("Target path contains a NUL character.");

            var comparison = ResolveComparison(caseSensitivity);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
            var lexicalTarget = Path.GetFullPath(target, root);
            if (!IsWithin(root, lexicalTarget, comparison) ||
                string.Equals(root, lexicalTarget, comparison))
            {
                return Failure($"Target path '{target}' escapes workspace '{root}'.");
            }

            var lexicalRelative = Path.GetRelativePath(root, lexicalTarget);
            var canonicalRoot = ResolveRoot(root);
            var resolvedTarget = ResolveExistingLinks(canonicalRoot, lexicalRelative, comparison);
            if (!resolvedTarget.Success)
                return resolvedTarget;

            var canonicalTarget = resolvedTarget.RelativePath!;
            if (!IsWithin(canonicalRoot, canonicalTarget, comparison) ||
                string.Equals(canonicalRoot, canonicalTarget, comparison))
            {
                return Failure($"Target path '{target}' resolves through a link outside workspace '{root}'.");
            }

            var relative = Path.GetRelativePath(canonicalRoot, canonicalTarget).Replace('\\', '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
                return Failure($"Target path '{target}' escapes workspace '{root}'.");

            return new WorkspacePathResolution(true, relative, null);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            NotSupportedException or
            PathTooLongException or
            IOException or
            UnauthorizedAccessException)
        {
            return Failure($"Target path '{target}' is invalid: {ex.Message}");
        }
    }

    private static string ResolveRoot(string root)
    {
        var info = new DirectoryInfo(root);
        return info.LinkTarget is null
            ? root
            : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new IOException($"Workspace root '{root}' is a broken symbolic link.");
    }

    private static WorkspacePathResolution ResolveExistingLinks(
        string canonicalRoot,
        string lexicalRelative,
        StringComparison comparison)
    {
        var current = canonicalRoot;
        foreach (var segment in lexicalRelative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            if (info?.LinkTarget is null)
                continue;

            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is null)
                return Failure($"Target path crosses broken symbolic link '{current}'.");

            current = Path.GetFullPath(resolved.FullName);
            if (!IsWithin(canonicalRoot, current, comparison))
                return Failure($"Target path resolves through symbolic link '{info.FullName}' outside workspace.");
        }

        return new WorkspacePathResolution(true, Path.GetFullPath(current), null);
    }

    private static bool IsWithin(string root, string target, StringComparison comparison)
    {
        if (string.Equals(root, target, comparison))
            return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return target.StartsWith(prefix, comparison);
    }

    private static StringComparison ResolveComparison(PathCaseSensitivity sensitivity)
    {
        return sensitivity == PathCaseSensitivity.Insensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private static WorkspacePathResolution Failure(string diagnostic) =>
        new(false, null, diagnostic);
}
