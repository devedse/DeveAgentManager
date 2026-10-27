"""Download Microsoft ELF symbols by build ID and reject a mismatched debug file."""
import http.client
import re
from pathlib import Path
import shutil
import subprocess
import time
import urllib.error
import urllib.request


def build_id(path):
    notes = subprocess.check_output(["readelf", "--notes", str(path)], text=True)
    match = re.search(r"Build ID: ([a-f0-9]{16,40})", notes)
    if not match:
        raise RuntimeError(f"No ELF build ID for {path.name}")
    return match.group(1).ljust(40, "0")


MAX_SYMBOL_BYTES = 512 * 1024 * 1024


def download_symbols(url, output, identity):
    partial = output.with_suffix(output.suffix + ".download")
    for attempt in range(4):
        try:
            with urllib.request.urlopen(url, timeout=120) as response, partial.open("wb") as target:
                length = response.headers.get("Content-Length")
                size = 0
                while chunk := response.read(1024 * 1024):
                    size += len(chunk)
                    if size > MAX_SYMBOL_BYTES:
                        raise RuntimeError(f"Debug symbols exceed the size limit for {output.name}")
                    target.write(chunk)
                if length is not None and size != int(length):
                    raise http.client.IncompleteRead(b"", max(0, int(length) - size))
            if build_id(partial) != identity:
                raise RuntimeError(f"Debug symbols do not match {output.name}")
            partial.replace(output)
            return
        except (urllib.error.URLError, TimeoutError, ConnectionError, http.client.IncompleteRead) as error:
            # A missing file or invalid identity needs a fix; retry only transient transport failures.
            # msdl redirects to signed storage URLs that occasionally reject their own signature; a new redirect fixes it.
            if isinstance(error, urllib.error.HTTPError) and error.code not in (408, 429, 500, 502, 503, 504) \
                    and not (error.code == 403 and error.url != url):
                raise
            if attempt == 3:
                raise
            delay = 2 ** (attempt + 1)
            print(f"Retrying {output.name} download in {delay}s (attempt {attempt + 2}/4): {error}", flush=True)
            time.sleep(delay)
        finally:
            partial.unlink(missing_ok=True)


def main():
    root = Path("/usr/share/dotnet/shared/Microsoft.NETCore.App")
    runtime = next(root.iterdir())
    symbols = Path("/runtime-symbols") / runtime.name
    symbols.mkdir(parents=True)
    for name in ("libcoreclr.so", "libclrjit.so", "libSystem.Native.so", "libSystem.Security.Cryptography.Native.OpenSsl.so"):
        identity = build_id(runtime / name)
        # Microsoft.SymbolStore ELFFileKeyGenerator uses _.debug and a normalized GNU build ID.
        url = f"https://msdl.microsoft.com/download/symbols/_.debug/elf-buildid-sym-{identity}/_.debug"
        output = runtime / (name + ".dbg")
        download_symbols(url, output, identity)
        shutil.copyfile(output, symbols / output.name)
        print(f"Verified {name}: ELF build ID {identity}", flush=True)


if __name__ == "__main__":
    main()
