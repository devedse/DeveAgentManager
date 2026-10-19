"""Create one immutable bundle whose hashes identify the exact binaries in this image."""
import hashlib
import json
from pathlib import Path
import zipfile

root = Path("/usr/share/dotnet/shared/Microsoft.NETCore.App")
runtime = next(root.iterdir())
publish = Path("/publish")
destination = Path("/diagnostics-symbols")
destination.mkdir()
files = sorted(runtime.glob("*.so*")) + sorted(publish.glob("DeveAgentManager*.dll")) + sorted(publish.glob("DeveAgentManager*.pdb"))
if not (runtime / "libcoreclr.so.dbg").exists():
    raise RuntimeError("Matching CoreCLR debug symbols are missing")
manifest = {"runtime": runtime.name, "profiler": "pyroscope-1.5.1", "files": []}
with zipfile.ZipFile(destination / "symbols.zip", "w", compression=zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
    for source in files:
        name = f"runtime/{source.name}" if source.parent == runtime else f"app/{source.name}"
        with source.open("rb") as data:
            digest = hashlib.file_digest(data, "sha256").hexdigest()
        manifest["files"].append({"file": name, "sha256": digest, "bytes": source.stat().st_size})
        archive.write(source, name)
    content = json.dumps(manifest, indent=2)
    archive.writestr("manifest.json", content)
(destination / "manifest.json").write_text(content)
