namespace DeveAgentManager.Contracts;

/// <summary>Last observations, retained while a refresh runs or fails. Null counts mean unknown, never zero.</summary>
public sealed record RunnerUpdatesDto(
    IReadOnlyList<RunnerSoftwareUpdateDto> Software,
    DateTime? SoftwareCheckedAt = null,
    string? SoftwareError = null,
    int? SystemPackageCount = null,
    DateTime? SystemCheckedAt = null,
    string? SystemError = null,
    bool RebootRequired = false,
    bool Checking = false)
{
    public int SoftwareCount => Software.Count(s => s.UpdateAvailable == true);
    public bool SoftwareKnown => SoftwareCheckedAt is not null && SoftwareError is null && Software.All(s => s.UpdateAvailable is not null && s.Error is null);
}

public sealed record RunnerSoftwareUpdateDto(string Id, string Name, string? InstalledVersion, string? LatestVersion,
    bool? UpdateAvailable, string? Error = null);
