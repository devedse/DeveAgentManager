using DeveAgentManager.Core.Abstractions;

namespace DeveAgentManager.Core.Editor;

/// <summary>
/// VS Code in the browser: code-server (https://github.com/coder/code-server), installed for the Runner Account in
/// ~/.local (no sudo). The Manager starts it on first use, on the Runner's loopback only and with a password of its
/// own, and reaches it through an SSH tunnel (see <see cref="EditorTunnels"/>).
/// </summary>
public sealed class VsCodeTool : IRunnerTool
{
    public const string ToolId = "vscode";

    /// <summary>Where code-server listens on the Runner (127.0.0.1 only).</summary>
    public const int Port = 13337;

    /// <summary>Its config (bind address, password), log and process id. Only readable by the Runner Account.</summary>
    public const string StateDirectory = "$HOME/.deveagentmanager/code-server";

    public string Id => ToolId;

    public string DisplayName => "VS Code";

    public string Description => "VS Code in the browser (code-server): browse files, edit and see git changes, on the Runner or in an agent's folder.";

    /// <summary>The Visual Studio Code logo (Simple Icons, CC0) in its blue.</summary>
    public string Icon => """<path fill="#1F9CF0" d="M23.15 2.587L18.21.21a1.494 1.494 0 0 0-1.705.29l-9.46 8.63-4.12-3.128a.999.999 0 0 0-1.276.057L.327 7.261A1 1 0 0 0 .326 8.74L3.899 12 .326 15.26a1 1 0 0 0 .001 1.479L1.65 17.94a.999.999 0 0 0 1.276.057l4.12-3.128 9.46 8.63a1.492 1.492 0 0 0 1.704.29l4.942-2.377A1.5 1.5 0 0 0 24 20.06V3.939a1.5 1.5 0 0 0-.85-1.352zm-5.146 14.861L10.826 12l7.178-5.448v10.896z"/>""";

    /// <remarks>
    /// The official install script's standalone method: a release tarball under ~/.local, linked as
    /// ~/.local/bin/code-server (on the PATH through env.sh). Running it again updates. Older versions are removed, and a
    /// running code-server is stopped so the next open starts the new version.
    /// </remarks>
    public string InstallScript => $$"""
        set -euo pipefail
        echo "==> Installing VS Code (code-server)"
        curl -fsSL https://code-server.dev/install.sh | sh -s -- --method=standalone --prefix="$HOME/.local"
        current="$(readlink -f "$HOME/.local/bin/code-server")"
        for old in "$HOME"/.local/lib/code-server-*; do
          case "$current" in
            "$old"/*) ;;
            *) echo "Removing $old"; rm -rf "$old" ;;
          esac
        done
        {{StopScript}}
        {{VersionCommand}}
        """;

    /// <remarks>An empty config, or code-server writes a default one to ~/.config the first time it runs.</remarks>
    public string VersionCommand => "CODE_SERVER_CONFIG=/dev/null code-server --version | head -n1 | cut -d' ' -f1";

    /// <summary>Stops the code-server the Manager started, if it runs (checked by its process id, not by name).</summary>
    public static string StopScript => $$"""
        state="{{StateDirectory}}"
        if [ -f "$state/pid" ]; then
          pid="$(cat "$state/pid")"
          if ps -p "$pid" -o args= 2>/dev/null | grep -q code-server; then
            echo "Stopping the running VS Code (it starts again with the new version when it is opened)"
            kill "$pid" 2>/dev/null || true
            for _ in $(seq 1 20); do ps -p "$pid" >/dev/null 2>&1 || break; sleep 0.25; done
          fi
          rm -f "$state/pid"
        fi
        """;

    /// <summary>
    /// Starts code-server when it isn't running and prints its password. The password is made once and kept in the
    /// config file (mode 600), so other users on the machine can't use code-server even though it listens on 127.0.0.1.
    /// </summary>
    public static string EnsureRunningScript => $$"""
        set -euo pipefail
        state="{{StateDirectory}}"
        mkdir -p "$state"
        chmod 700 "$state"
        alive() { curl -fsS -m 3 -o /dev/null "http://127.0.0.1:{{Port}}/healthz"; }
        if ! alive; then
          # Written at every start (a new Manager may add options); the password stays.
          password="$(sed -n 's/^password: //p' "$state/config.yaml" 2>/dev/null || true)"
          [ -n "$password" ] || password="$(head -c 32 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')"
          (umask 077; printf 'bind-addr: 127.0.0.1:%s\nauth: password\npassword: %s\ncert: false\ndisable-telemetry: true\ndisable-update-check: true\ndisable-getting-started-override: true\n' {{Port}} "$password" > "$state/config.yaml")
          if ! command -v code-server >/dev/null 2>&1; then
            echo "VS Code (code-server) is not installed on this Runner. Install it on the Runner page, under Operations." >&2
            exit 3
          fi
          setsid nohup code-server --config "$state/config.yaml" > "$state/code-server.log" 2>&1 < /dev/null &
          echo $! > "$state/pid"
          for _ in $(seq 1 80); do alive && break; sleep 0.25; done
          if ! alive; then
            echo "VS Code (code-server) did not start:" >&2
            tail -n 20 "$state/code-server.log" >&2
            exit 4
          fi
        fi
        sed -n 's/^password: //p' "$state/config.yaml"
        """;
}
