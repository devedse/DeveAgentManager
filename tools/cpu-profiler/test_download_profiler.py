"""Profiler retries and cache integrity, with no network or extracted native code."""
import hashlib
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.error

import verified_download
from test_download_symbols import Response, InterruptedResponse

spec = importlib.util.spec_from_file_location("profiler", Path(__file__).with_name("download-profiler.py"))
profiler = importlib.util.module_from_spec(spec)
spec.loader.exec_module(profiler)


class DownloadProfilerTests(unittest.TestCase):
    def setUp(self):
        scratch = self.enterContext(tempfile.TemporaryDirectory())
        self.output = Path(scratch) / "profiler.tar.gz"
        self.cache = Path(scratch) / "cache"
        self.enterContext(patch.dict(os.environ, {"CPU_DOWNLOAD_CACHE": str(self.cache)}))
        self.sleep = self.enterContext(patch.object(verified_download.time, "sleep"))
        self.data = b"verified test-only profiler archive"
        self.enterContext(patch.dict(profiler.HASHES, {arch: hashlib.sha256(self.data).hexdigest() for arch in profiler.HASHES}))

    def test_504_and_interrupted_body_retry_before_verification(self):
        for arch in profiler.HASHES:
            with self.subTest(arch=arch):
                responses = [urllib.error.HTTPError("https://download.example.com/archive", 504, "Gateway timeout", {}, None),
                             InterruptedResponse(b"partial"), Response(self.data)]
                with patch.object(verified_download.urllib.request, "urlopen", side_effect=responses) as request:
                    profiler.download_profiler(arch, self.output)
                self.assertEqual(request.call_count, 3)
                self.assertEqual(self.output.read_bytes(), self.data)
                urls = [call.args[0].full_url for call in request.call_args_list]
                self.assertNotEqual(urls[0], urls[1], "A retry must not reuse a stale signed redirect")

    def test_verified_cache_works_offline_but_corruption_is_refetched(self):
        with patch.object(verified_download.urllib.request, "urlopen", return_value=Response(self.data)):
            profiler.download_profiler("x86_64", self.output)
        self.output.unlink()
        with patch.object(verified_download.urllib.request, "urlopen", side_effect=AssertionError("Must use verified cache")):
            profiler.download_profiler("x86_64", self.output)
        self.assertEqual(self.output.read_bytes(), self.data)
        cached = next(self.cache.iterdir())
        cached.write_bytes(b"corrupt cache")
        with patch.object(verified_download.urllib.request, "urlopen", return_value=Response(self.data)) as request:
            profiler.download_profiler("x86_64", self.output)
        self.assertEqual(request.call_count, 1)
        self.assertEqual(cached.read_bytes(), self.data)

    def test_wrong_checksum_never_replaces_good_output_or_enters_cache(self):
        self.output.write_bytes(self.data)
        with patch.object(verified_download.urllib.request, "urlopen", return_value=Response(b"wrong archive")) as request:
            with self.assertRaisesRegex(RuntimeError, "checksum mismatch"):
                profiler.download_profiler("x86_64", self.output)
        self.assertEqual(request.call_count, 1)
        self.sleep.assert_not_called()
        self.assertEqual(self.output.read_bytes(), self.data)
        self.assertFalse(self.cache.exists())
        self.assertFalse(self.output.with_suffix(".gz.download").exists())

    def test_permanent_origin_403_does_not_retry(self):
        def forbidden(request, **kwargs):
            raise urllib.error.HTTPError(request.full_url, 403, "Forbidden", {}, None)
        with patch.object(verified_download.urllib.request, "urlopen", side_effect=forbidden) as request:
            with self.assertRaises(urllib.error.HTTPError):
                profiler.download_profiler("x86_64", self.output)
        self.assertEqual(request.call_count, 1)
        self.sleep.assert_not_called()


if __name__ == "__main__":
    unittest.main()
