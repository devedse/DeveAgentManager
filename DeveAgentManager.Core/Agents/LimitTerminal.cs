using System.Text.Json;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;

namespace DeveAgentManager.Core.Agents;

public sealed record LimitScreen(string Screen, string Identity, string? TimeZone, bool InputEmpty, string Hash, DateTime? ObservedAt = null);

/// <summary>What happened when typing continue.</summary>
public enum ContinueDelivery
{
    /// <summary>Typed and submitted with Enter.</summary>
    Sent,

    /// <summary>Nothing was typed: the screen changed since it was read, or the prompt had a draft.</summary>
    NotTyped,

    /// <summary>The text was typed but the prompt changed before Enter, so Enter was not sent: the text may still be there.</summary>
    Unconfirmed,
}

public interface ILimitTerminal
{
    Task<LimitScreen> ReadAsync(Agent agent, CancellationToken ct);
    Task<ContinueDelivery> SendContinueAsync(Agent agent, LimitScreen expected, CancellationToken ct);
}

/// <summary>Reads only the visible pane, not scrollback. Checks it again on the Runner immediately before typing.</summary>
public sealed class LimitTerminal(RunnerShell shell) : ILimitTerminal
{
    internal const string Probe = """
        import datetime,hashlib,json,os,pathlib,re,subprocess,sys
        target=sys.argv[1] if len(sys.argv)>1 else ''
        def tmux(*args):
            return subprocess.check_output(['tmux',*args],text=True,stderr=subprocess.DEVNULL)
        def snapshot():
            pane,pid,dead,cy,cx=tmux('display-message','-p','-t',target,'#{pane_id}|#{pane_pid}|#{pane_dead}|#{cursor_y}|#{cursor_x}').strip().split('|')
            if not pane or not pid.isdecimal() or int(pid)<=0 or dead!='0': raise RuntimeError('Harness is not running')
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
            input_text=prompt[2] if prompt else None
            if prompt:
                extra=[]
                for continuation in raw[int(cy)+1:]:
                    if re.match(r'^\s*(?:[─━]{4,}|(?:GPT-|gpt-|o[1-9]|codex-).*|\? for shortcuts|.*bypass permissions)',continuation): break
                    extra.append(continuation.rstrip())
                if any(x.strip() for x in extra):
                    input_text=(input_text+'\n'+'\n'.join(extra)).rstrip()
                    empty=False
            # Pi uses an unprefixed editor between horizontal rules. Inspect the entire box,
            # not just its current blank line, so a multiline draft cannot be overwritten.
            if prompt is None and any(re.search(r'^think:|[0-9]+(?:\.[0-9]+)?%/[0-9]',x) for x in raw[-8:]):
                y=int(cy)
                borders=[i for i,x in enumerate(raw) if re.match(r'^─{4,}\s*$',x)]
                above=next((i for i in reversed(borders) if i<y),None)
                below=next((i for i in borders if i>y),None)
                if above is not None and below is not None:
                    input_text='\n'.join(raw[above+1:below]).strip()
                    empty=not input_text
            # System.Text.Json reads +00:00 as DateTimeKind.Local; Z preserves Utc for reset parsing.
            return dict(Screen=screen,Identity=identity,TimeZone=zone,InputEmpty=empty,InputText=input_text,Hash=hashlib.sha256(screen.encode()).hexdigest(),ObservedAt=datetime.datetime.now(datetime.timezone.utc).isoformat().replace('+00:00','Z'))
        """;

    public async Task<LimitScreen> ReadAsync(Agent agent, CancellationToken ct)
    {
        var result = (await shell.RunAsync(agent.Runner!, BuildReadScript(agent.TmuxSession), TimeSpan.FromSeconds(10), ct)).EnsureSuccess("Reading the usage limit");
        return JsonSerializer.Deserialize<LimitScreen>(result.Output.Trim()) ?? throw new InvalidOperationException("No terminal snapshot returned.");
    }

