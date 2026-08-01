using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GigaClaw.Core.Automation.Workflow;

/// <summary>What one walk receipt records.</summary>
public enum WorkflowWalkEvent
{
    /// <summary>A walk was opened on this ticket. Nothing has been entered yet.</summary>
    Started,
    /// <summary>The walk entered a state. Open until a <see cref="Left"/> with the same step number.</summary>
    Entered,
    /// <summary>The walk left the state it was in, naming the outcome it routed on and the target.</summary>
    Left,
    /// <summary>The walk stopped because it could not decide. Terminal, and always with a reason.</summary>
    Parked,
    /// <summary>The walk reached a terminal state. Terminal.</summary>
    Finished
}

/// <summary>Where a walk stands, as replayed from the ticket's comments.</summary>
public enum WorkflowWalkStatus
{
    /// <summary>No walk has ever been opened on this ticket.</summary>
    NotStarted,
    /// <summary>A walk is open: either between states, or waiting inside one.</summary>
    Running,
    /// <summary>The walk fell over an undecidable transition and is waiting for the owner.</summary>
    Parked,
    /// <summary>The walk reached a terminal state.</summary>
    Finished
}

/// <summary>
/// One receipt of a walk. This is the whole durable model: there is no walker table and no walker
/// memory, so everything a resume needs has to be in here.
/// </summary>
public sealed record WorkflowWalkStep(int Step, WorkflowWalkEvent Event, string State)
{
    public WorkflowStateKind? Kind { get; init; }

    /// <summary>Role that handled this traversal — visited-role tracking, recorded durably.</summary>
    public string? Role { get; init; }

    /// <summary>Label the transition routed on: a gate outcome, or <c>DONE</c> for a finished task.</summary>
    public string? Outcome { get; init; }

    /// <summary>State the walk moved to. Only on <see cref="WorkflowWalkEvent.Left"/>.</summary>
    public string? To { get; init; }

    /// <summary>Why the walk parked. Only on <see cref="WorkflowWalkEvent.Parked"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Ticket the state's work happens on: a task state's sub-ticket, and for every other kind the
    /// subject it inherited. This is what a gate is evaluated against, which is why it is recorded
    /// rather than recomputed — the subject a gate judged must stay knowable after the fact.
    /// </summary>
    public int? Subject { get; init; }

    /// <summary>Team run backing a <see cref="WorkflowStateKind.FanOut"/> state.</summary>
    public long? RunId { get; init; }

    /// <summary>Branch states a fan-out opened, in declaration order.</summary>
    public IReadOnlyList<string> Branches { get; init; } = [];

    public DateTime At { get; init; }
}

/// <summary>
/// A walk as replayed from one ticket's comment trail. Nothing here is stored: re-deriving it after
/// an engine restart yields the same walk, because the receipts <em>are</em> the walk.
/// </summary>
public sealed record WorkflowWalkState(WorkflowWalkStatus Status, IReadOnlyList<WorkflowWalkStep> Steps)
{
    public static readonly WorkflowWalkState None = new(WorkflowWalkStatus.NotStarted, []);

    /// <summary>State the walk is inside right now — entered with nothing having left it yet.</summary>
    public WorkflowWalkStep? Open { get; init; }

    /// <summary>State the opening <c>started</c> receipt asked to begin at. Null means the graph's entry.</summary>
    public string? StartAt { get; init; }

    /// <summary>True while the walker still owns this ticket.</summary>
    public bool IsOpen => Status == WorkflowWalkStatus.Running;

    /// <summary>Number the next <c>entered</c> receipt gets.</summary>
    public int NextStep => Steps.Count == 0 ? 1 : Steps.Max(step => step.Step) + 1;

    /// <summary>
    /// Every role that handled a traversal, in order and with repeats — a role that worked the same
    /// state twice really did work it twice, and a gate asking "who has seen this" needs that.
    /// </summary>
    public IReadOnlyList<string> VisitedRoles => Steps
        .Where(step => step.Event == WorkflowWalkEvent.Entered && !string.IsNullOrWhiteSpace(step.Role))
        .Select(step => step.Role!)
        .ToArray();

    /// <summary>How many times the walk has entered <paramref name="state"/>. The cycle bound.</summary>
    public int EntryCount(string state) => Steps.Count(step =>
        step.Event == WorkflowWalkEvent.Entered
        && string.Equals(step.State, state, StringComparison.OrdinalIgnoreCase));

