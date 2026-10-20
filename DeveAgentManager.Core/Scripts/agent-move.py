"""Private, verified move staging. Called only with Manager-generated operation data."""
import ctypes
import fcntl
import hashlib
import io
import json
import os
from pathlib import Path
import re
import select
import shutil
import signal
import stat
import subprocess
import sys
import tarfile
import time


class Progress:
    def __init__(self, enabled):
        self.enabled = enabled
        self.last = 0
        self.value = {}

    def start(self, stage, total=None):
        self.value = dict(Stage=stage, CurrentFile=None, BytesCompleted=0, TotalBytes=total)
        self.send(True)

    def file(self, path):
        self.value['CurrentFile'] = str(path)
        self.send()

    def add(self, count):
        self.value['BytesCompleted'] += count
        self.send()

    def send(self, force=False):
        if self.enabled and (force or time.monotonic() - self.last >= 0.25):
            print('DAM_MOVE_PROGRESS ' + json.dumps(self.value), flush=True)
            self.last = time.monotonic()


class ProgressReader:
    def __init__(self, source, progress):
        self.source, self.progress = source, progress

    def read(self, size=-1):
        block = self.source.read(size)
        self.progress.add(len(block))
        return block


def digest(path, progress=None):
    h = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''):
            h.update(block)
            if progress: progress.add(len(block))
    return h.hexdigest()


def record(path, relative, progress=None):
    if progress: progress.file(path)
    s = path.lstat()
    item = dict(name=relative, mode=stat.S_IMODE(s.st_mode))
    if stat.S_ISLNK(s.st_mode):
        item.update(kind='link', target=os.readlink(path))
    elif stat.S_ISDIR(s.st_mode):
        item.update(kind='directory')
    elif stat.S_ISREG(s.st_mode):
        item.update(kind='file', size=s.st_size, sha256=digest(path, progress))
    else:
        raise ValueError('Stop processes using special files in the workspace before moving it: ' + str(path))
    return item


def entries(root):
    yield root, ''
    if root.is_dir() and not root.is_symlink():
        for folder, dirs, files in os.walk(root, followlinks=False):
            for name in sorted(dirs + files):
                p = Path(folder) / name
                yield p, p.relative_to(root).as_posix()


def verify(root, items, progress=None):
    actual = {name: record(path, name, progress) for path, name in entries(root)}
    expected = {item['name']: item for item in items}
    if actual != expected:
        raise ValueError('File verification failed; files were changed, omitted or damaged: ' + str(root))


def manifest_size(manifest):
    return sum(item.get('size', 0) for root in manifest for item in root['items'])


def write_json(path, data):
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(data))
    temporary.chmod(0o600)
    os.replace(temporary, path)


def roots_for(c, home, workspace):
    roots = [(workspace, True)]
    runtime = home / '.deveagentmanager' / 'agent-runtime' / c['Owner'] / c['Agent']
    if runtime.exists():
        roots.append((runtime, True))
    for history in c.get('Histories') or []:
        path = Path(os.path.expanduser(history['Path']))
        if not path.exists():
            raise ValueError('A native session file is missing: ' + str(path))
        roots.append((path.parent if history['Harness'] == 'grok' else path, True))
        companion = path.with_suffix('')
        if history['Harness'] == 'claude' and companion.is_dir():
            roots.append((companion, True))
    # Pending publications belong to the agent even if their session is archived.
    for kind in ('shared-images', 'shared-files'):
        folder = home / '.deveagentmanager' / kind
        if folder.exists():
            for metadata in folder.glob('*/metadata.json'):
                data = json.loads(metadata.read_text())
                if data.get('agentId', '').replace('-', '') == c['Agent']:
                    roots.append((metadata.parent, False))
    # Preserve pasted uploads and fetched artifacts referred to by native records.
    # Scan bounded chunks: a single JSON record can contain a very large image.
    pattern = re.compile(rb'(?:~|/[^\s"<>\x00]*?)/\.deveagentmanager/(?:uploads|artifact-cache)/[A-Za-z0-9_./-]+')
    for root, _ in list(roots):
        for path, _ in entries(root):
            if not path.is_file() or path.is_symlink() or path.suffix not in ('.json', '.jsonl', '.txt', '.md', '.log'):
                continue
            with path.open('rb') as source:
                carry = b''
                for block in iter(lambda: source.read(1024 * 1024), b''):
                    data = carry + block
                    for match in pattern.finditer(data):
                        value = match.group().decode('utf-8').replace('~/', str(home) + '/', 1)
                        p = Path(value)
                        if p.is_relative_to(home / '.deveagentmanager') and p.is_file():
                            roots.append((p, False))
                    carry = data[-8192:]
    result = []
    for path, remove in sorted(set(roots), key=lambda x: len(str(x[0]))):
        if any(path == previous[0] or path.is_relative_to(previous[0]) for previous in result):
            continue
        if not path.is_absolute() or not (path == workspace or path.is_relative_to(home)):
            raise ValueError('A session path is outside this agent workspace and runner home.')
        result.append((path, remove))
    return result


