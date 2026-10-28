using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;

namespace DeveAgentManager.Core.Accounts;

public sealed partial class AccountService
{
    internal static string ExistingPiRuntimeEnvironment(Agent agent) =>
        $"if [ -d \"{RuntimeFolder(agent)}\" ]; then export PI_CODING_AGENT_DIR=\"{RuntimeFolder(agent)}\"; fi; ";

    internal static string PiManagedExtension
    {
        get
        {
            using var stream = typeof(AccountService).Assembly.GetManifestResourceStream("DeveAgentManager.Core.Scripts.pi-managed-login.ts")!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public async Task<string> PreparePiManagedRuntimeAsync(Runner runner, Agent agent, Account account, CancellationToken ct)
    {
        if (!PiManagedLogin.Supports(account.HarnessId, account.Kind)) throw new ArgumentException("Choose a Claude or Codex subscription login for pi.");
        var environment = await PreparePiWorkspaceAsync(runner, agent, ct);
        var root = RuntimeFolder(agent);
        if (account.HarnessId == "claude")
            (await shell.RunAsync(runner, PiManagedRuntimeScript(agent), TimeSpan.FromSeconds(20), ct)).EnsureSuccess("Preparing pi managed login");
        await shell.UploadAsync(runner, root.Replace("$HOME/", "~/") + "/dam-managed-login.ts", PiManagedExtension, "600", ct);
        environment += $"export DAM_PI_LOGIN_PROVIDER={PiManagedLogin.Provider(account.HarnessId)}; export DAM_PI_LOGIN_EXTENSION=\"{root}/dam-managed-login.ts\"; ";
        if (account.HarnessId == "claude")
        {
            // The CLI bridge gets the selected token, with its own settings/history and no other agent's login.
            environment += $"export CLAUDE_CONFIG_DIR=\"{root}/claude-code\"; export PI_CLAUDE_CODE_CONFIG_DIR=\"{root}/claude-code\"; ";
            environment += "command -v claude >/dev/null || { echo 'Install Claude Code on this Runner before using its pi login.' >&2; exit 1; }; ";
        }
        else
        {
            // The bridge reads the Manager-maintained file for each request; it never receives a refresh token.
            environment += $"export DAM_PI_CODEX_AUTH_FILE=\"{Folder(account.Id)}/codex/auth.json\"; ";
        }
        return environment;
    }

    internal static string PiManagedRuntimeScript(Agent agent) => $$"""
        python3 - <<'DAM_PI_LOGIN'
        import json,os,pathlib
        root=pathlib.Path.home()/'.deveagentmanager'/'agent-runtime'/{{JsonSerializer.Serialize(agent.OwnerId.ToString("N"))}}/{{JsonSerializer.Serialize(agent.Id.ToString("N"))}}/'pi'/'claude-code'
        root.mkdir(parents=True,exist_ok=True,mode=0o700)
        state=root/'.claude.json'
        data=json.loads(state.read_text()) if state.exists() else {}
        data['hasCompletedOnboarding']=True
        data.setdefault('projects',{}).setdefault({{JsonSerializer.Serialize(agent.WorkingDirectory)}},{})['hasTrustDialogAccepted']=True
        state.write_text(json.dumps(data)); os.chmod(state,0o600)
        DAM_PI_LOGIN
        """;
}
