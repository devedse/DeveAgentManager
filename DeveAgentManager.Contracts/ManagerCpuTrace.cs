namespace DeveAgentManager.Contracts;

public sealed record ManagerCpuTrace(Guid Id, DateTimeOffset StartedAt, int DurationSeconds, string State, long Bytes = 0, string? Error = null);
public sealed record ManagerCpuTraceStatus(ManagerCpuTrace? Active, IReadOnlyList<ManagerCpuTrace> Captures);