def move_no_replace(source, target):
    libc = ctypes.CDLL(None, use_errno=True)
    result = libc.renameat2(-100, os.fsencode(source), -100, os.fsencode(target), 1)
    if result != 0:
        code = ctypes.get_errno()
        raise OSError(code, os.strerror(code), str(target))


def safe_parents(path):
    if any(parent.is_symlink() for parent in path.parents):
        raise ValueError('Move paths cannot pass through symlinked directories: ' + str(path))


def background_processes(c, before_stop=False):
    # Before stopping, exclude the live pane's tree. After stopping, every remaining
    # marked process can still use the workspace, including detached dev servers.
    pane_pid = None
    if before_stop and c.get('TmuxSession'):
        pane = subprocess.run(['tmux', 'display-message', '-p', '-t', '=' + c['TmuxSession'] + ':',
                               '#{pane_dead} #{pane_pid}'], capture_output=True, text=True)
        fields = pane.stdout.split()
        if pane.returncode == 0 and len(fields) == 2 and fields[0] == '0':
            pane_pid = int(fields[1])
    processes = {}
    for process in Path('/proc').iterdir():
        if not process.name.isdigit():
            continue
        try:
            fields = (process / 'stat').read_text().rsplit(') ', 1)[1].split()
            processes[int(process.name)] = (process, fields[0], int(fields[1]), int(fields[19]))
        except (OSError, ValueError, IndexError):
            continue
    marker = ('DAM_AGENT_ID=' + c['Agent']).encode()
    boot = Path('/proc/sys/kernel/random/boot_id').read_text().strip()
    blockers = []
    for pid, (process, state, parent, started) in processes.items():
        if pid == os.getpid() or state == 'Z':
            continue
        ancestor, seen = pid, set()
        while pane_pid is not None and ancestor in processes and ancestor not in seen and ancestor != pane_pid:
            seen.add(ancestor)
            ancestor = processes[ancestor][2]
        if pane_pid is not None and ancestor == pane_pid:
            continue
        try:
            if marker not in (process / 'environ').read_bytes().split(b'\0'):
                continue
            # Never include arguments: command lines may contain credentials.
            executable = Path(os.readlink(process / 'exe')).name
            args = (process / 'cmdline').read_bytes().split(b'\0')
            script = Path(os.fsdecode(args[1])).name if len(args) > 1 else ''
            label = script + ' (' + executable + ')' if script.endswith(('.js', '.mjs', '.cjs', '.py')) else executable
            try:
                directory = os.readlink(process / 'cwd')
            except OSError:
                directory = None
            blockers.append(dict(identity=dict(pid=pid, startTicks=started, bootId=boot), name=label, workingDirectory=directory))
        except (OSError, ValueError):
            continue
    return sorted(blockers, key=lambda item: item['identity']['pid'])


def check_background(c, before_stop=False):
    blockers = background_processes(c, before_stop)
    if blockers:
        detail = '; '.join(f"{p['name']}, PID {p['identity']['pid']}" for p in blockers[:8])
        if len(blockers) > 8:
            detail += f'; and {len(blockers) - 8} more'
        raise ValueError('Agent background processes are still running: ' + detail + '. Stop them, then retry the move.')


