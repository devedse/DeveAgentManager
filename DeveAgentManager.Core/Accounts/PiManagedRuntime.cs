using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Accounts;

public sealed partial class AccountService
{
    /// <summary>Only explicitly granted, enabled subscription accounts; management roles alone never authorize forwarding.</summary>
    public async Task<List<Account>> ResolvePiLoginsAsync(Guid ownerId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count > 16 || ids.Contains(Guid.Empty)) throw new ArgumentException("Choose at most 16 saved pi logins.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Users.AnyAsync(u => u.Id == ownerId && !u.Disabled, ct)) throw new UnauthorizedAccessException("The pi agent owner is disabled or removed.");
        var rows = await db.Accounts.AsNoTracking().Include(a => a.CreatedBy).Where(a => ids.Contains(a.Id) && a.Enabled
            && a.Grants.Any(g => g.UserId == ownerId && !g.User.Disabled && g.Access >= AccountAccess.Use)).ToListAsync(ct);
        if (ids.Distinct().Count() != rows.Count || rows.Any(a => !PiManagedLogin.Supports(a.HarnessId, a.Kind)))
            throw new ArgumentException("A selected pi login is unavailable. Choose enabled Claude Code or Codex subscriptions you have Use access to in My settings → Models.");
        if (rows.Any(a => a.HarnessId == "codex" && !a.ManagedRenewal))
            throw new ArgumentException("Reconnect the selected Codex login so the Manager can keep it signed in.");
        return ids.Distinct().Select(id => rows.Single(a => a.Id == id)).ToList();
    }

    public async Task<string> PreparePiManagedRuntimeAsync(Runner runner, Agent agent, IReadOnlyList<Account> logins, CancellationToken ct)
    {
        var environment = await PreparePiWorkspaceAsync(runner, agent, ct);
        var root = RuntimeFolder(agent);
        var manifest = new List<object>();
        foreach (var login in logins)
        {
            if (!PiManagedLogin.Supports(login.HarnessId, login.Kind)) throw new ArgumentException("Choose a Claude or Codex subscription login for pi.");
            var provider = PiManagedLogin.AccountProvider(login.HarnessId, login.Id);
            var config = $"{root}/claude-{login.Id:N}";
            var command = $"{root}/claude-{login.Id:N}.sh";
            if (login.HarnessId == "claude")
            {
                (await shell.RunAsync(runner, PiManagedRuntimeScript(agent, $"claude-{login.Id:N}"), TimeSpan.FromSeconds(20), ct)).EnsureSuccess("Preparing pi Claude login");
                // Credentials are read afresh by each CLI child, never exported into pi or copied to its auth.json.
                var wrapper = $"""
                    #!/bin/bash
                    set -e
                    unset CLAUDE_CODE_OAUTH_TOKEN ANTHROPIC_API_KEY ANTHROPIC_AUTH_TOKEN ANTHROPIC_BASE_URL
                    unset CLAUDE_CODE_USE_BEDROCK CLAUDE_CODE_USE_VERTEX CLAUDE_CODE_USE_FOUNDRY
                    . "{EnvironmentFile(login.Id)}"
                    export CLAUDE_CONFIG_DIR="{config}"
                    exec claude "$@"
                    """;
                await shell.UploadAsync(runner, command.Replace("$HOME/", "~/"), wrapper + "\n", "700", ct);
            }
            manifest.Add(new { provider, harness = login.HarnessId,
                name = $"{(login.HarnessId == "claude" ? "Claude Code" : "Codex")} · {login.CreatedBy?.Username ?? "User"} · {login.Name}",
                authFile = login.HarnessId == "codex" ? $"{Folder(login.Id)}/codex/auth.json" : EnvironmentFile(login.Id),
                command, config });
        }
        await shell.UploadAsync(runner, root.Replace("$HOME/", "~/") + "/dam-managed-login.ts", PiManagedExtension, "600", ct);
        await shell.UploadAsync(runner, root.Replace("$HOME/", "~/") + "/dam-logins.json", JsonSerializer.Serialize(new {
            logins = manifest,
            // Resume old preview conversations only on the same account, never an arbitrary replacement.
            legacyProvider = agent.AccountId is { } old && logins.Any(a => a.Id == old)
                ? PiManagedLogin.AccountProvider(logins.Single(a => a.Id == old).HarnessId, old) : null
        }), "600", ct);
        if (logins.Any(a => a.HarnessId == "claude"))
            environment += "command -v claude >/dev/null || { echo 'Install Claude Code on this Runner before using its pi login.' >&2; exit 1; }; ";
        return environment + $"export DAM_PI_LOGIN_MANIFEST=\"{root}/dam-logins.json\"; export DAM_PI_LOGIN_EXTENSION=\"{root}/dam-managed-login.ts\"; ";
    }
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

    internal static string PiManagedRuntimeScript(Agent agent, string folder = "claude-code") => $$"""
        python3 - <<'DAM_PI_LOGIN'
        import json,os,pathlib
        root=pathlib.Path.home()/'.deveagentmanager'/'agent-runtime'/{{JsonSerializer.Serialize(agent.OwnerId.ToString("N"))}}/{{JsonSerializer.Serialize(agent.Id.ToString("N"))}}/'pi'/{{JsonSerializer.Serialize(folder)}}
        root.mkdir(parents=True,exist_ok=True,mode=0o700)
        state=root/'.claude.json'
        data=json.loads(state.read_text()) if state.exists() else {}
        data['hasCompletedOnboarding']=True
        data.setdefault('projects',{}).setdefault({{JsonSerializer.Serialize(agent.WorkingDirectory)}},{})['hasTrustDialogAccepted']=True
        state.write_text(json.dumps(data)); os.chmod(state,0o600)
        DAM_PI_LOGIN
        """;
}