    public async Task<ContinueDelivery> SendContinueAsync(Agent agent, LimitScreen expected, CancellationToken ct)
    {
        var result = (await shell.RunAsync(agent.Runner!, BuildContinueScript(agent.TmuxSession, expected), TimeSpan.FromSeconds(10), ct)).EnsureSuccess("Sending continue");
        return result.Output.Trim() switch
        {
            "SENT" => ContinueDelivery.Sent,
            "CHANGED" => ContinueDelivery.NotTyped,
            _ => ContinueDelivery.Unconfirmed,
        };
    }

    internal static string BatchProbe => Probe + "\n" + """
        for session in sys.argv[1:]:
            try:
                target='='+session+':'
                state=snapshot()
                state['Identity']=state['Identity'].replace(':','|')
                print(json.dumps(dict(Session=session,**state)))
            except (OSError,ValueError,IndexError,RuntimeError,subprocess.CalledProcessError):
                print(json.dumps(dict(Session=session,Identity=None,Screen=None)))
        """;

    internal static string BuildReadScript(string session) => Script(session, "print(json.dumps(state))");

    internal static string BuildContinueScript(string session, LimitScreen expected) => BuildSubmitScript(session, expected, "continue");

    /// <summary>Prints SENT, CHANGED (nothing typed) or UNCONFIRMED (typed, but Enter was not sent).</summary>
    internal static string BuildSubmitScript(string session, LimitScreen expected, string command, bool requireUnchangedScreen = true, string? deliveryKey = null)
    {
        var expectedJson = JsonSerializer.Serialize(new { expected.Identity, expected.Hash });
        var code = $"""
            # Serialize guarded sends from quick commands and the continuation scheduler.
            import fcntl
            lockdir=pathlib.Path.home()/'.deveagentmanager'/'input-locks'
            lockdir.mkdir(parents=True,exist_ok=True)
            lock=(lockdir/hashlib.sha256(target.encode()).hexdigest()).open('a')
            try: fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
            except BlockingIOError:
                print('CHANGED')
                sys.exit(0)
            delivery_key={(deliveryKey is null ? "None" : JsonSerializer.Serialize(deliveryKey))}
            delivery_path=None
            def persist_delivery(value):
                if delivery_path is None: return
                temporary=delivery_path.with_suffix('.tmp')
                with temporary.open('w') as stream:
                    os.chmod(temporary,0o600)
                    stream.write(value)
                    stream.flush()
                    os.fsync(stream.fileno())
                temporary.replace(delivery_path)
                fd=os.open(delivery_path.parent,os.O_RDONLY)
                try: os.fsync(fd)
                finally: os.close(fd)
            if delivery_key is not None:
                deliveries=pathlib.Path.home()/'.deveagentmanager'/'job-deliveries'
                deliveries.mkdir(parents=True,exist_ok=True,mode=0o700)
                delivery_path=deliveries/hashlib.sha256(delivery_key.encode()).hexdigest()
                if delivery_path.exists():
                    print('SENT' if delivery_path.read_text()=='SENT' else 'UNCONFIRMED')
                    sys.exit(0)
            state=snapshot()
            command={JsonSerializer.Serialize(command)}
            expected=json.loads({JsonSerializer.Serialize(expectedJson)})
            if state['Identity']!=expected['Identity'] or ({(requireUnchangedScreen ? "True" : "False")} and state['Hash']!=expected['Hash']) or not state['InputEmpty']:
                print('CHANGED')
            else:
                pane=state['Identity'].split(':')[0]
                persist_delivery('SENDING')
                tmux('send-keys','-t',pane,'-l',command)
                # Codex's paste detection can absorb an Enter delivered in the same input burst.
                # Wait for the text to settle, then confirm nobody edited or replaced the prompt.
                import time
                time.sleep(0.35)
                ready=snapshot()
                if ready['Identity']!=expected['Identity'] or ready['InputText']!=command:
                    print('UNCONFIRMED')
                else:
                    tmux('send-keys','-t',pane,'Enter')
                    persist_delivery('SENT')
                    print('SENT')
            """;
        return Script(session, code);
    }

    private static string Script(string session, string suffix) =>
        $"python3 - {ShellQuote.Quote("=" + session + ":")} <<'DAM_LIMIT'\n{Probe}\nstate=snapshot()\n{suffix}\nDAM_LIMIT";
}