def stop_background(c):
    approved = {(p['Pid'], p['StartTicks'], p['BootId']) for p in c.get('StopProcesses') or []}
    current = background_processes(c, before_stop=True)
    key = lambda p: (p['pid'], p['startTicks'], p['bootId'])
    if any(key(p['identity']) not in approved for p in current):
        raise ValueError('Background processes changed. Review the updated list before moving.')
    if not current:
        return
    if not hasattr(os, 'pidfd_open') or not hasattr(signal, 'pidfd_send_signal'):
        raise ValueError('This runner cannot safely stop processes by identity. Stop them manually before moving.')
    handles = []
    try:
        # Opening first pins the process; subsequent identity checks and signals
        # cannot accidentally target a new process that reused its PID.
        for item in current:
            identity = item['identity']
            try:
                fd = os.pidfd_open(identity['pid'])
            except ProcessLookupError:
                continue
            handles.append(fd)
            process = Path('/proc') / str(identity['pid'])
            try:
                fields = (process / 'stat').read_text().rsplit(') ', 1)[1].split()
                owned = ('DAM_AGENT_ID=' + c['Agent']).encode() in (process / 'environ').read_bytes().split(b'\0')
                if int(fields[19]) != identity['startTicks'] or not owned or process.stat().st_uid != os.geteuid():
                    raise ValueError('A process identity or owner changed. Refresh the list before moving.')
            except FileNotFoundError:
                os.close(handles.pop())
                continue
        poll = select.poll()
        pending = set(handles)
        for fd in handles:
            poll.register(fd, select.POLLIN)
            try:
                signal.pidfd_send_signal(fd, signal.SIGTERM)
            except ProcessLookupError:
                pass

        def wait_for_exit(seconds):
            deadline = time.monotonic() + seconds
            while pending and time.monotonic() < deadline:
                for fd, _ in poll.poll(max(1, int((deadline - time.monotonic()) * 1000))):
                    pending.discard(fd)
                    poll.unregister(fd)

        wait_for_exit(10)
        for fd in pending:
            try:
                signal.pidfd_send_signal(fd, signal.SIGKILL)
            except ProcessLookupError:
                pass
        wait_for_exit(5)
        if pending:
            raise ValueError('Some approved processes did not stop. The source has been retained.')
        check_background(c, before_stop=True)
    finally:
        for fd in handles:
            os.close(fd)


