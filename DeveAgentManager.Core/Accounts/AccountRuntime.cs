using System.Text.Json;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;

namespace DeveAgentManager.Core.Accounts;

public sealed partial class AccountService
{
    public static string RuntimeFolder(Agent agent) => $"$HOME/.deveagentmanager/agent-runtime/{agent.OwnerId:N}/{agent.Id:N}/{agent.HarnessId}";

    public async Task<string> PreparePiWorkspaceAsync(Runner runner, Agent agent, CancellationToken ct)
    {
        (await shell.RunAsync(runner, PiWorkspaceScript(agent), TimeSpan.FromSeconds(30), ct)).EnsureSuccess("Preparing private pi workspace settings");
        return $"export PI_CODING_AGENT_DIR=\"{RuntimeFolder(agent)}\"; ";
    }

    internal static string PiWorkspaceScript(Agent agent) => $$"""
        python3 - <<'DAM_PI_WORKSPACE'
        import pathlib,shutil,os
        home=pathlib.Path.home()
        shared=home/'.pi'/'agent'
        root=home/'.deveagentmanager'/'agent-runtime'/{{JsonSerializer.Serialize(agent.OwnerId.ToString("N"))}}/{{JsonSerializer.Serialize(agent.Id.ToString("N"))}}/'pi'
        root.mkdir(parents=True,exist_ok=True,mode=0o700)
        # Mutable model/settings files are copied once; packages and Runner-level pi auth retain their existing scope.
        for name in ('settings.json','keybindings.json','models.json'):
            src=shared/name; dst=root/name
            if src.is_file() and not dst.exists():
                shutil.copy2(src,dst); os.chmod(dst,0o600)
        for name in ('auth.json','extensions','skills','prompts','AGENTS.md','packages'):
            src=shared/name; dst=root/name
            if src.exists() and not dst.exists() and not dst.is_symlink(): dst.symlink_to(src)
        # Only this selected conversation may be bootstrapped from the legacy upload location.
        for src in shared.glob('sessions/*/*_'+{{JsonSerializer.Serialize(agent.HarnessSessionId)}}+'.jsonl'):
            dst=root/src.relative_to(shared)
            dst.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
            if not dst.exists() or src.stat().st_mtime_ns>dst.stat().st_mtime_ns:
                shutil.copy2(src,dst); os.chmod(dst,0o600)
        DAM_PI_WORKSPACE
        """;

    public async Task<string> PrepareRuntimeAsync(Runner runner, Agent agent, Account account, CancellationToken ct)
    {
        if (agent.HarnessId is not ("claude" or "codex" or "grok")) return "";
        (await shell.RunAsync(runner, $". \"{EnvironmentFile(account.Id)}\"\n" + RuntimeScript(agent, account.Id), TimeSpan.FromSeconds(30), ct)).EnsureSuccess("Preparing private harness runtime");
        var variable = agent.HarnessId switch { "codex" => "CODEX_HOME", "grok" => "GROK_HOME", _ => "CLAUDE_CONFIG_DIR" };
        return $"export {variable}=\"{RuntimeFolder(agent)}\"; ";
    }

    internal static string RuntimeScript(Agent agent, Guid accountId)
    {
        var data = JsonSerializer.Serialize(new { Owner = agent.OwnerId.ToString("N"), Agent = agent.Id.ToString("N"),
            Harness = agent.HarnessId, Session = agent.HarnessSessionId, WorkingDirectory = agent.WorkingDirectory, Account = accountId.ToString("N"), Private = agent.IsClone });
        return $$"""
            python3 - <<'DAM_RUNTIME'
            import pathlib,json,shutil,os
            d=json.loads({{JsonSerializer.Serialize(data)}})
            home=pathlib.Path.home()
            base=home/'.deveagentmanager'/'agent-runtime'
            root=base/d['Owner']/d['Agent']/d['Harness']
            root.mkdir(parents=True,exist_ok=True,mode=0o700)
            os.chmod(root,0o700)
            shared=home/('.'+d['Harness'])
            # Only shared settings/packages, never another user's history or mutable state databases.
            names=['config.toml','skills','AGENTS.md','packages'] if d['Harness']=='codex' else (['config.toml','AGENTS.md','skills'] if d['Harness']=='grok' else ['settings.json','CLAUDE.md','commands','skills','plugins'])
            for name in names:
                src=shared/name
                dst=root/name
                if src.exists() and not dst.exists():
                    if d.get('Private') and name in ('config.toml','settings.json'):
                        shutil.copy2(src,dst); os.chmod(dst,0o600)
                    else: dst.symlink_to(src)
            if d['Harness']=='codex':
                auth=root/'auth.json'
                if auth.is_symlink() or auth.exists(): auth.unlink()
                auth.symlink_to(home/'.deveagentmanager'/'accounts'/d['Account']/'codex'/'auth.json')
                pattern='sessions/*/*/*/rollout-*-'+d['Session']+'.jsonl'
            elif d['Harness']=='grok':
                # Login is read from GROK_AUTH_PATH, outside the agent's private history directory.
                # Only this authorized session is restored; checkpoint files are part of its replay state.
                for src in shared.glob('sessions/*/'+d['Session']):
                    dst=root/src.relative_to(shared)
                    for original in src.rglob('*'):
                        if not original.is_file(): continue
                        target=dst/original.relative_to(src)
                        target.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
                        if not target.exists() or original.stat().st_mtime_ns>target.stat().st_mtime_ns:
                            shutil.copy2(original,target)
                            os.chmod(target,0o600)
                pattern='__none__'
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
