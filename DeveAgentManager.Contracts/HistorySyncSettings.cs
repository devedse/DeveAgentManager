namespace DeveAgentManager.Contracts;

public enum HistorySyncMode { Sampled, Full, Shadow }

public sealed record HistorySyncSettings(HistorySyncMode Mode = HistorySyncMode.Sampled);