    /// <summary>Subject the newest step carried, for a state that has no subject of its own.</summary>
    public int? NewestSubject => Steps
        .Where(step => step.Subject is not null)
        .Select(step => step.Subject)
        .LastOrDefault();
}

/// <summary>
/// The durable form of a workflow walk: one receipt comment per traversal, on the ticket itself.
/// <para>
/// <b>Why comments and not a table.</b> The same reason the C3 repair loop recounts its budget from
/// the comment trail instead of a counter column: the number is then auditable (an owner can reread
/// the ticket and get the same answer), restart-proof (the engine holds nothing to lose) and immune
/// to a resumed run restarting it. A walker table would be a second source of truth that the board
/// could contradict.
/// </para>
/// <para>
/// The shape is the verdict contract's: a marker line that makes the receipt greppable and cheap to
/// recognize, plus a <c>```json</c> block carrying the payload — so a state name or a role may be any
/// string the graph declares without a marker grammar constraining what a workflow may be called.
/// The marker and the body must agree; a receipt where they disagree is treated as unreadable and
/// skipped rather than half-believed.
/// </para>
/// </summary>
public static class WorkflowWalk
{
    /// <summary>Marker prefix every walk receipt carries. Also the search key that finds walking tickets.</summary>
    public const string MarkerPrefix = "GIGACLAW-WALK v1";

    /// <summary>Author every walk receipt is written under.</summary>
    public const string ReceiptAuthor = "automation";

    /// <summary>Outcome label a task state's completion routes on.</summary>
    public const string DoneOutcome = "DONE";

    /// <summary>Outcome label an ordinary (non-verdict) gate condition produces when it matches.</summary>
    public const string PassOutcome = "PASS";

    /// <summary>…and when it does not.</summary>
    public const string FailOutcome = "FAIL";

    private static readonly System.Text.RegularExpressions.Regex MarkerRegex = new(
        @"^GIGACLAW-WALK\s+v1\s+ticket-(?<ticket>[0-9]+)\s+step-(?<step>[0-9]+)\s+(?<event>started|entered|left|parked|finished)\s*$",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex FenceRegex = new(
        "```json\\s*\\n(?<body>.*?)\\n```",
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static JsonSerializerOptions JsonOptions => Json;

    public static bool IsWalkReceipt(string? body)
        => body is not null && MarkerRegex.IsMatch(body);

    /// <summary>Renders a receipt: marker line, the human sentence, then the payload.</summary>
    public static string Render(int ticketId, WorkflowWalkStep step, string? prose = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{MarkerPrefix} ticket-{ticketId} step-{step.Step} {Word(step.Event)}");
        sb.AppendLine();
        sb.AppendLine(prose ?? Describe(step));
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(step, Json));
        sb.AppendLine("```");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Reads a receipt back. The last marker wins so an edited comment cannot resurrect an earlier
    /// step, and the marker must agree with the payload it introduces.
    /// </summary>
    public static bool TryRead(string? body, out WorkflowWalkStep? step, out string? error)
    {
        step = null;
        error = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            error = "comment is empty";
            return false;
        }

        System.Text.RegularExpressions.Match? marker = null;
        foreach (System.Text.RegularExpressions.Match candidate in MarkerRegex.Matches(body))
            marker = candidate;
        if (marker is null)
        {
            error = $"comment has no '{MarkerPrefix}' marker line";
            return false;
        }

        var fence = FenceRegex.Match(body, marker.Index + marker.Length);
        if (!fence.Success)
        {
            error = "comment has a walk marker but no ```json payload after it";
            return false;
        }

        WorkflowWalkStep? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<WorkflowWalkStep>(fence.Groups["body"].Value, Json);
        }
        catch (JsonException exception)
        {
            error = $"walk payload is not valid JSON: {exception.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "walk payload is empty";
            return false;
        }

        if (!int.TryParse(marker.Groups["step"].Value, out var markerStep) || markerStep != parsed.Step)
        {
            error = $"marker says step-{marker.Groups["step"].Value} but the payload says step {parsed.Step}";
            return false;
        }

        if (!string.Equals(Word(parsed.Event), marker.Groups["event"].Value, StringComparison.OrdinalIgnoreCase))
        {
            error = $"marker says '{marker.Groups["event"].Value}' but the payload says '{Word(parsed.Event)}'";
            return false;
        }

        step = parsed;
        return true;
    }

