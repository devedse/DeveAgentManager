"""Download Microsoft ELF symbols by build ID and reject a mismatched debug file."""
import re
from pathlib import Path
import shutil
import subprocess
import urllib.request


def build_id(path):
    notes = subprocess.check_output(["readelf", "--notes", str(path)], text=True)
    match = re.search(r"Build ID: ([a-f0-9]{16,40})", notes)
    if not match:
        raise RuntimeError(f"No ELF build ID for {path.name}")
    return match.group(1).ljust(40, "0")


root = Path("/usr/share/dotnet/shared/Microsoft.NETCore.App")
runtime = next(root.iterdir())
symbols = Path("/runtime-symbols") / runtime.name
symbols.mkdir(parents=True)
for name in ("libcoreclr.so", "libclrjit.so", "libSystem.Native.so", "libSystem.Security.Cryptography.Native.OpenSsl.so"):
    identity = build_id(runtime / name)
    # Microsoft.SymbolStore ELFFileKeyGenerator uses _.debug and a normalized GNU build ID.
    url = f"https://msdl.microsoft.com/download/symbols/_.debug/elf-buildid-sym-{identity}/_.debug"
    output = runtime / (name + ".dbg")
    with urllib.request.urlopen(url, timeout=120) as response, output.open("wb") as target:
        shutil.copyfileobj(response, target)
    if output.stat().st_size > 512 * 1024 * 1024 or build_id(output) != identity:
        raise RuntimeError(f"Debug symbols do not match {name}")
    shutil.copyfile(output, symbols / output.name)
    print(f"Verified {name}: ELF build ID {identity}", flush=True)
