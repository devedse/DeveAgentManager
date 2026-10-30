"""Workspace clones next to a project's Main checkout. Called only with Manager-generated data.

A workspace is an independent clone in <main>.workspaces/<name>. Git hardlinks the history; working files are
written by checkout, or copied with copy-on-write where the filesystem supports it. The ownership marker lives in
the clone's .git folder, so it moves with the folder and never shows in `git status`.

The folder, measure and delete phases are for deleting an agent's own folder together with the agent.
"""
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile

MARKER = 'dam-workspace'


class Refused(Exception):
    """A reason the person can act on. Printed without a traceback."""


def git(repo, *args, check=True, text=True):
    result = subprocess.run(['git', '-C', str(repo), *args], capture_output=True, text=text)
    if check and result.returncode != 0:
        error = result.stderr if text else result.stderr.decode(errors='replace')
        raise Refused(error.strip() or 'git ' + args[0] + ' failed.')
    return result


def out(repo, *args):
    return git(repo, *args).stdout.strip()


def ok(repo, *args):
    return git(repo, *args, check=False).returncode == 0


def has_ref(repo, ref):
    return ok(repo, 'show-ref', '--verify', '--quiet', ref)


def current_branch(repo):
    return git(repo, 'symbolic-ref', '--quiet', '--short', 'HEAD', check=False).stdout.strip() or None


def require_repository(path, what):
    path = Path(path)
    if path.is_symlink() or not path.is_dir():
        raise Refused(what + ' is missing.')
    if any(parent.is_symlink() for parent in path.parents):
        raise Refused(what + ' cannot be inside a symbolic link.')
    dot = path / '.git'
    if dot.is_symlink() or not dot.is_dir():
        raise Refused(what + ' must be an ordinary Git checkout. Linked worktrees and external Git folders are not supported.')
    if Path(out(path, 'rev-parse', '--show-toplevel')).resolve() != path.resolve():
        raise Refused(what + ' must be the repository root.')
    if (path / '.gitmodules').exists():
        raise Refused('Repositories with submodules are not supported for workspaces yet.')
    if git(path, 'config', '--bool', 'core.sparseCheckout', check=False).stdout.strip() == 'true':
        raise Refused('Sparse checkouts are not supported for workspaces yet.')
    if (dot / 'shallow').exists():
        raise Refused('Fetch the complete history before creating workspaces.')


def default_branch(main):
    """The repository's main branch: origin's default, else main/master, else what Main has checked out."""
    head = git(main, 'symbolic-ref', '--quiet', '--short', 'refs/remotes/origin/HEAD', check=False).stdout.strip()
    if head.startswith('origin/'):
        return head[len('origin/'):]
    for name in ('main', 'master'):
        if has_ref(main, 'refs/heads/' + name) or has_ref(main, 'refs/remotes/origin/' + name):
            return name
    return current_branch(main)


def upstream(repo, branch):
    """(remote, 'remote/branch') this branch follows, or (None, None)."""
    if not branch:
        return None, None
    tracked = git(repo, 'for-each-ref', '--format=%(upstream:short)', 'refs/heads/' + branch, check=False).stdout.strip()
    remote = git(repo, 'config', '--get', 'branch.' + branch + '.remote', check=False).stdout.strip()
    if tracked and remote and tracked.startswith(remote + '/'):
        return remote, tracked
    if has_ref(repo, 'refs/remotes/origin/' + branch):
        return 'origin', 'origin/' + branch
    return None, None


def plan(c):
    """Where a new workspace starts: the repository to clone, its branch and exact commit."""
    main, source = Path(c['Main']), Path(c['Source'])
    require_repository(main, 'Main')
    if c['Start'] == 'MainBranch':
        repo = main
        branch = default_branch(main)
        remote, tracked = upstream(main, branch)
        if branch and has_ref(main, 'refs/heads/' + branch):
            ref = 'refs/heads/' + branch
        elif tracked:
            ref = 'refs/remotes/' + tracked
        else:
            ref, branch = 'HEAD', current_branch(main)
    else:
        require_repository(source, 'This workspace')
        repo = source
        branch = current_branch(source)
        remote, tracked = upstream(source, branch)
        ref = 'HEAD'
    commit = git(repo, 'rev-parse', '--verify', '--quiet', ref + '^{commit}', check=False).stdout.strip()
    if not commit:
        raise Refused('The repository has no commits yet. Commit something before creating a workspace.')
    if ok(repo, 'grep', '-q', 'filter=lfs', commit, '--', '.gitattributes', '**/.gitattributes'):
        raise Refused('Repositories that use Git LFS are not supported for workspaces yet.')
    return dict(Repo=str(repo), Branch=branch, Remote=remote, Upstream=tracked, Commit=commit)


