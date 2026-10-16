"""Capture the actual PTY bytes of a tmux browser client on an isolated test server."""
import argparse
import fcntl
import os
import pathlib
import pty
import select
import shlex
import struct
import subprocess
import sys
import tempfile
import termios
import time

parser = argparse.ArgumentParser()
parser.add_argument('--cols', type=int, default=100)
parser.add_argument('--rows', type=int, default=30)
parser.add_argument('--attach-script', default='exec tmux -u -T RGB,hyperlinks attach-session -t =dam-linktest')
args = parser.parse_args()

with tempfile.TemporaryDirectory(prefix='dam-tmux-links-') as root:
    env = {**os.environ, 'TMUX_TMPDIR': root, 'TERM': 'xterm-256color'}
    env.pop('TMUX', None)
    for name in ('TERM_PROGRAM', 'TERM_PROGRAM_VERSION', 'VTE_VERSION', 'WT_SESSION', 'KITTY_WINDOW_ID'):
        env.pop(name, None)
    writer = pathlib.Path(root, 'writer.py')
    writer.write_text('''import os,tty
tty.setraw(0)
os.write(1, b'\\x1b[2J\\x1b[Hhttps://example.com/report#results\\r\\n'
    b'\\x1b]8;;https://example.org/named#details\\x07Named report\\x1b]8;;\\x07\\r\\n'
    b'\\x1b]8;;javascript:alert(1)\\x07Unsafe link\\x1b]8;;\\x07\\r\\n'
    b'ordinary terminal text\\r\\n\\x1b[?1000h\\x1b[?1002h\\x1b[?1006h')
while os.read(0, 4096): pass
''')
    master = slave = None
    client = None
    try:
        subprocess.run(['tmux', '-f', '/dev/null', 'new-session', '-d', '-s', 'dam-linktest',
            '-x', str(args.cols), '-y', str(args.rows), shlex.join([sys.executable, str(writer)])], env=env, check=True)
        subprocess.run(['tmux', 'set-option', '-g', 'status', 'off'], env=env, check=True)
        subprocess.run(['tmux', 'set-option', '-g', 'mouse', 'on'], env=env, check=True)
        subprocess.run(['tmux', 'set-option', '-s', 'terminal-features', 'xterm-256color:RGB'], env=env, check=True)
        # Wait until the pane has emitted the link, before attaching and testing its redraw.
        deadline = time.monotonic() + 3
        while True:
            pane = subprocess.check_output(['tmux', 'capture-pane', '-p', '-t', '=dam-linktest:'], env=env)
            if b'Named report' in pane:
                break
            if time.monotonic() >= deadline:
                raise TimeoutError('Test pane did not render its links')
            time.sleep(.02)
        master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', args.rows, args.cols, 0, 0))
        client = subprocess.Popen(['bash', '-c', args.attach_script], env=env, stdin=slave, stdout=slave, stderr=slave)
        os.close(slave)
        slave = None
        output = bytearray()
        deadline = time.monotonic() + 1
        while time.monotonic() < deadline:
            if select.select([master], [], [], .05)[0]:
                try:
                    chunk = os.read(master, 65536)
                except OSError:
                    break
                if not chunk:
                    break
                output.extend(chunk)
        if client.poll() is not None:
            raise RuntimeError('Test tmux attachment exited: ' + output.decode(errors='replace'))
        sys.stdout.buffer.write(output)
    finally:
        if client is not None and client.poll() is None:
            client.terminate()
            try:
                client.wait(timeout=3)
            except subprocess.TimeoutExpired:
                client.kill()
                client.wait(timeout=3)
        for fd in (master, slave):
            if fd is not None:
                os.close(fd)
        subprocess.run(['tmux', 'kill-server'], env=env, capture_output=True)
