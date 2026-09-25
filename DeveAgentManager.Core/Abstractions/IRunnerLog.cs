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

/// <summary>The setup log of one Runner, shown live in the Portal.</summary>
public interface IRunnerLog
{
    void Write(RunnerLogLevel level, string text);
}

public static class RunnerLogExtensions
{
    public static void Step(this IRunnerLog log, string text) => log.Write(RunnerLogLevel.Step, text);
    public static void Info(this IRunnerLog log, string text) => log.Write(RunnerLogLevel.Info, text);
    public static void Output(this IRunnerLog log, string text) => log.Write(RunnerLogLevel.Output, text);
    public static void Success(this IRunnerLog log, string text) => log.Write(RunnerLogLevel.Success, text);
    public static void Warning(this IRunnerLog log, string text) => log.Write(RunnerLogLevel.Warning, text);
    public static void Error(this IRunnerLog log, string text) => log.Write(RunnerLogLevel.Error, text);
}

public sealed class NullRunnerLog : IRunnerLog
{
    public static readonly NullRunnerLog Instance = new();

    public void Write(RunnerLogLevel level, string text)
    {
    }
}
