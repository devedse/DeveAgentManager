using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Setup;
using DeveAgentManager.Core.Ssh;
using Microsoft.Extensions.DependencyInjection;

namespace DeveAgentManager.Harnesses.Grok;

/// <summary>xAI's official Grok Build CLI, not the similarly named third-party npm CLIs.</summary>
public sealed class GrokHarness(HttpMessageHandler? tokenHandler = null) : IHarness
{
    public const string HarnessId = "grok";
    public const string NpmPackage = "@xai-official/grok";
    public const string PrepareScript = "$HOME/.deveagentmanager/grok-prepare.py";
    public const string SettingsFile = "$HOME/.deveagentmanager/grok-config.json";
    private readonly HttpClient http = new(tokenHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    public string Id => HarnessId;
    public string DisplayName => "Grok";
    public string Description => "xAI's Grok Build CLI. Connect a Grok login or xAI API key under My settings → Harness logins.";
    public string Icon => """<path d="M5 3h4l10 18h-4L5 3Zm14 0h-4L5 21h4L19 3Z"/>""";
    public string VersionCommand => "grok --version";
    public string NewSessionId(Guid agentId) => Guid.CreateVersion7().ToString();
    public string BusyPattern => "^[[:space:]]*[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏].*\\[stop\\]";
    public string InstallScript => $$"""
        set -euo pipefail
        npm install -g --no-fund --no-audit {{NpmPackage}}@latest
        grok --version
        mkdir -p "$HOME/.deveagentmanager" "$HOME/.grok"
        cat > "{{PrepareScript}}" <<'DAM_GROK_PREPARE'
        {{PrepareSource}}
        DAM_GROK_PREPARE
        """;

    public static string PrepareSource
    {
        get
        {
            using var reader = new StreamReader(typeof(GrokHarness).Assembly.GetManifestResourceStream(
                "DeveAgentManager.Harnesses.Grok.Scripts.prepare.py")!);
            return reader.ReadToEnd();
        }
    }

    public string BuildLaunchCommand(HarnessLaunch launch)
    {
        var id = ShellQuote.Quote(launch.SessionId);
        var socketId = Guid.Parse(launch.SessionId).ToString("N");
        var flags = string.IsNullOrWhiteSpace(launch.Instructions) ? "" : " --rules " + ShellQuote.Quote(launch.Instructions);
        // The conversation keeps its model (current_model_id) but not its effort, so the model is given when it starts
        // here and the effort at every start. Versions before the effort flag would refuse to start with it.
        var model = string.IsNullOrWhiteSpace(launch.Model) ? "" : "model=(--model " + ShellQuote.Quote(launch.Model.Trim()) + ")";
        var starts = launch.Transferred ? "true" : "[ \"$mode\" = --session-id ]";
        var defaults = (model.Length == 0 ? "" : $"\nif {starts}; then {model}; fi")
            + (string.IsNullOrWhiteSpace(launch.Effort) ? ""
                : $"\nif grok --help 2>/dev/null | grep -- '--reasoning-effort' >/dev/null; then effort=(--reasoning-effort {ShellQuote.Quote(launch.Effort.Trim())}); fi");
        var script = $$"""
            set -euo pipefail
            export GROK_DISABLE_AUTOUPDATER=1
            socket_dir="/tmp/dam-grok-$(id -u)-{{socketId}}"
            mkdir -p -m 700 "$socket_dir"
            if [ -f "{{SettingsFile}}" ]; then export GROK_CONFIG_PATH="{{SettingsFile}}"; fi
            mode=$(python3 "{{PrepareScript}}" {{id}} {{ShellQuote.Quote(launch.DisplayName)}})
            model=()
            effort=(){{defaults}}
            exec grok --leader-socket "$socket_dir/leader.sock" --trust --always-approve --fullscreen "$mode" {{id}} "${model[@]}" "${effort[@]}"{{flags}}
            """;
        return "bash -c " + ShellQuote.Quote(script);
    }

    public IReadOnlyList<string> ConfigFiles { get; } = ["~/.grok/config.toml", "~/.grok/AGENTS.md"];
    public HarnessConfigSupport ConfigSupport { get; } = new("", [], "~/.deveagentmanager/grok-config.json", "",
        ["low", "medium", "high"], "https://docs.x.ai/build/overview",
        HasPackages: false, HasProvider: false, HasKeybindings: false, HasExtraSettings: false, EffortName: "Reasoning effort");

    public string ModelsCommand => "GROK_DISABLE_AUTOUPDATER=1 grok models";
    public IReadOnlyList<ModelChoice> ParseModels(string output) => output.Split('\n')
        .Select(line => System.Text.RegularExpressions.Regex.Match(line, @"^\s+[*-]\s+(\S+)(?:\s+\(default\))?\s*$"))
        .Where(match => match.Success).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal)
        .Select(id => new ModelChoice(id, id, [])).ToList();

