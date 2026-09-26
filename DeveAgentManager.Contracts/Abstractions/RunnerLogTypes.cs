namespace DeveAgentManager.Core.Abstractions;

public enum RunnerLogLevel
{
    Step,
    Info,
    Output,
    Success,
    Warning,
    Error,
}

public sealed record RunnerLogLine(DateTime Timestamp, RunnerLogLevel Level, string Text);
