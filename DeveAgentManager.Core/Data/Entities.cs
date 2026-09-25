using DeveAgentManager.Core.Abstractions;

namespace DeveAgentManager.Core.Data;

public enum RunnerState
{
    Creating,
    Starting,
    WaitingForNetwork,
    SettingUp,
    Ready,
    Stopping,
    Stopped,
    Deleting,
    Error,
}

public enum AgentState
{
    Starting,
    Running,
    Exited,
    Stopped,
    Unknown,
    Error,
}

public class Runner
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string ProviderId { get; set; } = "";

    public string? TemplateId { get; set; }

    public RunnerKind Kind { get; set; }

    /// <summary>The provider's id for the machine (the VMID on Proxmox, the host for an existing machine).</summary>
    public string? MachineId { get; set; }

    public string? ProviderData { get; set; }

    public RunnerState State { get; set; }

    public string? StateMessage { get; set; }

    public string? Host { get; set; }

    public int SshPort { get; set; } = 22;

    public string Username { get; set; } = "";

    /// <summary>Only set for existing machines added with a password; encrypted.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>SHA256 fingerprint of the SSH host key, learned on first connect (trust on first use).</summary>
    public string? HostKeyFingerprint { get; set; }

    public string? HomeDirectory { get; set; }

    public int Cores { get; set; }

    public int MemoryMb { get; set; }

    public int DiskGb { get; set; }

    /// <summary>Comma separated harness ids to install, e.g. "pi".</summary>
    public string Harnesses { get; set; } = "";

    public int? SetupVersion { get; set; }

    /// <summary>Tool versions found after the last setup, e.g. "pi 0.87.1 · node v24.8.0 · tmux 3.5a".</summary>
    public string? InstalledVersions { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? LastSetupAt { get; set; }

    public List<Agent> Agents { get; set; } = [];

    public IReadOnlyList<string> HarnessIds => Harnesses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public RunnerMachine ToMachine() => new(Id, Name, MachineId, ProviderData, Host, SshPort);
}

public class Agent
{
    public Guid Id { get; set; }

    public Guid RunnerId { get; set; }

    public Runner? Runner { get; set; }

    public string Name { get; set; } = "";

    public string HarnessId { get; set; } = "";

    public string WorkingDirectory { get; set; } = "";

    public string? RepositoryUrl { get; set; }

    public string? RepositoryBranch { get; set; }

    /// <summary>Name of the tmux session the harness runs in.</summary>
    public string TmuxSession { get; set; } = "";

    /// <summary>The harness's own session id, so a restart resumes the same conversation.</summary>
    public string HarnessSessionId { get; set; } = "";

    public AgentState State { get; set; }

    public string? StateMessage { get; set; }

    public int? ExitCode { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public class SettingEntry
{
    public string Key { get; set; } = "";

    public string? Value { get; set; }

    public DateTime UpdatedAt { get; set; }
}
