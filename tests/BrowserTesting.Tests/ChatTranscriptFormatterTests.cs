using BrowserTesting.Core.Models;
using BrowserTesting.Core.Services;

namespace BrowserTesting.Tests;

public sealed class ChatTranscriptFormatterTests
{
    [Fact]
    public void FormatForSelection_IncludesMessagesAndToolMetadata()
    {
        var chat = CreateChat();

        var transcript = ChatTranscriptFormatter.FormatForSelection(chat);

        Assert.Contains("Chat: Export Demo", transcript);
        Assert.Contains("You", transcript);
        Assert.Contains("Assistant", transcript);
        Assert.Contains("Tool Call Started: open_browser", transcript);
        Assert.Contains("Tool Response: open_browser", transcript);
        Assert.Contains("Metadata:", transcript);
        Assert.Contains("\"url\": \"https://example.com\"", transcript);
    }

    [Fact]
    public void FormatForExport_IncludesRunsGoalsAndTimelineEntries()
    {
        var chat = CreateChat();

        var export = ChatTranscriptFormatter.FormatForExport(chat);

        Assert.Contains("Chat Export", export);
        Assert.Contains("Run 11111111-1111-1111-1111-111111111111", export);
        Assert.Contains("Browser Snapshot:", export);
        Assert.Contains("Goals:", export);
        Assert.Contains("Success Criteria: Dashboard appears", export);
        Assert.Contains("Timeline", export);
        Assert.Contains("Tool Name: open_browser", export);
        Assert.Contains("Content:", export);
        Assert.Contains("Opened the home page.", export);
    }

    private static ChatSession CreateChat()
    {
        var runId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        return new ChatSession
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Title = "Export Demo",
            CreatedAtUtc = new DateTime(2026, 4, 13, 12, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 4, 13, 12, 5, 0, DateTimeKind.Utc),
            Runs =
            [
                new TestRun
                {
                    Id = runId,
                    ChatSessionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    UserPrompt = "Open the homepage and verify the dashboard.",
                    Status = TestRunStatus.Completed,
                    CreatedAtUtc = new DateTime(2026, 4, 13, 12, 1, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 4, 13, 12, 4, 0, DateTimeKind.Utc),
                    CompletedAtUtc = new DateTime(2026, 4, 13, 12, 4, 30, DateTimeKind.Utc),
                    BrowserSnapshot = new BrowserSessionSnapshot
                    {
                        TestRunId = runId,
                        CurrentUrl = "https://example.com/dashboard",
                        PageTitle = "Dashboard",
                        RestoreStatus = RestoreStatus.Active,
                        Tabs =
                        [
                            new BrowserTabInfo
                            {
                                Handle = "tab-1",
                                Title = "Dashboard",
                                Url = "https://example.com/dashboard",
                                IsSelected = true,
                            },
                        ],
                    },
                    Goals =
                    [
                        new GoalItem
                        {
                            Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                            TestRunId = runId,
                            Title = "Verify dashboard",
                            SuccessCriteria = "Dashboard appears",
                            Status = GoalStatus.Passed,
                            Note = "Visible after login.",
                            Evidence = "Dashboard heading is present.",
                            CreatedAtUtc = new DateTime(2026, 4, 13, 12, 2, 0, DateTimeKind.Utc),
                            UpdatedAtUtc = new DateTime(2026, 4, 13, 12, 4, 0, DateTimeKind.Utc),
                            CompletedAtUtc = new DateTime(2026, 4, 13, 12, 4, 5, DateTimeKind.Utc),
                        },
                    ],
                },
            ],
            Timeline =
            [
                new TimelineEntry
                {
                    Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                    ChatSessionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    TestRunId = runId,
                    Sequence = 1,
                    Kind = TimelineItemKind.UserMessage,
                    Role = "user",
                    Content = "Open the homepage.",
                    CreatedAtUtc = new DateTime(2026, 4, 13, 12, 1, 0, DateTimeKind.Utc),
                },
                new TimelineEntry
                {
                    Id = Guid.Parse("10000000-0000-0000-0000-000000000002"),
                    ChatSessionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    TestRunId = runId,
                    Sequence = 2,
                    Kind = TimelineItemKind.AssistantMessage,
                    Role = "assistant",
                    Content = "I'm opening the homepage now.",
                    CreatedAtUtc = new DateTime(2026, 4, 13, 12, 1, 10, DateTimeKind.Utc),
                },
                new TimelineEntry
                {
                    Id = Guid.Parse("10000000-0000-0000-0000-000000000003"),
                    ChatSessionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    TestRunId = runId,
                    Sequence = 3,
                    Kind = TimelineItemKind.ToolCallStarted,
                    Role = "assistant",
                    Content = "Calling `open_browser`...",
                    ToolCallId = "call_1",
                    ToolName = "open_browser",
                    MetadataJson = "{\"url\":\"https://example.com\"}",
                    CreatedAtUtc = new DateTime(2026, 4, 13, 12, 1, 20, DateTimeKind.Utc),
                },
                new TimelineEntry
                {
                    Id = Guid.Parse("10000000-0000-0000-0000-000000000004"),
                    ChatSessionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    TestRunId = runId,
                    Sequence = 4,
                    Kind = TimelineItemKind.ToolCallFinished,
                    Role = "tool",
                    Content = "Opened the home page.",
                    ToolCallId = "call_1",
                    ToolName = "open_browser",
                    MetadataJson = "{\"success\":true,\"data\":{\"url\":\"https://example.com/dashboard\"}}",
                    CreatedAtUtc = new DateTime(2026, 4, 13, 12, 1, 25, DateTimeKind.Utc),
                },
            ],
        };
    }
}
