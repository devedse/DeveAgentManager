using DeveAgentManager.Core.Abstractions;

namespace DeveAgentManager.Core.Data;

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

    /// <summary>The account the harness runs with; null uses the harness's default account (or the Runner's own login).</summary>
    public Guid? AccountId { get; set; }

    public Account? Account { get; set; }

    public AgentState State { get; set; }

    public string? StateMessage { get; set; }

    public int? ExitCode { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A conversation stored by the Manager (the content is a DeveSession file in /data/sessions). It stays when its agent
/// or Runner is deleted, and can then be continued on another Runner.
/// </summary>
public class StoredSession
{
    public Guid Id { get; set; }

    public string Title { get; set; } = "";

    /// <summary>The harness it last ran in, e.g. "pi".</summary>
    public string HarnessId { get; set; } = "";

    /// <summary>The harness's own session id on the Runner.</summary>
    public string? HarnessSessionId { get; set; }

    /// <summary>The harnesses the conversation went through, oldest first, e.g. "pi,codex,claude".</summary>
    public string? HarnessTrail { get; set; }

    /// <summary>The agent currently running this session; null once the agent (or its Runner) is deleted.</summary>
    public Guid? AgentId { get; set; }

    public Agent? Agent { get; set; }

    public string? RunnerName { get; set; }

    public string? WorkingDirectory { get; set; }

    public string? RepositoryUrl { get; set; }

    public string? RepositoryBranch { get; set; }

    public int MessageCount { get; set; }

    /// <summary>Path and size of the harness's session file on the Runner at the last sync.</summary>
    public string? SourcePath { get; set; }

    public long SourceSize { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Last activity in the conversation.</summary>
    public DateTime UpdatedAt { get; set; }

    public DateTime? LastSyncedAt { get; set; }
}

public class SettingEntry
{
    public string Key { get; set; } = "";

    public string? Value { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A login for a harness (e.g. a Claude subscription), stored encrypted in the Manager and copied to Runners when an
/// agent uses it. A harness can have several (e.g. two Claude subscriptions, to switch when one hits its limit).
/// </summary>
public class Account
{
    public Guid Id { get; set; }

    public string HarnessId { get; set; } = "";

    public string Name { get; set; } = "";

    public AccountKind Kind { get; set; }

    /// <summary>The credential (a token, an API key or a harness's login file), encrypted.</summary>
    public string ProtectedSecret { get; set; } = "";

    /// <summary>Used by agents that don't pick an account.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Goes up when the credential is replaced (a new login), so Runners take the new one.</summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

