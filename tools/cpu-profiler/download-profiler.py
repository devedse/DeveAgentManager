"""Fetch the exact native profiler for the target image, verifying the release's SHA-256."""
import hashlib
import io
import os
from pathlib import Path
import tarfile
import urllib.request

arch = {"amd64": "x86_64", "arm64": "aarch64"}[os.environ["TARGETARCH"]]
expected = {
    "x86_64": "06bc3888e45d53cecefc3263db957833b1c0cb082f9129d1b1e5f63c84b88d18",
    "aarch64": "745752f5f07dd51a6d0fbbcfe4d5a0e0a2d7f9a9286eb661a84b2370a595e87d",
}[arch]
url = f"https://github.com/grafana/pyroscope-dotnet/releases/download/pyroscope-1.5.1/pyroscope.1.5.1-glibc-{arch}.tar.gz"
with urllib.request.urlopen(url, timeout=120) as response:
    data = response.read(100 * 1024 * 1024)
if hashlib.sha256(data).hexdigest() != expected:
    raise RuntimeError("Profiler release checksum mismatch")
target = Path("/cpu-profiler")
target.mkdir()
with tarfile.open(fileobj=io.BytesIO(data)) as archive:
    archive.extractall(target, filter="data")
# The native profiler checks the architecture-specific wrapper basename. Preserve it exactly.
assert next(target.glob("Pyroscope.Linux.ApiWrapper.*.so")).is_file()
