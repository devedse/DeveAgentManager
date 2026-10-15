using System.Text.Json;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;

namespace DeveAgentManager.Core.Accounts;

public sealed partial class AccountService
{
    public static string RuntimeFolder(Agent agent) => $"$HOME/.deveagentmanager/agent-runtime/{agent.OwnerId:N}/{agent.Id:N}/{agent.HarnessId}";

    public async Task<string> PrepareRuntimeAsync(Runner runner, Agent agent, Account account, CancellationToken ct)
    {
        if (agent.HarnessId is not ("claude" or "codex")) return "";
        (await shell.RunAsync(runner, $". \"{EnvironmentFile(account.Id)}\"\n" + RuntimeScript(agent, account.Id), TimeSpan.FromSeconds(30), ct)).EnsureSuccess("Preparing private harness runtime");
        return agent.HarnessId == "codex"
            ? $"export CODEX_HOME=\"{RuntimeFolder(agent)}\"; "
            : $"export CLAUDE_CONFIG_DIR=\"{RuntimeFolder(agent)}\"; ";
    }

    internal static string RuntimeScript(Agent agent, Guid accountId)
    {
        var data = JsonSerializer.Serialize(new { Owner = agent.OwnerId.ToString("N"), Agent = agent.Id.ToString("N"),
            Harness = agent.HarnessId, Session = agent.HarnessSessionId, WorkingDirectory = agent.WorkingDirectory, Account = accountId.ToString("N") });
        return $$"""
            python3 - <<'DAM_RUNTIME'
            import pathlib,json,shutil,os
            d=json.loads({{JsonSerializer.Serialize(data)}})
            home=pathlib.Path.home()
            base=home/'.deveagentmanager'/'agent-runtime'
            root=base/d['Owner']/d['Agent']/d['Harness']
            root.mkdir(parents=True,exist_ok=True,mode=0o700)
            os.chmod(root,0o700)
            shared=home/('.codex' if d['Harness']=='codex' else '.claude')
            # Only shared settings/packages, never another user's history or mutable state databases.
            names=['config.toml','skills','AGENTS.md','packages'] if d['Harness']=='codex' else ['settings.json','CLAUDE.md','commands','skills','plugins']
            for name in names:
                src=shared/name
                dst=root/name
                if src.exists() and not dst.exists(): dst.symlink_to(src)
            if d['Harness']=='codex':
                auth=root/'auth.json'
                if auth.is_symlink() or auth.exists(): auth.unlink()
                auth.symlink_to(home/'.deveagentmanager'/'accounts'/d['Account']/'codex'/'auth.json')
                pattern='sessions/*/*/*/rollout-*-'+d['Session']+'.jsonl'
            else:
                # CLAUDE_CONFIG_DIR has its own onboarding/trust state. Never copy another user's
                # global account state, OAuth credentials or project history into this runtime.
                statefile=root/'.claude.json'
                state=json.loads(statefile.read_text()) if statefile.exists() else {}
                state['hasCompletedOnboarding']=True
                state.setdefault('projects',{}).setdefault(d['WorkingDirectory'],{})['hasTrustDialogAccepted']=True
                key=os.environ.get('ANTHROPIC_API_KEY')
                if key:
                    answers=state.setdefault('customApiKeyResponses',{'approved':[],'rejected':[]})
                    suffix=key[-20:]
                    if suffix not in answers['approved']: answers['approved'].append(suffix)
                    answers['rejected']=[x for x in answers['rejected'] if x!=suffix]
                statefile.write_text(json.dumps(state))
                os.chmod(statefile,0o600)
                settings=root/'settings.json'
                if not settings.exists(): settings.write_text(json.dumps({'skipDangerousModePermissionPrompt':True}))
                pattern='projects/*/'+d['Session']+'.jsonl'
            # Bootstrap resume from the legacy/upload location for this authorized session only.
            # New observations stay under this agent's runtime; no whole sessions directory is shared.
            for src in shared.glob(pattern):
                dst=root/src.relative_to(shared)
                dst.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
                if not dst.exists() or src.stat().st_mtime_ns>dst.stat().st_mtime_ns:
                    shutil.copy2(src,dst)
            DAM_RUNTIME
            """;
    }
}
