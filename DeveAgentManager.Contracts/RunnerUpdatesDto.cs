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
    bool Checking = false,
    IReadOnlyList<RunnerSystemPackageUpdateDto>? SystemPackages = null,
    RunnerKernelDto? Kernel = null)
{
    public const int MaxSystemPackageDetails = 200;
    public int SoftwareCount => Software.Count(s => s.UpdateAvailable == true);
    /// <summary>A restart would load a newer installed kernel or finish updates the system says need one (Restart's badge).</summary>
    public bool RestartPending => RebootRequired || Kernel?.Installed is not null;
    public bool SoftwareKnown => SoftwareCheckedAt is not null && SoftwareError is null && Software.All(s => s.UpdateAvailable is not null && s.Error is null);
}

public sealed record RunnerSoftwareUpdateDto(string Id, string Name, string? InstalledVersion, string? LatestVersion,
    bool? UpdateAvailable, string? Error = null);

public sealed record RunnerSystemPackageUpdateDto(string Name, string? InstalledVersion, string LatestVersion);

/// <summary>
/// Kernel releases as <c>uname -r</c> prints them. <see cref="Installed"/>: the newest installed kernel when newer than the
/// running one (often installed by unattended-upgrades; a restart loads it). <see cref="Available"/>: a newer one that
/// dist-upgrade (Update all) would install. Null when there is none.
/// </summary>
public sealed record RunnerKernelDto(string Running, string? Installed = null, string? Available = null);
