using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeveAgentManager.Core.Runners;

/// <summary>Public release metadata shared by every Runner; never uses an operator's npm config or credentials.</summary>
public sealed class RunnerReleaseCatalog
{
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly TimeProvider _clock;
    private readonly HttpClient _client;
    public RunnerReleaseCatalog(TimeProvider clock) : this(clock, SharedClient) { }
    internal RunnerReleaseCatalog(TimeProvider clock, HttpClient client) { _clock = clock; _client = client; }
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, Release> _releases = [];
    internal sealed record Release(string? Version, string? Error, DateTime At);
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeveAgentManager/1.0");
        return client;
    }
    internal async Task<Release> LatestAsync(string source, bool github, CancellationToken ct, bool refresh = false)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            if (_releases.TryGetValue(source, out var last) && now - last.At < (refresh ? TimeSpan.FromMinutes(1) : last.Error is null ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(10))) return last;
            try
            {
                var url = github ? $"https://api.github.com/repos/{source}/releases/latest"
                    : $"https://registry.npmjs.org/-/package/{Uri.EscapeDataString(source)}/dist-tags";
                using var response = await _client.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var version = json.RootElement.GetProperty(github ? "tag_name" : "latest").GetString();
                version = NormalizeVersion(version) ?? throw new FormatException("Release has no valid version.");
                return _releases[source] = new(version, null, now);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or FormatException)
            {
                return _releases[source] = new(last?.Version, "Latest release unavailable", now);
            }
        }
        finally { _gate.Release(); }
    }

    internal static string? NormalizeVersion(string? value) => value is null ? null :
        Regex.Match(value, @"(?<![\w.])v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)(?![\w.])") is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>SemVer precedence: newer releases only, including prerelease ordering; build metadata is ignored.</summary>
    internal static bool? IsNewer(string? installed, string? latest)
    {
        installed = NormalizeVersion(installed); latest = NormalizeVersion(latest);
        if (installed is null || latest is null) return null;
        var a = installed.Split('+')[0].Split('-', 2); var b = latest.Split('+')[0].Split('-', 2);
        if (!Version.TryParse(a[0], out var av) || !Version.TryParse(b[0], out var bv)) return null;
        var compared = bv.CompareTo(av);
        if (compared != 0) return compared > 0;
        if (a.Length == 1 || b.Length == 1) return a.Length > b.Length;
        var ai = a[1].Split('.'); var bi = b[1].Split('.');
        for (var i = 0; i < Math.Min(ai.Length, bi.Length); i++)
        {
            var an = System.Numerics.BigInteger.TryParse(ai[i], out var anum);
            var bn = System.Numerics.BigInteger.TryParse(bi[i], out var bnum);
            compared = an && bn ? bnum.CompareTo(anum) : an != bn ? (an ? 1 : -1) : string.CompareOrdinal(bi[i], ai[i]);
            if (compared != 0) return compared > 0;
        }
        return bi.Length > ai.Length;
    }
}