    public string BuildConfigureScript(HarnessConfig config)
    {
        config.Validate();
        var models = new JsonObject();
        if (!string.IsNullOrWhiteSpace(config.DefaultModel)) models["default"] = config.DefaultModel.Trim();
        if (!string.IsNullOrWhiteSpace(config.DefaultThinkingLevel)) models["default_reasoning_effort"] = config.DefaultThinkingLevel.Trim();
        var json = new JsonObject { ["models"] = models }.ToJsonString();
        return $"mkdir -p \"$HOME/.deveagentmanager\"\nprintf %s {ShellQuote.Quote(json)} > \"{SettingsFile}\"\n";
    }

    public HarnessAccountSupport AccountSupport { get; } = new(
        "Grok account", "GROK_HOME=\"$DAM_LOGIN_DIR\" GROK_AUTH_PATH=\"$DAM_LOGIN_DIR/auth.json\" env -u GROK_AUTH -u XAI_API_KEY -u GROK_CODE_XAI_API_KEY grok login --device-auth",
        "Open the link on any device, sign in to Grok and enter the code. The Manager saves the login when Grok finishes.",
        null, "auth.json", "XAI_API_KEY", null);

    public HarnessAccountSetup BuildAccountSetup(string accountFolder, AccountKind kind, string secret)
    {
        var home = accountFolder + "/grok";
        var environment = $"export GROK_HOME=\"{home}\"\nexport GROK_AUTH_PATH=\"{home}/auth.json\"\nunset GROK_AUTH XAI_API_KEY GROK_CODE_XAI_API_KEY\n";
        var auth = kind == AccountKind.Subscription ? RunnerAccountCopy(secret) : "{}";
        if (kind != AccountKind.Subscription) environment += "export XAI_API_KEY=" + ShellQuote.Quote(secret.Trim()) + "\n";
        return new([("grok/auth.json", auth, "600")], environment, $"mkdir -p \"{home}\" \"$HOME/.grok\"\n");
    }

    public bool RenewsAccounts => true;
    public string RunnerAccountCopy(string secret)
    {
        var auth = JsonNode.Parse(secret)?.AsObject() ?? throw new FormatException("Invalid Grok login.");
        foreach (var value in auth.Select(p => p.Value).OfType<JsonObject>()) value.Remove("refresh_token");
        return auth.ToJsonString();
    }

    public async Task<string?> RenewAccountAsync(string secret, CancellationToken cancellationToken)
    {
        var store = JsonNode.Parse(secret)?.AsObject() ?? throw new FormatException("Invalid Grok login.");
        var changed = false;
        foreach (var auth in store.Select(p => p.Value).OfType<JsonObject>())
        {
            if ((string?)auth["refresh_token"] is not { Length: > 0 } refresh) continue;
            var expires = DateTimeOffset.TryParse((string?)auth["expires_at"], out var at) ? at
                : DateTimeOffset.TryParse((string?)auth["create_time"], out var created) ? created.AddDays(30) : DateTimeOffset.MinValue;
            // Background renewal runs every ten minutes, and Grok treats the final five minutes as expired.
            if (expires > DateTimeOffset.UtcNow.AddMinutes(20)) continue;
            // Only the official login issuer is supported; never send stored credentials to an arbitrary pasted URL.
            if ((string?)auth["oidc_issuer"] != "https://auth.x.ai" || (string?)auth["oidc_client_id"] is not { Length: > 0 } client)
                throw new AccountRenewalException("Reconnect this Grok login using device authentication.", true);
            var discovery = await http.GetFromJsonAsync<JsonObject>("https://auth.x.ai/.well-known/openid-configuration", cancellationToken);
            if (!Uri.TryCreate((string?)discovery?["token_endpoint"], UriKind.Absolute, out var endpoint)
                || endpoint.Scheme != "https" || endpoint.Host != "auth.x.ai" || !endpoint.IsDefaultPort || endpoint.UserInfo.Length > 0)
                throw new AccountRenewalException("Grok returned an unsupported token endpoint.", false);
            var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = client };
            foreach (var key in new[] { "principal_type", "principal_id" })
                if ((string?)auth[key] is { Length: > 0 } value) form[key] = value;
            using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(form), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string? code = null;
                try { code = (string?)JsonNode.Parse(body)?["error"]; } catch (System.Text.Json.JsonException) { }
                throw new AccountRenewalException("Grok login renewal failed. " + (code is "invalid_grant" or "invalid_client" ? "Connect the account again." : "The Manager will retry."), code is "invalid_grant" or "invalid_client");
            }
            var tokens = JsonNode.Parse(body)?.AsObject();
            if ((string?)tokens?["access_token"] is not { Length: > 0 } access)
                throw new AccountRenewalException("Grok did not return an access token.", false);
            auth["key"] = access;
            auth["refresh_token"] = (string?)tokens["refresh_token"] ?? refresh;
            auth["create_time"] = DateTimeOffset.UtcNow.ToString("O");
            auth["expires_at"] = DateTimeOffset.UtcNow.AddSeconds((long?)tokens["expires_in"] ?? 3600).ToString("O");
            changed = true;
        }
        return changed ? store.ToJsonString() : null;
    }
}

public static class GrokServiceCollectionExtensions
{
    public static IServiceCollection AddGrokHarness(this IServiceCollection services) =>
        services.AddSingleton<IHarness, GrokHarness>().AddSingleton<ISessionFormat, GrokSessionFormat>();
}
