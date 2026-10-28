"""Exercise transient failures without contacting a live symbol server."""
import http.client
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.error
import verified_download

spec = importlib.util.spec_from_file_location("download_symbols", Path(__file__).with_name("download-symbols.py"))
symbols = importlib.util.module_from_spec(spec)
spec.loader.exec_module(symbols)
URL = "https://symbols.example.com/test.debug"


class Response(io.BytesIO):
    def __init__(self, data, length=None):
        super().__init__(data)
        self.headers = {"Content-Length": str(len(data) if length is None else length)}


class InterruptedResponse(Response):
    def read(self, size=-1):
        if self.tell():
            raise http.client.IncompleteRead(b"partial")
        return super().read(size)


class DownloadSymbolsTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.output = Path(self.scratch.name) / "test.dbg"
        self.output.write_bytes(b"previous verified symbols")
        self.sleep = self.enterContext(patch.object(verified_download.time, "sleep"))
        self.identity = self.enterContext(patch.object(symbols, "build_id", return_value="expected"))

    def check_preserved(self):
        self.assertEqual(self.output.read_bytes(), b"previous verified symbols")
        self.assertEqual(list(self.output.parent.iterdir()), [self.output])

    def test_retries_503_and_interrupted_body_then_verifies_complete_file(self):
        responses = [urllib.error.HTTPError(URL, 503, "Unavailable", {}, None),
                     InterruptedResponse(b"incomplete symbols"), Response(b"complete symbols")]
        with patch.object(verified_download.urllib.request, "urlopen", side_effect=responses) as request:
            symbols.download_symbols(URL, self.output, "expected")
        self.assertEqual(request.call_count, 3)
        self.assertEqual(self.output.read_bytes(), b"complete symbols")
        self.identity.assert_called_once()
        self.assertEqual([call.args[0] for call in self.sleep.call_args_list], [2, 4])
        self.assertEqual(list(self.output.parent.iterdir()), [self.output])

    def test_short_response_retries_before_checking_identity(self):
        responses = [Response(b"short", length=100), Response(b"complete symbols")]
        with patch.object(verified_download.urllib.request, "urlopen", side_effect=responses) as request:
            symbols.download_symbols(URL, self.output, "expected")
        self.assertEqual(request.call_count, 2)
        self.identity.assert_called_once()
        self.assertEqual(self.output.read_bytes(), b"complete symbols")

    def test_missing_symbols_fail_without_retry(self):
        with patch.object(verified_download.urllib.request, "urlopen", side_effect=urllib.error.HTTPError(URL, 404, "Missing", {}, None)) as request:
            with self.assertRaises(urllib.error.HTTPError):
                symbols.download_symbols(URL, self.output, "expected")
        self.assertEqual(request.call_count, 1)
        self.sleep.assert_not_called()
        self.check_preserved()

    def test_transport_retries_are_bounded_and_leave_no_partial_symbols(self):
        with patch.object(verified_download.urllib.request, "urlopen", side_effect=lambda *a, **k: InterruptedResponse(b"partial")) as request:
            with self.assertRaises(http.client.IncompleteRead):
                symbols.download_symbols(URL, self.output, "expected")
        self.assertEqual(request.call_count, 4)
        self.assertEqual(self.sleep.call_count, 3)
        self.identity.assert_not_called()
        self.check_preserved()

    def test_retries_403_from_redirected_storage_but_not_from_symbol_server(self):
        storage = "https://storage.example.com/test.debug?sig=expired"
        responses = [urllib.error.HTTPError(storage, 403, "Server failed to authenticate", {}, None), Response(b"complete symbols")]
        with patch.object(verified_download.urllib.request, "urlopen", side_effect=responses) as request:
            symbols.download_symbols(URL, self.output, "expected")
        self.assertEqual(request.call_count, 2)
        self.assertEqual(self.output.read_bytes(), b"complete symbols")
        with patch.object(verified_download.urllib.request, "urlopen", side_effect=urllib.error.HTTPError(URL, 403, "Forbidden", {}, None)) as request:
            with self.assertRaises(urllib.error.HTTPError):
                symbols.download_symbols(URL, self.output, "expected")
        self.assertEqual(request.call_count, 1)

    def test_identity_mismatch_fails_without_retry_or_replacing_good_file(self):
        self.identity.return_value = "wrong"
        with patch.object(verified_download.urllib.request, "urlopen", return_value=Response(b"wrong symbols")) as request:
            with self.assertRaisesRegex(RuntimeError, "do not match"):
                symbols.download_symbols(URL, self.output, "expected")
        self.assertEqual(request.call_count, 1)
        self.sleep.assert_not_called()
        self.check_preserved()

    def test_oversized_symbols_fail_before_verification(self):
        with patch.object(symbols, "MAX_SYMBOL_BYTES", 4), patch.object(verified_download.urllib.request, "urlopen", return_value=Response(b"too large")):
            with self.assertRaisesRegex(RuntimeError, "size limit"):
                symbols.download_symbols(URL, self.output, "expected")
        self.identity.assert_not_called()
        self.sleep.assert_not_called()
        self.check_preserved()


if __name__ == "__main__":
    unittest.main()
