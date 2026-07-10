using System.Text.Json;
using System.Text.Json.Serialization;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// Normalized SSE/HTTP payload for one CC transcript record.
/// Wire shape per spec 03 §1.6 — kept stable so PWA isn't coupled to CC's raw JSONL fields.
/// </summary>
public record TokenUsage(
    [property: JsonPropertyName("inputTokens")] int InputTokens,
    [property: JsonPropertyName("outputTokens")] int OutputTokens,
    [property: JsonPropertyName("cacheCreationInputTokens")] int CacheCreationInputTokens,
    [property: JsonPropertyName("cacheReadInputTokens")] int CacheReadInputTokens,
    [property: JsonPropertyName("model")] string? Model
);

public record SessionMessage(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("uuid")] string? Uuid,
    [property: JsonPropertyName("parentUuid")] string? ParentUuid,
    [property: JsonPropertyName("sessionUuid")] string? SessionUuid,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("ts")] string? Timestamp,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("userType")] string? UserType,
    [property: JsonPropertyName("isSidechain")] bool IsSidechain,
    [property: JsonPropertyName("content")] JsonElement? Content,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("raw")] JsonElement? Raw,
    [property: JsonPropertyName("usage")] TokenUsage? Usage = null
);

public record SessionStatus(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sessionUuid")] string? SessionUuid,
    [property: JsonPropertyName("needsInput")] bool NeedsInput,
    [property: JsonPropertyName("running")] bool Running,
    [property: JsonPropertyName("lastEventAt")] string? LastEventAt,
    [property: JsonPropertyName("notificationMessage")] string? NotificationMessage = null,
    // Authoritative "claude is working this turn" signal driven by CC hooks
    // (UserPromptSubmit/PreToolUse/PostToolUse start it, Stop ends it). Replaces
    // the unreliable client-side JSONL-shape guess.
    [property: JsonPropertyName("processing")] bool Processing = false
);

public record SessionListItem(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sessionUuid")] string? SessionUuid,
    [property: JsonPropertyName("lastMessageAt")] string? LastMessageAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("needsInput")] bool NeedsInput,
    // ADR-015 ownership for the multi-session dashboard: "tmux" (Mode A),
    // "pc" (Mode B), or "none". Derived cheaply from the persisted pc-marker
    // + the already-computed running window set (no extra tmux calls).
    [property: JsonPropertyName("owner")] string Owner = "none"
);

public record TranscriptResponse(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sessionUuid")] string? SessionUuid,
    [property: JsonPropertyName("messages")] List<SessionMessage> Messages,
    [property: JsonPropertyName("readOnly")] bool ReadOnly = false,
    // Tail-load metadata (set when ?limit= was applied): total records in the
    // JSONL + whether older records exist beyond the returned window.
    [property: JsonPropertyName("total")] int Total = 0,
    [property: JsonPropertyName("truncated")] bool Truncated = false,
    // Byte offset (EOF) this read consumed up to — the SSE handshake anchor. The
    // PWA passes it back as ?since= so the stream replays only [tailOffset, EOF)
    // (records appended in the gap between this REST read and the SSE connect),
    // never the full history. Computed in the same read pass (atomic). See
    // docs/specs/01 "SSE = live delta thuần; lịch sử qua REST + offset handshake".
    [property: JsonPropertyName("tailOffset")] long TailOffset = 0
);
