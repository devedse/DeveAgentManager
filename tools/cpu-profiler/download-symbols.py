"""Download Microsoft ELF symbols by build ID and reject a mismatched debug file."""
import re
from pathlib import Path
import shutil
import subprocess
from verified_download import download_verified


def build_id(path):
    try:
        notes = subprocess.check_output(["readelf", "--notes", str(path)], text=True)
    except subprocess.CalledProcessError as error:
        raise RuntimeError(f"Invalid ELF file: {path.name}") from error
    match = re.search(r"Build ID: ([a-f0-9]{16,40})", notes)
    if not match:
        raise RuntimeError(f"No ELF build ID for {path.name}")
    return match.group(1).ljust(40, "0")


MAX_SYMBOL_BYTES = 512 * 1024 * 1024


def download_symbols(url, output, identity):
    def verify(path):
        if build_id(path) != identity:
            raise RuntimeError(f"Debug symbols do not match {output.name}")
    download_verified(url, output, verify, MAX_SYMBOL_BYTES)


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