def main(phase, c):
    progress = Progress(c.get('ReportProgress', False))
    os.umask(0o077)
    home = Path(c.get('Home') or Path.home())
    workspace = Path(c['Workspace'])
    operation = home / '.deveagentmanager' / 'moves' / c['Id']
    if not re.fullmatch('[a-f0-9]{32}', c['Id']):
        raise ValueError('Invalid move identity.')
    if phase == 'preflight':
        safe_parents(workspace)
        if not workspace.is_absolute() or workspace.is_symlink() or workspace in (Path('/'), home, home.parent):
            raise ValueError('Move requires an independent, absolute workspace directory.')
        if workspace.is_relative_to(home / '.deveagentmanager'):
            raise ValueError('The workspace cannot be a Manager runtime or credential directory.')
        if home.is_relative_to(workspace) or any(workspace == Path(p) or workspace.is_relative_to(Path(p)) for p in ('/etc', '/usr', '/var', '/proc', '/sys', '/dev', '/run', '/boot', '/bin', '/sbin', '/lib')):
            raise ValueError('System and shared home directories cannot be moved.')
        if c.get('Source') and not workspace.is_dir():
            raise ValueError('The source workspace does not exist.')
        if not c.get('Source') and workspace.exists():
            raise ValueError('The destination workspace already exists and will not be overwritten.')
        others = c.get('OtherSourceFolders') if c.get('Source') else c.get('OtherTargetFolders')
        for other in others or []:
            other = Path(other).resolve()
            if workspace.resolve().is_relative_to(other) or other.is_relative_to(workspace.resolve()):
                raise ValueError('Another agent shares the resolved workspace or a parent directory.')
        if workspace.exists() and (workspace / '.git').is_file():
            raise ValueError('This workspace is a linked Git worktree. Move an independent checkout instead.')
        print(json.dumps(dict(home=str(home), workspace=str(workspace.resolve()))))
        return
    if phase in ('quiesce', 'check-background'):
        check_background(c, before_stop=phase == 'check-background')
        return
    if phase == 'list-background':
        print(json.dumps(background_processes(c, before_stop=True)))
        return
    operation.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    # A lost SSH connection must not let cleanup race an already submitted phase.
    lock = (operation.parent / (c['Id'] + '.lock')).open('a')
    fcntl.flock(lock, fcntl.LOCK_EX)
    operation.mkdir(exist_ok=True, mode=0o700)
    archive = operation / 'bundle.tar'
    manifest_path = operation / 'manifest.json'
    ledger_path = operation / 'installed.json'
    if phase == 'stop-background':
        stop_background(c)
        print('Approved background processes stopped.')
        return
    if phase == 'discard':
        shutil.rmtree(operation)
        return
    if phase == 'pack':
        progress.start('Finding session files and attachments')
        roots = roots_for(c, home, workspace)
        # Metadata only: no extra reads of history contents to calculate progress.
        total = sum(path.lstat().st_size for root, _ in roots for path, _ in entries(root)
                    if stat.S_ISREG(path.lstat().st_mode))
        progress.start('Checking source files', total)
        manifest = []
        for root, remove in roots:
            safe_parents(root)
            manifest.append(dict(path=str(root), remove=remove,
                                 items=[record(path, relative, progress) for path, relative in entries(root)]))
        progress.start('Packing files', manifest_size(manifest))
        # A small streaming index maps archive offsets to original paths during SFTP transfer.
        # It is for display only; the archive checksum and per-file manifest remain authoritative.
        with (operation / 'transfer-index.jsonl').open('w') as transfer_index, tarfile.open(archive.with_suffix('.tmp'), 'w') as bundle:
            for index, root in enumerate(manifest):
                for item in root['items']:
                    relative = item['name']
                    path = Path(root['path']) / relative if relative else Path(root['path'])
                    progress.file(path)
                    info = bundle.gettarinfo(str(path), f'{index}/{relative}'.rstrip('/'))
                    if info.islnk():
                        info.type = tarfile.REGTYPE
                        info.linkname = ''
                        info.size = path.stat().st_size
                    with path.open('rb') if info.isfile() else io.BytesIO() as source:
                        bundle.addfile(info, ProgressReader(source, progress) if info.isfile() else None)
                    transfer_index.write(json.dumps([bundle.offset, str(path)]) + '\n')
            progress.send(True)
            progress.start('Verifying packed source files', manifest_size(manifest))
            for root in manifest:
                verify(Path(root['path']), root['items'], progress)
            data = json.dumps(manifest).encode()
            info = tarfile.TarInfo('manifest.json'); info.size = len(data); info.mode = 0o600
            bundle.addfile(info, io.BytesIO(data))
            transfer_index.write(json.dumps([bundle.offset, 'Move file manifest']) + '\n')
        (operation / 'transfer-index.jsonl').chmod(0o600)
        os.replace(archive.with_suffix('.tmp'), archive)
        archive.chmod(0o600)
        write_json(manifest_path, manifest)
        progress.start('Checking packed archive', archive.stat().st_size)
        progress.file('bundle.tar')
        checksum = digest(archive, progress)
        progress.send(True)
        print(checksum)
    elif phase == 'unpack':
        progress.start('Checking received archive', archive.stat().st_size)
        progress.file('bundle.tar')
        if digest(archive, progress) != c['ArchiveSha256']:
            raise ValueError('Transfer checksum mismatch. The source has been retained.')
        with tarfile.open(archive) as bundle:
            manifest = json.load(bundle.extractfile('manifest.json'))
            write_json(manifest_path, manifest)
            progress.start('Restoring files on destination', manifest_size(manifest))
            for index, root in enumerate(manifest):
                destination = Path(root['path'])
                safe_parents(destination)
                if not destination.is_absolute() or not (destination == workspace or destination.is_relative_to(home)):
                    raise ValueError('Invalid destination in move archive.')
                if destination.exists() or destination.is_symlink():
                    if root['remove']:
                        raise ValueError('Destination already exists; it will not be overwritten: ' + str(destination))
                    verify(destination, root['items'], progress)
                    continue
                stage = destination.with_name(destination.name + '.dam-moving-' + c['Id'])
                if stage.exists() or stage.is_symlink():
                    raise ValueError('Move staging already exists; recover the earlier move first.')
                destination.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
                for item in root['items']:
                    relative = Path(item['name'])
                    if relative.is_absolute() or '..' in relative.parts:
                        raise ValueError('Invalid archive path.')
                    path = stage / relative if item['name'] else stage
                    progress.file(destination / relative if item['name'] else destination)
                    if any(p.is_symlink() for p in path.parents if p != destination.parent and p.is_relative_to(destination.parent)):
                        raise ValueError('Archive cannot write through a symlink.')
                    if item['kind'] == 'directory':
                        path.mkdir(exist_ok=True, mode=0o700)
                    elif item['kind'] == 'link':
                        path.symlink_to(item['target'])
                    else:
                        archive_name = f'{index}/{item["name"]}'.rstrip('/')
                        if not bundle.getmember(archive_name).isfile():
                            raise ValueError('Regular file expected in archive.')
                        member = bundle.extractfile(archive_name)
                        with path.open('xb') as output:
                            shutil.copyfileobj(ProgressReader(member, progress), output, 1024 * 1024)
                        path.chmod(item['mode'])
                for item in reversed(root['items']):
                    if item['kind'] == 'directory':
                        (stage / item['name'] if item['name'] else stage).chmod(item['mode'])
            progress.send(True)
            progress.start('Verifying restored files', manifest_size(manifest))
            for root in manifest:
                destination = Path(root['path'])
                stage = destination.with_name(destination.name + '.dam-moving-' + c['Id'])
                verify(stage if stage.exists() or stage.is_symlink() else destination, root['items'], progress)
            progress.send(True)
        print('verified')
    elif phase == 'install':
        manifest = json.loads(manifest_path.read_text())
        progress.start('Installing and verifying destination files', manifest_size(manifest))
        ledger = []
        for root in manifest:
            destination = Path(root['path'])
            safe_parents(destination)
            stage = destination.with_name(destination.name + '.dam-moving-' + c['Id'])
            if not stage.exists() and not stage.is_symlink():
                if root['remove']:
                    raise ValueError('Missing move staging directory.')
                verify(destination, root['items'], progress); continue
            ledger.append(dict(path=str(destination), stage=str(stage)))
            write_json(ledger_path, ledger)
            move_no_replace(stage, destination)
            verify(destination, root['items'], progress)
        progress.send(True)
        print('installed')
    elif phase == 'verify-source':
        manifest = [root for root in json.loads(manifest_path.read_text()) if root['remove']]
        progress.start('Checking source before removal', manifest_size(manifest))
        for root in manifest:
            if root['remove']:
                verify(Path(root['path']), root['items'], progress)
        progress.send(True)
        print('unchanged')
    elif phase in ('cleanup-target', 'cleanup-source'):
        if not manifest_path.exists():
            if phase == 'cleanup-target':
                shutil.rmtree(operation)
            return
        manifest = json.loads(manifest_path.read_text())
        progress.start('Removing source files' if phase == 'cleanup-source' else 'Cleaning destination after failed move')
        ledger = json.loads(ledger_path.read_text()) if ledger_path.exists() else []
        for root in reversed(manifest):
            path = Path(root['path'])
            stage = path.with_name(path.name + '.dam-moving-' + c['Id'])
            owned = any(item['path'] == str(path) and not Path(item['stage']).exists() and not Path(item['stage']).is_symlink() for item in ledger)
            remove = root['remove'] if phase == 'cleanup-source' else owned
            if remove and (path.exists() or path.is_symlink()):
                verify(path, root['items'], progress)
                if path.is_dir() and not path.is_symlink(): shutil.rmtree(path)
                else: path.unlink()
            if phase == 'cleanup-target' and (stage.exists() or stage.is_symlink()):
                if stage.is_dir() and not stage.is_symlink(): shutil.rmtree(stage)
                else: stage.unlink()
        shutil.rmtree(operation)
        progress.send(True)
        print('cleaned')
    else:
        raise ValueError('Unknown move phase.')


if __name__ == '__main__':
    try:
        main(sys.argv[1], json.loads(sys.argv[2]))
    except ValueError as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
