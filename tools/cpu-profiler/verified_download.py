"""Bounded, atomic downloads; cached bytes must pass the caller's identity check too."""
import hashlib
import http.client
import os
from pathlib import Path
import shutil
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request


def download_verified(url, output, verify, max_bytes):
    cache_dir = os.environ.get("CPU_DOWNLOAD_CACHE")
    cached = Path(cache_dir) / hashlib.sha256(url.encode()).hexdigest() if cache_dir else None
    partial = output.with_suffix(output.suffix + ".download")
    try:
        if cached is not None and cached.is_file():
            try:
                if cached.stat().st_size > max_bytes:
                    raise RuntimeError("Cached download exceeds the size limit")
                verify(cached)
            except (RuntimeError, OSError, ValueError):
                cached.unlink()
            else:
                shutil.copyfile(cached, partial)
                partial.replace(output)
                return
        for attempt in range(4):
            # A cached redirect can contain an expired storage signature. Ask the origin for a fresh redirect.
            request_url = url
            if attempt:
                parts = urllib.parse.urlsplit(url)
                query = urllib.parse.parse_qsl(parts.query) + [("dam_retry", str(time.time_ns()))]
                request_url = urllib.parse.urlunsplit(parts._replace(query=urllib.parse.urlencode(query)))
            request = urllib.request.Request(request_url, headers={"Cache-Control": "no-cache"})
            try:
                with urllib.request.urlopen(request, timeout=120) as response, partial.open("wb") as target:
                    length = response.headers.get("Content-Length")
                    size = 0
                    while chunk := response.read(1024 * 1024):
                        size += len(chunk)
                        if size > max_bytes:
                            raise RuntimeError(f"Download exceeds the size limit for {output.name}")
                        target.write(chunk)
                    if length is not None and size != int(length):
                        raise http.client.IncompleteRead(b"", max(0, int(length) - size))
                verify(partial)
                partial.replace(output)
                if cached is not None:
                    cached.parent.mkdir(parents=True, exist_ok=True)
                    with tempfile.NamedTemporaryFile(dir=cached.parent, delete=False) as entry:
                        temporary = Path(entry.name)
                    try:
                        shutil.copyfile(output, temporary)
                        temporary.replace(cached)
                    finally:
                        temporary.unlink(missing_ok=True)
                return
            except (urllib.error.URLError, TimeoutError, ConnectionError, http.client.IncompleteRead) as error:
                if isinstance(error, urllib.error.HTTPError):
                    redirected_403 = error.code == 403 and error.url not in (url, request_url)
                    if error.code not in (408, 429, 500, 502, 503, 504) and not redirected_403:
                        raise
                if attempt == 3:
                    raise
                delay = 2 ** (attempt + 1)
                print(f"Retrying {output.name} download in {delay}s (attempt {attempt + 2}/4): {error}", flush=True)
                time.sleep(delay)
            finally:
                partial.unlink(missing_ok=True)
    finally:
        partial.unlink(missing_ok=True)