def reflink(folder):
    """Whether this filesystem makes instant copy-on-write copies."""
    try:
        with tempfile.TemporaryDirectory(prefix='.dam-reflink-', dir=folder) as temp:
            first = Path(temp) / 'a'
            first.write_bytes(b'x' * 4096)
            return subprocess.run(['cp', '--reflink=always', str(first), str(Path(temp) / 'b')], capture_output=True).returncode == 0
    except OSError:
        return False


def writable_folder(container):
    folder = container if container.is_dir() else container.parent
    if container.is_symlink() or not folder.is_dir():
        raise Refused('The folder next to Main is missing or a symbolic link: ' + str(folder))
    if not os.access(folder, os.W_OK | os.X_OK):
        raise Refused('Workspaces are created next to Main, in ' + str(folder) + ', which this Runner user cannot write to.')
    return folder


def inspect(c):
    """How much a start point copies: tracked files of a commit, the current code, or everything."""
    p = plan(c)
    repo, files, size = Path(p['Repo']), 0, 0
    if c['Start'] in ('MainBranch', 'LastCommit'):
        for entry in git(repo, 'ls-tree', '-r', '-l', '-z', p['Commit'], text=False).stdout.split(b'\0'):
            if not entry:
                continue
            fields = entry.split(b'\t', 1)[0].split()
            if fields[1] == b'blob':
                files += 1
                size += int(fields[3])
    elif c['Start'] == 'CurrentCode':
        names = git(repo, 'ls-files', '-z', '--cached', '--others', '--exclude-standard', text=False).stdout.split(b'\0')
        for name in dict.fromkeys(names):
            try:
                info = os.lstat(os.path.join(os.fsencode(repo), name)) if name else None
            except FileNotFoundError:
                continue
            if info and (stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode)):
                files += 1
                size += info.st_size
    else:
        for root, dirs, names in os.walk(repo):
            if root == str(repo) and '.git' in dirs:
                dirs.remove('.git')
            for name in names:
                try:
                    info = os.lstat(os.path.join(root, name))
                except FileNotFoundError:
                    continue
                files += 1
                size += info.st_size
    folder = writable_folder(Path(c['Container']))
    print(json.dumps(dict(Bytes=size, Files=files, FreeBytes=shutil.disk_usage(folder).free, CopyOnWrite=reflink(folder),
                          Branch=p['Branch'], CanPull=p['Upstream'] is not None)))


def preflight(c):
    """Validates the start point and picks the first free folder name. Changes nothing."""
    p = plan(c)
    container = Path(c['Container'])
    writable_folder(container)
    if c.get('Pull') and not p['Upstream']:
        raise Refused('The main branch has no remote to pull from. Turn off Pull first.')
    if c.get('NewBranch'):
        branch = c['NewBranch']
        if branch.startswith('-') or not ok('.', 'check-ref-format', '--branch', branch):
            raise Refused('Choose a valid branch name.')
    for name in c['Names']:
        path = container / name
        if not path.exists() and not path.is_symlink():
            print(json.dumps(dict(p, Name=name, Path=str(path))))
            return
    raise Refused('Every suggested folder name is taken. Choose another name.')


def remote_config(repo):
    """The source's remotes, so a clone pulls and pushes where the source does."""
    data = git(repo, 'config', '-z', '--get-regexp', r'^remote\.', check=False).stdout
    return [tuple(item.split('\n', 1)) for item in data.split('\0') if '\n' in item]


