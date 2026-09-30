using System.Text.Json;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;

namespace DeveAgentManager.Core.Agents;

public sealed record LimitScreen(string Screen, string Identity, string? TimeZone, bool InputEmpty, string Hash);

public interface ILimitTerminal
{
    Task<LimitScreen> ReadAsync(Agent agent, CancellationToken ct);
    Task<bool> SendContinueAsync(Agent agent, LimitScreen expected, CancellationToken ct);
}

/// <summary>Reads only the visible pane, not scrollback. Checks it again on the Runner immediately before typing.</summary>
public sealed class LimitTerminal(RunnerShell shell) : ILimitTerminal
{
    internal const string Probe = """
        import hashlib,json,os,pathlib,re,subprocess,sys
        target=sys.argv[1]
        def tmux(*args):
            return subprocess.check_output(['tmux',*args],text=True,stderr=subprocess.DEVNULL)
        def snapshot():
            pane,pid,dead,cy,cx=tmux('display-message','-p','-t',target,'#{pane_id}|#{pane_pid}|#{pane_dead}|#{cursor_y}|#{cursor_x}').strip().split('|')
            if dead!='0': raise RuntimeError('Harness is not running')
            process=pathlib.Path('/proc')/pid
            start=process.joinpath('stat').read_text().rsplit(')',1)[1].split()[19]
            identity=':'.join([pane,pid,start,pathlib.Path('/proc/sys/kernel/random/boot_id').read_text().strip()])
            zone=None
            try:
                env=process.joinpath('environ').read_bytes().split(b'\0')
                zone=next((x[3:].decode().lstrip(':') for x in env if x.startswith(b'TZ=')),None)
            except (OSError,UnicodeError): pass
            if not zone:
                path=str(pathlib.Path('/etc/localtime').resolve())
                if '/zoneinfo/' in path: zone=path.split('/zoneinfo/',1)[1]
                elif pathlib.Path('/etc/timezone').exists(): zone=pathlib.Path('/etc/timezone').read_text().strip()
            raw=tmux('capture-pane','-p','-t',target).splitlines()
            screen=tmux('capture-pane','-p','-J','-t',target)
            line=raw[int(cy)] if int(cy)<len(raw) else ''
            prompt=re.match(r'^(\s*[›❯] ?)(.*)$',line)
            # capture-pane trims the trailing space of an empty prompt, although the cursor is still after it.
            empty=bool(prompt and int(cx)<=len(prompt[1].rstrip())+1 and prompt[2].strip() in ('','Ask Codex to do anything'))
            return dict(Screen=screen,Identity=identity,TimeZone=zone,InputEmpty=empty,Hash=hashlib.sha256(screen.encode()).hexdigest())
        state=snapshot()
        """;

    public async Task<LimitScreen> ReadAsync(Agent agent, CancellationToken ct)
    {
        var result = (await shell.RunAsync(agent.Runner!, BuildReadScript(agent.TmuxSession), TimeSpan.FromSeconds(10), ct)).EnsureSuccess("Reading the usage limit");
        return JsonSerializer.Deserialize<LimitScreen>(result.Output.Trim()) ?? throw new InvalidOperationException("No terminal snapshot returned.");
    }

    public async Task<bool> SendContinueAsync(Agent agent, LimitScreen expected, CancellationToken ct)
    {
        var result = (await shell.RunAsync(agent.Runner!, BuildContinueScript(agent.TmuxSession, expected), TimeSpan.FromSeconds(10), ct)).EnsureSuccess("Sending continue");
        return result.Output.Trim() == "SENT";
    }

    internal static string BuildReadScript(string session) => Script(session, "print(json.dumps(state))");

    internal static string BuildContinueScript(string session, LimitScreen expected)
    {
        var expectedJson = JsonSerializer.Serialize(new { expected.Identity, expected.Hash });
        var code = $"""
            expected=json.loads({JsonSerializer.Serialize(expectedJson)})
            if state['Identity']!=expected['Identity'] or state['Hash']!=expected['Hash'] or not state['InputEmpty']:
                print('CHANGED')
            else:
                pane=state['Identity'].split(':')[0]
                tmux('send-keys','-t',pane,'-l','continue',';','send-keys','-t',pane,'Enter')
                print('SENT')
            """;
        return Script(session, code);
    }

    private static string Script(string session, string suffix) =>
        $"python3 - {ShellQuote.Quote("=" + session + ":")} <<'DAM_LIMIT'\n{Probe}\n{suffix}\nDAM_LIMIT";
}
