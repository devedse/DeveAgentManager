using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DeveAgentManager.Core.Diagnostics;

/// <summary>Records command kind/duration, never SQL, parameters, connection strings or exception messages.</summary>
internal sealed class CaptureCommandInterceptor : DbCommandInterceptor
{
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    { CaptureOperations.Database("reader", eventData.Duration); return result; }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    { CaptureOperations.Database("reader", eventData.Duration); return ValueTask.FromResult(result); }
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    { CaptureOperations.Database("nonquery", eventData.Duration); return result; }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    { CaptureOperations.Database("nonquery", eventData.Duration); return ValueTask.FromResult(result); }
    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    { CaptureOperations.Database("scalar", eventData.Duration); return result; }
    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    { CaptureOperations.Database("scalar", eventData.Duration); return ValueTask.FromResult(result); }
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => CaptureOperations.Database("failed", eventData.Duration, true);
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    { CaptureOperations.Database("failed", eventData.Duration, true); return Task.CompletedTask; }
}
