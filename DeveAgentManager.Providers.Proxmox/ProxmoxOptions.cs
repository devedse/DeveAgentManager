using Microsoft.Extensions.Configuration;

namespace DeveAgentManager.Providers.Proxmox;

/// <summary>Proxmox settings from the compose file (PROXMOX_* and TEMPLATE_* environment variables).</summary>
public sealed class ProxmoxOptions
{
    public string? Url { get; init; }

    public string Host { get; init; } = "";

    public int Port { get; init; } = 8006;

    public string? TokenId { get; init; }

    public string? TokenSecret { get; init; }

    public bool VerifyTls { get; init; } = true;

    /// <summary>Node to create Runners on. Empty: the node the template lives on.</summary>
    public string? Node { get; init; }

    public string? Pool { get; init; }

    /// <summary>Storage for full clones. Empty: the template's storage.</summary>
    public string? Storage { get; init; }

    /// <summary>Bridge for the Runner's first NIC. Empty: keep the template's bridge.</summary>
    public string? Bridge { get; init; }

    public int VmIdStart { get; init; } = 30000;

    public int VmIdEnd { get; init; } = 30999;

    public bool FullClone { get; init; } = true;

    public int? HeadlessTemplateVmId { get; init; }

    public int? DesktopTemplateVmId { get; init; }

    public string ApiToken => $"{TokenId}={TokenSecret}";

    public bool IsConfigured =>
        !string.IsNullOrEmpty(Host)
        && !string.IsNullOrEmpty(TokenId)
        && !string.IsNullOrEmpty(TokenSecret)
        && (HeadlessTemplateVmId is not null || DesktopTemplateVmId is not null);

    public static ProxmoxOptions FromConfiguration(IConfiguration configuration)
    {
        var url = Value(configuration, "PROXMOX_URL");
        var host = "";
        var port = 8006;
        if (url is not null)
        {
            var uri = new Uri(url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url);
            host = uri.Host;
            port = uri.IsDefaultPort ? 8006 : uri.Port;
        }

        var (start, end) = ParseRange(Value(configuration, "PROXMOX_VMID_RANGE"));
        return new ProxmoxOptions
        {
            Url = url,
            Host = host,
            Port = port,
            TokenId = Value(configuration, "PROXMOX_TOKEN_ID"),
            TokenSecret = Value(configuration, "PROXMOX_TOKEN_SECRET"),
            VerifyTls = Bool(configuration, "PROXMOX_VERIFY_TLS", true),
            Node = Value(configuration, "PROXMOX_NODE"),
            Pool = Value(configuration, "PROXMOX_POOL"),
            Storage = Value(configuration, "PROXMOX_STORAGE"),
            Bridge = Value(configuration, "PROXMOX_BRIDGE"),
            VmIdStart = start,
            VmIdEnd = end,
            FullClone = Bool(configuration, "PROXMOX_FULL_CLONE", true),
            HeadlessTemplateVmId = int.TryParse(Value(configuration, "TEMPLATE_HEADLESS_VMID"), out var headless) ? headless : null,
            DesktopTemplateVmId = int.TryParse(Value(configuration, "TEMPLATE_DESKTOP_VMID"), out var desktop) ? desktop : null,
        };
    }

    internal static (int Start, int End) ParseRange(string? value)
    {
        var parts = value?.Split('-', StringSplitOptions.TrimEntries);
        if (parts is [var a, var b] && int.TryParse(a, out var start) && int.TryParse(b, out var end) && start >= 100 && end >= start)
        {
            return (start, end);
        }

        return (30000, 30999);
    }

    private static string? Value(IConfiguration configuration, string key) =>
        string.IsNullOrWhiteSpace(configuration[key]) ? null : configuration[key]!.Trim();

    private static bool Bool(IConfiguration configuration, string key, bool fallback) =>
        Value(configuration, key)?.ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => fallback,
        };
}
