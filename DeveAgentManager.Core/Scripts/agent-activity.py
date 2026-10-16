# Read-only lifecycle evidence for the exact Codex process/session in a pane. The full conversation
# and process environment never leave the Runner. Bounded reads keep polling large sessions cheap.
def read_codex_turn(path, session_id):
    with path.open('rb') as stream:
        header=json.loads(stream.readline(65536))
        if header.get('type')!='session_meta' or header.get('payload',{}).get('id')!=session_id: return None
        stream.seek(0,2)
        offset=max(0,stream.tell()-262144)
        stream.seek(offset)
        tail=stream.read(262144)
    # A writer may still be appending its final line.
    lines=tail.split(b'\n')[:-1]
    if offset: lines=lines[1:] # Discard the first partial record, within the same read budget.
    for raw in reversed(lines):
        try:
            line=json.loads(raw)
            payload=line.get('payload',{})
            if line.get('type')!='event_msg' or not isinstance(payload,dict): continue
            state={'task_started':'Started','task_complete':'Completed','turn_aborted':'Aborted'}.get(payload.get('type'))
            if state is None: continue
            turn=payload.get('turn_id')
            if not isinstance(turn,str) or not turn or len(turn)>128: return None
            at=datetime.datetime.fromisoformat(line['timestamp'].replace('Z','+00:00'))
            if at.tzinfo is None or at>datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(seconds=5): return None
            return dict(SessionId=session_id,TurnId=turn,State=state,At=at.astimezone(datetime.timezone.utc).isoformat().replace('+00:00','Z'))
        except (ValueError,KeyError,TypeError): continue
    return None

def native_activity(config, identity):
    if not config or config.get('Harness')!='codex': return None
    session_id=config.get('SessionId','')
    if not isinstance(session_id,str) or not re.fullmatch(r'[0-9a-fA-F-]{36}',session_id): return None
    pid=identity.split('|')[1]
    if (pathlib.Path('/proc')/pid/'stat').read_text().rsplit(')',1)[1].split()[19]!=identity.split('|')[2]: return None
    pending=[pid]
    visited=set()
    while pending and len(visited)<32:
        current=pending.pop()
        if current in visited: continue
        visited.add(current)
        process=pathlib.Path('/proc')/current
        try:
            args=[x.decode() for x in process.joinpath('cmdline').read_bytes().split(b'\0') if x]
            if 'resume' in args and session_id in args and any(pathlib.Path(x).name in ('codex','codex.exe','codex.js') for x in args):
                env=dict(x.split(b'=',1) for x in process.joinpath('environ').read_bytes().split(b'\0') if b'=' in x)
                home=pathlib.Path(env.get(b'CODEX_HOME',env.get(b'HOME',b'')+b'/.codex').decode())
                candidates=list(home.glob('sessions/*/*/*/rollout-*-'+session_id+'.jsonl'))
                if len(candidates)!=1: return None
                turn=read_codex_turn(candidates[0],session_id)
                if turn and turn['State']=='Started':
                    # Resume can leave an unfinished start from a previous CLI process in the file.
                    # Only starts belonging to this process can establish current work.
                    ticks=int(process.joinpath('stat').read_text().rsplit(')',1)[1].split()[19])
                    uptime=float(pathlib.Path('/proc/uptime').read_text().split()[0])
                    started=datetime.datetime.now(datetime.timezone.utc)-datetime.timedelta(seconds=uptime-ticks/os.sysconf('SC_CLK_TCK'))
                    if datetime.datetime.fromisoformat(turn['At'].replace('Z','+00:00'))<started-datetime.timedelta(seconds=1): return None
                return turn
            pending.extend(process.joinpath('task',current,'children').read_text().split())
        except (OSError,ValueError,UnicodeError,IndexError): continue
    return None
