namespace DeveAgentManager.Client.Shared;

public static class SessionSize
{
    public static string Bytes(long value) => value >= 1048576 ? $"{value / 1048576.0:0.#} MiB" : $"{value / 1024.0:0.#} KiB";
    public static string Text(DeveAgentManager.Contracts.SessionDto session) => session.LastSyncedAt is null
        ? "Not synced yet"
        : $"Current harness file {Bytes(session.SourceSize)} · Saved history + backups {Bytes(session.TotalSize)}";
}
