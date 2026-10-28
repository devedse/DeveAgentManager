"""Own one Codex server and its terminal UI for the lifetime of a Manager agent's tmux pane."""
import ctypes
import fcntl
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time

os.umask(0o077)
agent, session, *options = sys.argv[1:]
state = Path.home() / '.deveagentmanager' / 'codex-voice' / agent
state.mkdir(parents=True, exist_ok=True)
owner = (state / 'owner.lock').open('w')
try:
    fcntl.flock(owner, fcntl.LOCK_EX | fcntl.LOCK_NB)
except BlockingIOError:
    sys.exit('The previous Codex process is still stopping. Start this agent again once it has stopped.')

# Unix socket paths have a 108-byte limit. Account runtime paths can be much longer.
temporary = Path(tempfile.mkdtemp(prefix='dam-codex-', dir='/tmp'))
socket = temporary / 'control.sock'
marker = state / 'connection.json'
parent = os.getpid()
children = []
stopping = False


def parent_death():
    # A killed tmux pane must not leave its server executing requests. Also cover the fork/prctl race.
    ctypes.CDLL(None, use_errno=True).prctl(1, signal.SIGTERM)
    if os.getppid() != parent:
        os._exit(1)


def stop(signum, frame):
    global stopping
    stopping = True
    for child in children:
        if child.poll() is None:
            child.terminate()


for sig in (signal.SIGTERM, signal.SIGHUP, signal.SIGINT):
    signal.signal(sig, stop)

try:
    with (state / 'server.log').open('w') as log:
        server = subprocess.Popen(['codex', 'app-server', '-c', 'approval_policy="never"', '-c',
                                   'sandbox_mode="danger-full-access"', *options, '--listen', 'unix://' + str(socket)],
                                  stdin=subprocess.DEVNULL, stdout=log, stderr=log, preexec_fn=parent_death)
        children.append(server)
        for attempt in range(200):
            if stopping or server.poll() is not None:
                sys.exit('Codex could not start its server. Check the agent\'s Codex server log on the Runner.')
            if socket.exists():
                break
            time.sleep(0.1)
        else:
            sys.exit('Codex did not open its control socket in time.')

        pending = marker.with_suffix('.pending')
        pending.write_text(json.dumps(dict(sessionId=session, socket=str(socket), supervisor=parent)))
        pending.replace(marker)
        # The server owns permissions and launch instructions. Remote resume rejects CLI permission overrides.
        terminal = subprocess.Popen(['codex', '--remote', 'unix://' + str(socket), 'resume', session], preexec_fn=parent_death)
        children.append(terminal)
        while not stopping and terminal.poll() is None and server.poll() is None:
            time.sleep(0.1)
        result = terminal.poll()
        sys.exit(result if result is not None else 1)
finally:
    stop(None, None)
    deadline = time.monotonic() + 6
    for child in children:
        try:
            child.wait(timeout=max(0.1, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            child.kill()
            child.wait()
    try:
        if json.loads(marker.read_text()).get('supervisor') == parent:
            marker.unlink()
    except (OSError, ValueError):
        pass
    shutil.rmtree(temporary)
