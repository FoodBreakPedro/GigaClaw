using GigaClaw.Core.Models;
using GigaClaw.Core.Packs;

namespace GigaClaw.Web.Api;

public record CreateProjectRequest(string Name);
public record InitializeProjectRequest(bool OverwriteConflicts = false);
public record ApplyAgentTemplateSyncRequest(string PlanToken);
public record AgentTemplateSyncApplyResponse(
    AgentTemplateSyncPlan Plan,
    IReadOnlyList<string> AppliedPaths,
    IReadOnlyList<string> MembersCreated,
    bool AutomationsReloaded);
public record CreateTicketRequest(string Title, string CreatedBy, string Status, string Description = "", List<int>? LabelIds = null, TicketPriority Priority = TicketPriority.NiceToHave, string? AssignedTo = null, int? ParentId = null);
public record UpdateTicketRequest(string Author, string? Title = null, string? Description = null, TicketPriority? Priority = null, string? AssignedTo = null, List<int>? LabelIds = null);
public record MoveTicketRequest(string Status, string Author);
public record TransitionTicketRequest(string Status, string Author, string? AssignedTo = null, string? ExpectedStatus = null);
public record ScheduleTicketRequest(DateTime FireAt, string Author, string? TargetStatus = "Todo");
public record AddCommentRequest(string Content, string Author);
public record UpdateCommentRequest(string Content, string Author);
public record CreateLabelRequest(string Name, string Color = "#6366f1");
public record UpdateLabelRequest(string? Name = null, string? Color = null);
public record SetTicketLabelsRequest(List<int> LabelIds);
public record PatchTicketLabelsRequest(string Author, List<int>? AddLabelIds = null, List<int>? RemoveLabelIds = null);
public record ReorderTicketRequest(string Status, int Index);
public record CreateColumnRequest(string Name, string Color = "#5a6a80");
public record UpdateColumnRequest(string? Name = null, string? Color = null);
public record ReorderColumnRequest(int ColumnId, int Index);
public record CreateMemberRequest(string Name);
public record UpdateMemberRequest(string? Name = null, string? DefaultModel = null, AgentHarness? Harness = null);
public record SetParentRequest(int ParentId);
public record AddTicketDependencyRequest(int BlockingTicketId);
public record UpdateProjectRequest(string? WorkspacePath = null, string? FallbackModel = null, bool UpdateFallbackModel = false);
public record SaveLocalModelConfigRequest(string? LocalModelBaseUrl = null, string? LocalModelName = null);
public record SteerRunRequest(string Text);
public record HermesApprovalRequest(string Choice, bool ResolveAll = false);
public record BrowseFolderRequest(string? InitialPath = null);
public record ChatImageDto(string DataUrl, string Mime, string Name, long SizeBytes);
public record ChatStartRequest(string Message, string Target = "owner-chat", bool ForceNew = false, int? TicketId = null, IReadOnlyList<ChatImageDto>? Images = null, string? Model = null);
public record ChatTargetDto(string Slug, string Name, string Kind);
public record ChatTargetsResponse(string? LastTarget, List<ChatTargetDto> Targets);
public record ChatMessageDto(string Role, string Text, string? ToolName, string? Detail, string CreatedAt);
public record MoveTileRequest(int X, int Y); // required — pixel coords snapped to 20px grid
public record ResizeTileRequest(int Width, int Height); // required — pixels snapped to 20px grid

// C7/U5 GitHub surface. The token is write-only: it goes in on SaveGitHubConfigRequest and never
// comes back out — GitHubConfigDto reports only whether one is stored.
public record SaveGitHubConfigRequest(
    bool Enabled,
    string? Owner = null,
    string? Repo = null,
    string? ImportLabel = null,
    string? ImportStatus = null,
    bool CommentOnIssueWhenTicketDone = false,
    bool CloseIssueWhenTicketDone = false,
    List<string>? DoneStatuses = null,
    List<string>? OwnerLogins = null,
    string? ApiBaseUrl = null,
    string? Token = null);
public record GitHubConfigDto(
    bool Configured,
    bool Enabled,
    string Owner,
    string Repo,
    string ImportLabel,
    string ImportStatus,
    bool CommentOnIssueWhenTicketDone,
    bool CloseIssueWhenTicketDone,
    List<string> DoneStatuses,
    List<string> OwnerLogins,
    string ApiBaseUrl,
    bool TokenConfigured);