def copy_changes(repo, target):
    """Copies changed and new files (what `git status` lists, without ignored files) and applies deletions."""
    # Against HEAD, so staged changes count too.
    changed = git(repo, 'diff', '-z', '--name-only', '--no-renames', 'HEAD', text=False).stdout.split(b'\0')
    changed += git(repo, 'ls-files', '-z', '--others', '--exclude-standard', text=False).stdout.split(b'\0')
    copy = []
    for name in dict.fromkeys(changed):
        if not name:
            continue
        if os.path.lexists(os.path.join(os.fsencode(repo), name)):
            copy.append(name)
            continue
        gone = os.path.join(os.fsencode(target), name)
        if os.path.isdir(gone) and not os.path.islink(gone):
            shutil.rmtree(gone)
        elif os.path.lexists(gone):
            os.unlink(gone)
    for start in range(0, len(copy), 500):
        subprocess.run(['cp', '-a', '--reflink=auto', '--parents', '-t', str(target), '--', *copy[start:start + 500]], cwd=repo, check=True)


def create(c):
    target = Path(c['Path'])
    container = target.parent
    temporary = container / ('.' + target.name + '.' + c['Token'] + '.creating')
    if target.exists() or target.is_symlink():
        raise Refused('The workspace folder already exists: ' + str(target))
    container.mkdir(mode=0o755, exist_ok=True)
    if container.is_symlink():
        raise Refused('The workspaces folder cannot be a symbolic link.')
    if temporary.exists():
        # An interrupted attempt of this same creation; it was never handed to an agent.
        shutil.rmtree(temporary)
    repo = Path(c['Repo'])
    remotes = remote_config(repo)
    try:
        clone = ['git', 'clone', '--quiet', '--local', '--no-checkout']
        if remotes:
            clone += ['--origin', 'dam-source']
        result = subprocess.run(clone + ['--', str(repo), str(temporary)], capture_output=True, text=True)
        if result.returncode != 0:
            raise Refused(result.stderr.strip() or 'Cloning failed.')
        (temporary / '.git' / MARKER).write_text(json.dumps(dict(Token=c['Token'])))
        if remotes:
            # Same remotes and the source's last-known remote branches, without network access.
            git(temporary, 'fetch', '--quiet', '--no-tags', 'dam-source', '+refs/remotes/*:refs/remotes/*')
            git(temporary, 'remote', 'remove', 'dam-source')
            for key, value in remotes:
                git(temporary, 'config', '--add', key, value)
        commit, branch = c['Commit'], c.get('NewBranch') or c.get('Branch')
        if c.get('Pull'):
            # Uses the Runner user's own Git credentials.
            git(temporary, 'fetch', '--quiet', c['Remote'])
            commit = out(temporary, 'rev-parse', '--verify', 'refs/remotes/' + c['Upstream'] + '^{commit}')
        if c['Start'] == 'ExactCopy':
            if branch:
                git(temporary, 'update-ref', 'refs/heads/' + branch, commit)
                git(temporary, 'symbolic-ref', 'HEAD', 'refs/heads/' + branch)
            else:
                git(temporary, 'update-ref', '--no-deref', 'HEAD', commit)
            entries = [str(repo / name) for name in os.listdir(repo) if name != '.git']
            if entries:
                subprocess.run(['cp', '-a', '--reflink=auto', '-t', str(temporary), '--', *entries], check=True)
            # The index follows HEAD; the copied files stay exactly as they were.
            git(temporary, 'reset', '--quiet')
        else:
            if branch:
                git(temporary, 'checkout', '--quiet', '-f', '-B', branch, commit)
            else:
                git(temporary, 'checkout', '--quiet', '-f', '--detach', commit)
            if c['Start'] == 'CurrentCode':
                copy_changes(repo, temporary)
        if branch and not c.get('NewBranch') and c.get('Upstream') and has_ref(temporary, 'refs/remotes/' + c['Upstream']):
            git(temporary, 'branch', '--quiet', '--set-upstream-to=' + c['Upstream'], branch)
        # Only this workspace's branch: other local branches of the source would look like unpushed work here.
        for other in out(temporary, 'for-each-ref', '--format=%(refname:short)', 'refs/heads').splitlines():
            if other and other != branch:
                git(temporary, 'branch', '--quiet', '-D', other)
        os.rename(temporary, target)
    except BaseException:
        if temporary.exists():
            shutil.rmtree(temporary, ignore_errors=True)
        raise
    print(json.dumps(dict(Branch=current_branch(target), Commit=commit)))


def owned(path, token):
    marker = Path(path) / '.git' / MARKER
    try:
        return not marker.is_symlink() and json.loads(marker.read_text()).get('Token') == token
    except (OSError, ValueError):
        return False


