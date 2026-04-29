namespace BrowserTesting.Core.Models;

public enum LlmProvider
{
    Local = 0,
    OpenAi = 1,
}

public enum GoalStatus
{
    Pending = 0,
    Running = 1,
    Passed = 2,
    Failed = 3,
}

public enum TestRunStatus
{
    Pending = 0,
    Running = 1,
    WaitingForTool = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
}

public enum BrowserState
{
    NotStarted = 0,
    Active = 1,
    Closed = 2,
    Failed = 3,
}

public enum TimelineItemKind
{
    UserMessage = 0,
    AssistantMessage = 1,
    ToolCallStarted = 2,
    ToolCallFinished = 3,
    GoalChanged = 4,
    SystemNotice = 5,
}
