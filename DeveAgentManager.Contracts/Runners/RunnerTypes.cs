using DeveAgentManager.Core.Abstractions;

namespace DeveAgentManager.Core.Runners;

public sealed record CreateRunnerRequest(
    string Name,
    string ProviderId,
    string? TemplateId,
    int Cores,
    int MemoryMb,
    int DiskGb,
    IReadOnlyList<string> Harnesses,
    ConnectionDetails? Connection);