def verify(c):
    path = Path(c['Path'])
    if path.is_symlink() or not path.is_dir():
        raise Refused('The workspace folder is missing: ' + str(path) + '. Remove this workspace and create a new one.')
    if not owned(path, c['Token']):
        raise Refused('This folder is not the workspace the Manager created. It was left untouched.')
    if Path(out(path, 'rev-parse', '--show-toplevel')).resolve() != path.resolve():
        raise Refused('The workspace folder is no longer the repository root.')
    print(json.dumps(dict(Branch=current_branch(path) or out(path, 'rev-parse', '--short', 'HEAD'))))


def unsaved(path):
    """Uncommitted files (the first three as a sample) and commits that are on no remote."""
    entries, skip = [], False
    for item in git(path, 'status', '--porcelain=v1', '-z', '--untracked-files=all', text=False).stdout.split(b'\0'):
        if skip or not item:
            skip = False
            continue
        if item[:1] in (b'R', b'C'):
            skip = True
        entries.append(os.fsdecode(item[3:]))
    has_commits = ok(path, 'rev-parse', '--verify', '--quiet', 'HEAD')
    unpushed = int(out(path, 'rev-list', '--count', 'HEAD', '--branches', '--not', '--remotes')) if has_commits else 0
    return dict(Uncommitted=len(entries), Sample=entries[:3], Unpushed=unpushed)


def changes(c):
    """What removing or updating would affect: uncommitted files and commits that are on no remote."""
    path = Path(c['Path'])
    if path.is_symlink() or not (path / '.git').is_dir():
        print(json.dumps(dict(Exists=False, Uncommitted=0, Sample=[], Unpushed=0, Upstream=None, Branch=None, Owned=False)))
        return
    work = unsaved(path)
    tracked = git(path, 'rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{upstream}', check=False).stdout.strip() or None
    print(json.dumps(dict(Exists=True, **work, Upstream=tracked, Branch=current_branch(path), Owned=owned(path, c.get('Token')))))


# An agent's own folder (Main, or an agent without a project) can be deleted with the agent. Whatever the folder, these
# are never deleted: the home folder or a folder containing it, top-level and system folders, the Manager's own files,
# mount points, symbolic links and folders of another user.
SYSTEM = ('/bin', '/boot', '/dev', '/etc', '/lib', '/lib32', '/lib64', '/libx32', '/proc', '/run', '/sbin', '/sys', '/usr')


def within(path, folder):
    return path == folder or folder in path.parents


def refusal(path):
    """Why this folder must not be deleted, or None. Checked when asked and again right before deleting."""
    if not path.is_absolute():
        return 'Its path is not absolute.'
    if path.is_symlink():
        return 'It is a symbolic link.'
    if not path.is_dir():
        return 'It is not a folder.'
    real, home = path.resolve(), Path.home().resolve()
    if len(real.parts) < 3:
        return 'It is a top-level folder.'
    if within(home, real):
        return 'It is the home folder, or contains it.'
    if any(within(real, home / name) for name in ('.deveagentmanager', '.ssh')) or any(within(real, Path(p)) for p in SYSTEM):
        return 'It belongs to the system or the Manager.'
    if os.path.ismount(real):
        return 'It is a mount point.'
    if real.stat().st_uid != os.getuid():
        return 'It belongs to another user on the Runner.'
    if not os.access(real.parent, os.W_OK | os.X_OK):
        return 'The Runner user cannot delete folders in ' + str(real.parent) + '.'
    return None


def folder(c):
    """What deleting an agent's folder would affect: whether it may, and its unsaved Git work."""
    path = Path(c['Path'])
    result = dict(Exists=path.exists() or path.is_symlink(), Refusal=None, Git=False, Uncommitted=0, Sample=[], Unpushed=0, GitProblem=None)
    if result['Exists'] or not path.is_absolute():
        result['Refusal'] = refusal(path)
    if result['Exists'] and path.is_absolute():
        if not path.is_symlink() and (path / '.git').exists():
            result['Git'] = True
            try:
                result.update(unsaved(path))
            except Refused as error:
                result['GitProblem'] = str(error)
    print(json.dumps(result))


