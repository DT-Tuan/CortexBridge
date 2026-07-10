using CortexBridge.Api.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// Golden tests for JsonlReader. Fixtures live in tests/fixtures/jsonl/ and are
/// schema-faithful samples (see tests/fixtures/README.md).
/// </summary>
public class JsonlReaderTests
{
    private static string FixturePath(string name)
    {
        // Tests run from bin/Debug/net10.0/, walk back to repo root then into fixtures
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "jsonl", name);
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new FileNotFoundException($"Cannot locate fixture {name}");
    }

    private static JsonlReader Reader() => new(NullLogger<JsonlReader>.Instance);

    [Fact]
    public async Task SampleSession_ParsesAllRecords()
    {
        var path = FixturePath("sample-session-001.jsonl");
        var (msgs, offset) = await Reader().ReadFromOffsetAsync(path, 0, "sample-project", CancellationToken.None);

        Assert.Equal(7, msgs.Count);
        Assert.Equal("summary",   msgs[0].Role);
        Assert.Equal("user",      msgs[1].Role);
        Assert.Equal("assistant", msgs[2].Role);
        Assert.Equal("user",      msgs[3].Role);
        Assert.Equal("assistant", msgs[4].Role);
        Assert.Equal("user",      msgs[5].Role);
        Assert.Equal("assistant", msgs[6].Role);

        // Offset should equal file size when reading from 0
        var fileSize = new FileInfo(path).Length;
        Assert.Equal(fileSize, offset);
    }

    [Fact]
    public async Task SampleSession_PassesProjectIdThrough()
    {
        var path = FixturePath("sample-session-001.jsonl");
        var (msgs, _) = await Reader().ReadFromOffsetAsync(path, 0, "my-custom-id", CancellationToken.None);
        Assert.All(msgs, m => Assert.Equal("my-custom-id", m.ProjectId));
    }

    [Fact]
    public async Task EdgeCases_PreservesUtf8Vietnamese()
    {
        var path = FixturePath("edge-cases.jsonl");
        var (msgs, _) = await Reader().ReadFromOffsetAsync(path, 0, "edge", CancellationToken.None);

        // Find the user message (index 1 in fixture: summary, user, sidechain-assistant, system, ...)
        var userMsg = msgs.First(m => m.Role == "user" && m.Uuid == "edge-001");
        var contentJson = userMsg.Content?.GetRawText() ?? "";
        Assert.Contains("Xin ch", contentJson); // Vietnamese tone marks survive parse
        Assert.Contains("đây", contentJson);
        Assert.Contains("ặ", contentJson);
    }

    [Fact]
    public async Task EdgeCases_UnknownRecordTypeFallsThroughAsKindUnknown()
    {
        var path = FixturePath("edge-cases.jsonl");
        var (msgs, _) = await Reader().ReadFromOffsetAsync(path, 0, "edge", CancellationToken.None);
        var unknown = msgs.Single(m => m.Kind == "unknown");
        Assert.Equal("future_record_type_we_dont_know", unknown.Role);
        Assert.NotNull(unknown.Raw);
    }

    [Fact]
    public async Task EdgeCases_UnknownDoesNotHaltSubsequentParsing()
    {
        var path = FixturePath("edge-cases.jsonl");
        var (msgs, _) = await Reader().ReadFromOffsetAsync(path, 0, "edge", CancellationToken.None);
        // After the unknown record (edge-005) there is edge-006 — parser must reach it
        Assert.Contains(msgs, m => m.Uuid == "edge-006");
    }

    [Fact]
    public async Task EdgeCases_PreservesSidechainFlag()
    {
        var path = FixturePath("edge-cases.jsonl");
        var (msgs, _) = await Reader().ReadFromOffsetAsync(path, 0, "edge", CancellationToken.None);
        var sidechain = msgs.Single(m => m.IsSidechain);
        Assert.Equal("edge-002", sidechain.Uuid);
    }

    [Fact]
    public async Task MalformedLine_DoesNotHaltStream()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"jsonl-mid-bad-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(tmp, """
            {"type":"user","uuid":"u1","timestamp":"2026-05-06T00:00:00Z","sessionId":"s","cwd":"/x","message":{"role":"user","content":"first"}}
            this is not json {{{
            {"type":"user","uuid":"u2","timestamp":"2026-05-06T00:00:01Z","sessionId":"s","cwd":"/x","message":{"role":"user","content":"third"}}

            """);
        try
        {
            var (msgs, _) = await Reader().ReadFromOffsetAsync(tmp, 0, "p", CancellationToken.None);
            Assert.Equal(2, msgs.Count);
            Assert.Equal("u1", msgs[0].Uuid);
            Assert.Equal("u2", msgs[1].Uuid);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task TaskNotification_UserRecord_HiddenAsUnknown()
    {
        // Harness background-task events arrive as role:user string records wrapped
        // in <task-notification> — pure orchestration noise that leaked into the PWA
        // transcript. Must classify as kind:"unknown" (hidden); a real user message
        // alongside it stays kind:"message".
        var tmp = Path.Combine(Path.GetTempPath(), $"jsonl-tasknotif-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(tmp, """
            {"type":"user","uuid":"tn1","timestamp":"2026-06-13T00:00:00Z","sessionId":"s","cwd":"/x","message":{"role":"user","content":"<task-notification>\n<task-id>abc</task-id>\n<status>completed</status>\n</task-notification>"}}
            {"type":"user","uuid":"u1","timestamp":"2026-06-13T00:00:01Z","sessionId":"s","cwd":"/x","message":{"role":"user","content":"a real message mentioning task-notification in prose"}}

            """);
        try
        {
            var (msgs, _) = await Reader().ReadFromOffsetAsync(tmp, 0, "p", CancellationToken.None);
            Assert.Equal("unknown", msgs.Single(m => m.Uuid == "tn1").Kind);
            Assert.Equal("message", msgs.Single(m => m.Uuid == "u1").Kind);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task ReadFromOffset_OnlyReturnsAppendedLines()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"jsonl-append-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(tmp,
            """{"type":"user","uuid":"u1","timestamp":"2026-05-06T00:00:00Z","sessionId":"s","cwd":"/x","message":{"role":"user","content":"a"}}""" + "\n");
        try
        {
            var (first, off1) = await Reader().ReadFromOffsetAsync(tmp, 0, "p", CancellationToken.None);
            Assert.Single(first);

            await File.AppendAllTextAsync(tmp,
                """{"type":"user","uuid":"u2","timestamp":"2026-05-06T00:00:01Z","sessionId":"s","cwd":"/x","message":{"role":"user","content":"b"}}""" + "\n");

            var (second, off2) = await Reader().ReadFromOffsetAsync(tmp, off1, "p", CancellationToken.None);
            Assert.Single(second);
            Assert.Equal("u2", second[0].Uuid);
            Assert.True(off2 > off1);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task ReadFromOffset_FileShrinkReturnsZeroOffset()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"jsonl-shrink-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(tmp, "AAAAAAAAAAAA");
        try
        {
            var (msgs, off) = await Reader().ReadFromOffsetAsync(tmp, 999_999, "p", CancellationToken.None);
            Assert.Empty(msgs);
            Assert.Equal(0, off);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task CompactBoundary_BecomesCompactKind_WithTriggerAndTokens()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"jsonl-compact-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(tmp, """
            {"type":"user","uuid":"u1","timestamp":"2026-05-19T04:00:00Z","sessionId":"s","cwd":"/x","message":{"role":"user","content":"before"}}
            {"type":"system","subtype":"compact_boundary","content":"Conversation compacted","timestamp":"2026-05-19T04:03:05Z","uuid":"cb1","sessionId":"s","compactMetadata":{"trigger":"manual","preTokens":173284,"postTokens":8357}}
            {"type":"user","uuid":"u2","timestamp":"2026-05-19T04:03:06Z","sessionId":"s","cwd":"/x","isCompactSummary":true,"isVisibleInTranscriptOnly":true,"message":{"role":"user","content":"This session is being continued ... (huge summary)"}}
            {"type":"user","uuid":"u3","timestamp":"2026-05-19T04:04:00Z","sessionId":"s","cwd":"/x","message":{"role":"user","content":"after"}}

            """);
        try
        {
            var (msgs, _) = await Reader().ReadFromOffsetAsync(tmp, 0, "p", CancellationToken.None);

            var compact = Assert.Single(msgs, m => m.Kind == "compact");
            Assert.Equal("compact", compact.Role);
            Assert.Equal("manual|173284|8357", compact.Text);   // backend-owned stable tuple

            // The multi-KB isCompactSummary context-seed record is suppressed.
            var seed = Assert.Single(msgs, m => m.Uuid == "u2");
            Assert.Equal("unknown", seed.Kind);

            // Real conversation around it is untouched.
            Assert.Contains(msgs, m => m.Uuid == "u1" && m.Role == "user");
            Assert.Contains(msgs, m => m.Uuid == "u3" && m.Role == "user");
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task LocalCommand_StdoutWrapper_StrippedToInnerText()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"jsonl-lc-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(tmp, """
            {"type":"system","subtype":"local_command","content":"<local-command-stdout>Bye!</local-command-stdout>","timestamp":"2026-05-19T05:00:00Z","uuid":"lc1","sessionId":"s"}
            {"type":"system","subtype":"local_command","content":"<local-command-stderr>boom</local-command-stderr>","timestamp":"2026-05-19T05:00:01Z","uuid":"lc2","sessionId":"s"}
            {"type":"system","subtype":"local_command","content":"<local-command-stdout></local-command-stdout>","timestamp":"2026-05-19T05:00:02Z","uuid":"lc3","sessionId":"s"}
            {"type":"system","subtype":"local_command","content":"<local-command-stdout>\n  Compacted (ctrl+o)\n</local-command-stdout>","timestamp":"2026-05-19T05:00:03Z","uuid":"lc4","sessionId":"s"}

            """);
        try
        {
            var (msgs, _) = await Reader().ReadFromOffsetAsync(tmp, 0, "p", CancellationToken.None);

            var m1 = Assert.Single(msgs, m => m.Uuid == "lc1");
            Assert.Equal("system", m1.Role);
            Assert.Equal("Bye!", m1.Text);                       // wrapper stripped

            var m2 = Assert.Single(msgs, m => m.Uuid == "lc2");
            Assert.Equal("boom", m2.Text);                       // stderr too

            var m3 = Assert.Single(msgs, m => m.Uuid == "lc3");
            Assert.Equal("unknown", m3.Kind);                    // empty ⇒ hidden noise

            var m4 = Assert.Single(msgs, m => m.Uuid == "lc4");
            Assert.Equal("Compacted (ctrl+o)", m4.Text);         // inner trimmed
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task LocalCommand_UserRoleWrapper_CleanedAnsiStripped_BadgeUntouched()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"jsonl-lcu-{Guid.NewGuid():N}.jsonl");
        // CC writes local-command OUTPUT as role:"user" string content.
        await File.WriteAllTextAsync(tmp, """
            {"type":"user","uuid":"x1","timestamp":"2026-05-19T05:00:00Z","sessionId":"s","cwd":"/x","message":{"role":"user","content":"<local-command-stdout>Goodbye!</local-command-stdout>"}}
            {"type":"user","uuid":"x2","timestamp":"2026-05-19T05:00:01Z","sessionId":"s","cwd":"/x","message":{"role":"user","content":"<local-command-stdout>\u001b[2mCompacted (ctrl+o to see full summary)\u001b[22m</local-command-stdout>"}}
            {"type":"user","uuid":"x3","timestamp":"2026-05-19T05:00:02Z","sessionId":"s","cwd":"/x","message":{"role":"user","content":"<local-command-stdout></local-command-stdout>"}}
            {"type":"user","uuid":"x4","timestamp":"2026-05-19T05:00:03Z","sessionId":"s","cwd":"/x","message":{"role":"user","content":"<command-name>/exit</command-name>\n<command-message>exit</command-message>"}}
            {"type":"user","uuid":"x5","timestamp":"2026-05-19T05:00:04Z","sessionId":"s","cwd":"/x","message":{"role":"user","content":"real user text [12] kept"}}

            """);
        try
        {
            var (msgs, _) = await Reader().ReadFromOffsetAsync(tmp, 0, "p", CancellationToken.None);

            var x1 = Assert.Single(msgs, m => m.Uuid == "x1");
            Assert.Equal("system", x1.Role);            // command output, not user speech
            Assert.Equal("Goodbye!", x1.Text);

            var x2 = Assert.Single(msgs, m => m.Uuid == "x2");
            Assert.Equal("Compacted (ctrl+o to see full summary)", x2.Text);  // ANSI stripped

            var x3 = Assert.Single(msgs, m => m.Uuid == "x3");
            Assert.Equal("unknown", x3.Kind);           // empty ⇒ hidden

            // <command-name> record is left intact so the PWA still renders the
            // "/exit" command badge (content passed through, role still user).
            var x4 = Assert.Single(msgs, m => m.Uuid == "x4");
            Assert.Equal("message", x4.Kind);
            Assert.Equal("user", x4.Role);
            Assert.NotNull(x4.Content);

            // Plain user text containing literal brackets is NOT ANSI-mangled.
            var x5 = Assert.Single(msgs, m => m.Uuid == "x5");
            Assert.Equal("user", x5.Role);
            Assert.NotNull(x5.Content);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task CompactBoundary_MissingMetadata_StillCompactKind()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"jsonl-compact2-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(tmp, """
            {"type":"system","subtype":"compact_boundary","content":"Conversation compacted","timestamp":"2026-05-19T04:03:05Z","uuid":"cb2","sessionId":"s"}

            """);
        try
        {
            var (msgs, _) = await Reader().ReadFromOffsetAsync(tmp, 0, "p", CancellationToken.None);
            var compact = Assert.Single(msgs);
            Assert.Equal("compact", compact.Kind);
            Assert.Equal("?|0|0", compact.Text);   // graceful fallback (older CC / no metadata)
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task ReadTail_ReturnsLastN_WithTotalAndTruncated()
    {
        var path = FixturePath("sample-session-001.jsonl");
        var (msgs, total, truncated, _) = await Reader().ReadTailAsync(path, "p", 3, CancellationToken.None);
        Assert.Equal(7, total);                     // total LINES in the file
        Assert.True(truncated);                     // more than 3 records exist
        Assert.Equal(3, msgs.Count);                // only the last 3 records
        Assert.Equal("assistant", msgs[^1].Role);   // chronological — newest last
    }

    [Fact]
    public async Task ReadTail_TailOffset_IsZeroGapAnchor_CatchUpFromItReadsNothing()
    {
        // The SSE handshake invariant: tailOffset must mark EXACTLY the EOF the
        // tail read consumed, so a catch-up ReadFromOffsetAsync(since=tailOffset)
        // on an unchanged file returns ZERO new records (no gap, no re-stream).
        var path = FixturePath("sample-session-001.jsonl");
        var (_, _, _, tailOffset) = await Reader().ReadTailAsync(path, "p", 3, CancellationToken.None);
        Assert.True(tailOffset > 0);
        var (gap, _) = await Reader().ReadFromOffsetAsync(path, tailOffset, "p", CancellationToken.None);
        Assert.Empty(gap);
    }

    [Fact]
    public async Task ReadTail_LimitBeyondFile_ReturnsAll_NotTruncated()
    {
        var path = FixturePath("sample-session-001.jsonl");
        var (msgs, total, truncated, _) = await Reader().ReadTailAsync(path, "p", 1000, CancellationToken.None);
        Assert.Equal(7, msgs.Count);
        Assert.Equal(7, total);
        Assert.False(truncated);
    }

    [Fact]
    public async Task ReadTail_PreservesChronologicalOrder_MatchesFullTail()
    {
        var path = FixturePath("sample-session-001.jsonl");
        var (full, _) = await Reader().ReadFromOffsetAsync(path, 0, "p", CancellationToken.None);
        var (tail, _, _, _) = await Reader().ReadTailAsync(path, "p", 4, CancellationToken.None);
        Assert.Equal(full.Skip(full.Count - 4).Select(m => m.Role), tail.Select(m => m.Role));
    }
}
