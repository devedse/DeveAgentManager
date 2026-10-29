"""Prepare a private packaged Codex terminal with a browser audio helper; preserve the installed package."""
import hashlib
import platform


def native_terminal(helper):
    installed = shutil.which('codex')
    if not installed:
        return 'codex', None
    executable = Path(installed).resolve()
    try:
        if executable.open('rb').read(4) != b'\x7fELF':
            architecture = 'aarch64' if platform.machine() in ('aarch64', 'arm64') else 'x86_64'
            packages = executable.parent.parent / 'node_modules' / '@openai'
            executable = next(path for path in packages.glob('codex-linux-*/vendor/*/bin/codex') if architecture in str(path))
        package = executable.parent.parent
        if not (package / 'codex-package.json').is_file() or not (package / 'codex-resources/voice/bin/codex-voice-host').is_file():
            return 'codex', None
        stat = executable.stat()
        key = hashlib.sha256((str(executable) + str(stat.st_mtime_ns) + str(stat.st_size) + helper).encode()).hexdigest()[:24]
        cache = Path.home() / '.deveagentmanager' / 'native-codex'
        cache.mkdir(parents=True, exist_ok=True)
        with (cache / 'prepare.lock').open('w') as preparation:
            fcntl.flock(preparation, fcntl.LOCK_EX)
            target = cache / key
            if not (target / '.dam-native-codex').is_file():
                pending = Path(tempfile.mkdtemp(prefix='pending-', dir=cache))
                try:
                    # Copy-on-write where supported; never hardlink editable working files or change the installed CLI.
                    subprocess.run(['cp', '-a', '--reflink=auto', str(package) + '/.', str(pending)], check=True,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    adapter = pending / 'codex-resources/voice/bin/codex-voice-host'
                    adapter.unlink()
                    adapter.write_text('#!' + sys.executable + '\n' + helper.split('\n', 1)[1])
                    adapter.chmod(0o700)
                    (pending / '.dam-native-codex').write_text('browser-audio-helper-v1')
                    pending.rename(target)
                finally:
                    if pending.exists():
                        shutil.rmtree(pending)
            lease = (target / 'lease.lock').open('w')
            fcntl.flock(lease, fcntl.LOCK_SH)
            # Only retire our own unused caches. Running terminals hold a shared lease on their exact package.
            for old in cache.iterdir():
                if old == target or not (old / '.dam-native-codex').is_file():
                    continue
                with (old / 'lease.lock').open('w') as old_lease:
                    try:
                        fcntl.flock(old_lease, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    except BlockingIOError:
                        continue
                    shutil.rmtree(old)
            return str(target / 'bin/codex'), lease
    except (OSError, StopIteration, subprocess.SubprocessError):
        return 'codex', None