def measure(c):
    """The folder's disk space, each file once, and what deleting it frees: a file also linked from outside stays."""
    path = Path(c['Path'])
    used, freed, files, linked = 0, 0, 0, {}
    if path.is_dir() and not path.is_symlink():
        folders = [str(path)]
        while folders:
            try:
                entries = os.scandir(folders.pop())
            except OSError:
                continue
            with entries:
                for entry in entries:
                    try:
                        info = entry.stat(follow_symlinks=False)
                    except OSError:
                        continue
                    size = info.st_blocks * 512
                    if entry.is_dir(follow_symlinks=False):
                        folders.append(entry.path)
                    else:
                        files += 1
                    if info.st_nlink > 1 and not stat.S_ISDIR(info.st_mode):
                        seen = linked.setdefault((info.st_dev, info.st_ino), [info.st_nlink, size, 0])
                        seen[2] += 1
                    else:
                        used += size
                        freed += size
        for links, size, seen in linked.values():
            used += size
            if seen >= links:
                freed += size
    print(json.dumps(dict(Bytes=used, FreedBytes=freed, Files=files)))


def delete(c):
    """Deletes an agent's folder, after checking again that it may."""
    path = Path(c['Path'])
    if not path.exists() and not path.is_symlink():
        print(json.dumps(dict(Removed=False)))
        return
    reason = refusal(path)
    if reason:
        raise Refused(reason + ' It was left untouched: ' + str(path))
    failed = []
    handler = lambda function, name, error: failed.append(name)
    shutil.rmtree(path, **(dict(onexc=handler) if sys.version_info >= (3, 12) else dict(onerror=handler)))
    if failed:
        raise Refused(f'{len(failed)} {"item" if len(failed) == 1 else "items"} could not be deleted, such as {failed[0]}. '
                      'The rest of the folder was deleted.')
    print(json.dumps(dict(Removed=True)))


def remove(c):
    path, container = Path(c['Path']), Path(c['Container'])
    if path.parent != container or container.is_symlink():
        raise Refused('Only folders in the project workspaces folder can be removed.')
    for leftover in container.glob('.' + path.name + '.' + c['Token'] + '.creating'):
        shutil.rmtree(leftover)
    if not path.exists() and not path.is_symlink():
        print(json.dumps(dict(Removed=False)))
        return
    if path.is_symlink() or not owned(path, c['Token']):
        raise Refused('This folder is not the workspace the Manager created, so it was left untouched: ' + str(path))
    shutil.rmtree(path)
    print(json.dumps(dict(Removed=True)))


def adopt(c):
    """Hands a kept workspace folder to a new agent."""
    path = Path(c['Path'])
    if path.is_symlink() or not path.is_dir() or not owned(path, c['Token']):
        raise Refused('The kept folder is missing or no longer the one the Manager created.')
    (path / '.git' / MARKER).write_text(json.dumps(dict(Token=c['NewToken'])))
    print(json.dumps(dict(Branch=current_branch(path) or out(path, 'rev-parse', '--short', 'HEAD'))))


def pull(c):
    """Updates a clean workspace to its upstream before a new conversation starts there."""
    path = Path(c['Path'])
    if git(path, 'status', '--porcelain', '--untracked-files=all').stdout.strip():
        raise Refused('This workspace has uncommitted changes. Commit or discard them before updating.')
    tracked = git(path, 'rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{upstream}', check=False).stdout.strip()
    if not tracked:
        raise Refused('This branch has no remote branch to update from.')
    if int(out(path, 'rev-list', '--count', '@{upstream}..HEAD')) > 0:
        raise Refused('This workspace has commits that are not pushed. Push them before updating.')
    git(path, 'pull', '--quiet', '--ff-only')
    print(json.dumps(dict(Branch=current_branch(path))))


def branches(c):
    result = {}
    for path in c['Paths']:
        if Path(path, '.git').exists():
            result[path] = current_branch(path) or git(path, 'rev-parse', '--short', 'HEAD', check=False).stdout.strip() or None
    print(json.dumps(result))


PHASES = dict(inspect=inspect, preflight=preflight, create=create, verify=verify, changes=changes, remove=remove,
              adopt=adopt, pull=pull, branches=branches, folder=folder, measure=measure, delete=delete)

if __name__ == '__main__':
    os.umask(0o022)
    try:
        PHASES[sys.argv[1]](json.loads(sys.argv[2]))
    except Refused as error:
        print(str(error), file=sys.stderr)
        sys.exit(3)