    /// <summary>
    /// Replays the ticket's comments into the walk they describe.
    /// <para>
    /// A <c>started</c> receipt <b>resets</b> the walk: it is the only thing that opens one, and the
    /// action that writes it refuses while a walk is still running — so a second <c>started</c> is
    /// always a genuinely new walk (an owner re-running a parked ticket), not a duplicate of the
    /// live one. Everything else accumulates in comment order.
    /// </para>
    /// An unreadable receipt is skipped rather than guessed at, exactly as an unreadable verdict is.
    /// </summary>
    public static WorkflowWalkState Replay(IEnumerable<string> commentBodies)
    {
        var steps = new List<WorkflowWalkStep>();
        var status = WorkflowWalkStatus.NotStarted;
        string? startAt = null;

        foreach (var body in commentBodies)
        {
            if (!IsWalkReceipt(body)) continue;
            if (!TryRead(body, out var step, out _)) continue;

            if (step!.Event == WorkflowWalkEvent.Started)
            {
                steps.Clear();
                startAt = string.IsNullOrWhiteSpace(step.State) ? null : step.State;
                status = WorkflowWalkStatus.Running;
                continue;
            }

            // Receipts that arrive after the walk closed belong to no walk: believing them would
            // resurrect a parked ticket without an owner ever reopening it.
            if (status is WorkflowWalkStatus.NotStarted or WorkflowWalkStatus.Parked or WorkflowWalkStatus.Finished)
                continue;

            steps.Add(step);
            status = step.Event switch
            {
                WorkflowWalkEvent.Parked => WorkflowWalkStatus.Parked,
                WorkflowWalkEvent.Finished => WorkflowWalkStatus.Finished,
                _ => WorkflowWalkStatus.Running,
            };
        }

        var closed = steps
            .Where(step => step.Event is WorkflowWalkEvent.Left or WorkflowWalkEvent.Parked or WorkflowWalkEvent.Finished)
            .Select(step => step.Step)
            .ToHashSet();
        var open = steps.LastOrDefault(step => step.Event == WorkflowWalkEvent.Entered && !closed.Contains(step.Step));

        return new WorkflowWalkState(status, steps) { Open = open, StartAt = startAt };
    }

    /// <summary>The walk history an escalation receipt carries, so the argument is on the ticket.</summary>
    public static string RenderHistory(WorkflowWalkState walk)
    {
        if (walk.Steps.Count == 0) return "The walk had taken no step yet.";

        var sb = new StringBuilder();
        foreach (var step in walk.Steps)
            sb.AppendLine($"- step {step.Step}: {Describe(step)}");
        return sb.ToString().TrimEnd();
    }

    public static string Describe(WorkflowWalkStep step) => step.Event switch
    {
        WorkflowWalkEvent.Started =>
            $"Workflow walk opened at '{step.State}'.",
        WorkflowWalkEvent.Entered =>
            $"Entered '{step.State}' ({Word(step.Kind)}){Role(step)}{Subject(step)}.",
        WorkflowWalkEvent.Left =>
            $"Left '{step.State}' on {step.Outcome ?? "?"} → '{step.To}'.",
        WorkflowWalkEvent.Parked =>
            $"Parked at '{step.State}': {step.Reason}",
        WorkflowWalkEvent.Finished =>
            $"Finished at terminal state '{step.State}'.",
        _ => step.State,
    };

    private static string Role(WorkflowWalkStep step)
        => string.IsNullOrWhiteSpace(step.Role) ? "" : $", role {step.Role}";

    private static string Subject(WorkflowWalkStep step)
        => step.Subject is null ? "" : $", ticket #{step.Subject}";

    private static string Word(WorkflowWalkEvent value) => value switch
    {
        WorkflowWalkEvent.Started => "started",
        WorkflowWalkEvent.Entered => "entered",
        WorkflowWalkEvent.Left => "left",
        WorkflowWalkEvent.Parked => "parked",
        _ => "finished",
    };

    private static string Word(WorkflowStateKind? kind) => kind switch
    {
        WorkflowStateKind.Task => "task",
        WorkflowStateKind.FanOut => "fanOut",
        WorkflowStateKind.Join => "join",
        WorkflowStateKind.Gate => "gate",
        WorkflowStateKind.Terminal => "terminal",
        _ => "state",
    };
}
