namespace DeveAgentManager.Contracts;

public sealed record ManagerCpuTrace(Guid Id, DateTimeOffset StartedAt, int DurationSeconds, string State, long Bytes = 0,
    string? Error = null, string Trigger = "manual", string? Phase = null, DateTimeOffset? PhaseStartedAt = null, int PhaseSeconds = 0);
public sealed record ManagerCpuTraceStatus(ManagerCpuTrace? Active, IReadOnlyList<ManagerCpuTrace> Captures,
    DateTimeOffset ServerTime = default, string? StartupError = null);
