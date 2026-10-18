import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
import uuid
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("share", Path(__file__).with_name("dam-tools.py"))
share = importlib.util.module_from_spec(spec)
spec.loader.exec_module(share)


class ShareTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.home = Path(temp.name)
        self.root = self.home / "outbox"
        self.patcher = patch.object(share, "ROOT", self.root)
        self.patcher.start()
        self.addCleanup(self.patcher.stop)
        self.path = self.home / "image.png"
        self.path.write_bytes(b"\x89PNG\r\n\x1a\nexample")
        self.agent = str(uuid.uuid4())
        self.session = str(uuid.uuid4())

    def test_copy_is_durable_and_can_be_acknowledged_idempotently(self):
        record = share.publish(self.path, self.agent, self.session, "A\ncaption")
        self.path.unlink()
        self.assertEqual(self.agent, record["agentId"])
        self.assertEqual(self.session, record["sessionId"])
        self.assertEqual("A caption", record["caption"])
        self.assertEqual([record], share.pending())
        image = self.root / uuid.UUID(record["id"]).hex / "image"
        self.assertEqual(b"\x89PNG\r\n\x1a\nexample", image.read_bytes())
        self.assertEqual(0o600, image.stat().st_mode & 0o777)
        share.acknowledge(record["id"])
        share.acknowledge(record["id"])
        self.assertEqual([], share.pending())

    def test_invalid_images_sizes_and_ids_do_not_publish(self):
        self.path.write_text("<svg onload='alert(1)'></svg>")
        with self.assertRaises(ValueError):
            share.publish(self.path, self.agent, self.session, "")
        self.path.write_bytes(b"x" * (share.MAX_BYTES + 1))
        with self.assertRaises(ValueError):
            share.publish(self.path, self.agent, self.session, "")
        with self.assertRaises(ValueError):
            share.publish(self.path, "../../escape", self.session, "")
        self.assertFalse(self.root.exists())

    def test_pending_ignores_partial_and_corrupt_records(self):
        record = share.publish(self.path, self.agent, self.session, "")
        (self.root / ".pending-test").mkdir()
        corrupt = self.root / uuid.uuid4().hex
        corrupt.mkdir()
        (corrupt / "metadata.json").write_text("bad json")
        self.assertEqual([record], share.pending())
        with self.assertRaises(ValueError):
            share.acknowledge("../escape")


class FileShareTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.home = Path(temp.name)
        self.root = self.home / "files"
        for name, value in (("FILES", self.root), ("MAX_FILE_BYTES", 64)):
            patcher = patch.object(share, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        self.path = self.home / "app-release.apk"
        self.path.write_bytes(b"PK\x03\x04 an android package")
        self.agent = str(uuid.uuid4())
        self.session = str(uuid.uuid4())

    def test_copy_is_durable_checksummed_private_and_acknowledged_idempotently(self):
        record = share.publish_file(self.path, self.agent, self.session, "Debug\nbuild")
        self.path.unlink()
        data = b"PK\x03\x04 an android package"
        self.assertEqual((self.agent, self.session, "app-release.apk", "Debug build", len(data), hashlib.sha256(data).hexdigest()),
                         (record["agentId"], record["sessionId"], record["fileName"], record["caption"], record["bytes"], record["sha256"]))
        self.assertEqual([record], share.pending_files())
        copy = self.root / uuid.UUID(record["id"]).hex / "file"
        self.assertEqual(data, copy.read_bytes())
        self.assertEqual(0o600, copy.stat().st_mode & 0o777)
        share.acknowledge(record["id"], self.root)
        share.acknowledge(record["id"], self.root)
        self.assertEqual([], share.pending_files())

    def test_folders_special_empty_and_oversized_files_do_not_publish(self):
        with self.assertRaisesRegex(ValueError, "Archive it first"):
            share.publish_file(self.home, self.agent, self.session, "")
        fifo = self.home / "pipe"
        os.mkfifo(fifo)
        with self.assertRaisesRegex(ValueError, "not a regular file"):
            share.publish_file(fifo, self.agent, self.session, "")
        self.path.write_bytes(b"")
        with self.assertRaisesRegex(ValueError, "empty"):
            share.publish_file(self.path, self.agent, self.session, "")
        self.path.write_bytes(b"x" * 65)
        with self.assertRaisesRegex(ValueError, "up to 1 GiB"):
            share.publish_file(self.path, self.agent, self.session, "")
        with self.assertRaises(ValueError):
            share.publish_file(self.path, "../../escape", self.session, "")
        self.assertEqual([], [p for p in self.root.iterdir()] if self.root.exists() else [])

    def test_pending_ignores_changed_and_corrupt_records_and_clears_stale_staging(self):
        record = share.publish_file(self.path, self.agent, self.session, "")
        changed = share.publish_file(self.path, self.agent, self.session, "")
        (self.root / uuid.UUID(changed["id"]).hex / "file").write_bytes(b"truncated")
        corrupt = self.root / uuid.uuid4().hex
        corrupt.mkdir()
        (corrupt / "metadata.json").write_text("bad json")
        fresh, stale = self.root / ".pending-fresh", self.root / ".pending-stale"
        fresh.mkdir()
        stale.mkdir()
        old = time.time() - share.STALE_STAGING_SECONDS - 60
        os.utime(stale, (old, old))
        self.assertEqual([record], share.pending_files())
        self.assertTrue(fresh.exists())
        self.assertFalse(stale.exists())
        with self.assertRaises(ValueError):
            share.acknowledge("../escape", self.root)

    def test_sizes_read_like_a_person_would_write_them(self):
        self.assertEqual(["12 bytes", "1.5 KiB", "12.4 MiB", "2.0 GiB"],
                         [share.size_text(n) for n in (12, 1536, int(12.4 * 1024 * 1024), 2 * 1024 ** 3)])


class CommandLineTests(unittest.TestCase):
    def run_tool(self, *args, env=None):
        environment = {k: v for k, v in os.environ.items() if not k.startswith("DAM_")}
        environment.update(env or {})
        return subprocess.run([sys.executable, str(Path(__file__).with_name("dam-tools.py")), *args],
                              capture_output=True, text=True, env=environment)

    def test_help_lists_share_image_without_internal_options(self):
        result = self.run_tool("--help")
        self.assertEqual(0, result.returncode)
        self.assertIn("share-image", result.stdout)
        self.assertIn("share-file", result.stdout)
        self.assertNotIn("pending", result.stdout)
        detail = self.run_tool("share-image", "--help")
        self.assertIn("--caption", detail.stdout)
        self.assertNotIn("--agent", detail.stdout)

    def test_share_image_outside_an_agent_explains_itself(self):
        result = self.run_tool("share-image", "/nonexistent.png")
        self.assertEqual(2, result.returncode)
        self.assertIn("Run from an agent", result.stderr)

    def test_share_image_from_an_agent_uses_its_environment(self):
        with tempfile.TemporaryDirectory() as home:
            image = Path(home) / "shot.png"
            image.write_bytes(b"\x89PNG\r\n\x1a\nexample")
            agent, session = str(uuid.uuid4()), str(uuid.uuid4())
            result = self.run_tool("share-image", str(image), "--caption", "Login page",
                                   env={"HOME": home, "DAM_AGENT_ID": agent, "DAM_SESSION_ID": session})
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("Image shared: Login page", result.stdout)
            pending = self.run_tool("--pending-images", env={"HOME": home})
            [record] = json.loads(pending.stdout)
            self.assertEqual((agent, session, "Login page"), (record["agentId"], record["sessionId"], record["caption"]))


    def test_share_file_from_an_agent_uses_its_environment_and_explains_where_it_goes(self):
        with tempfile.TemporaryDirectory() as home:
            build = Path(home) / "Setup.exe"
            build.write_bytes(b"MZ installer")
            agent, session = str(uuid.uuid4()), str(uuid.uuid4())
            result = self.run_tool("share-file", str(build), "--caption", "Windows installer",
                                   env={"HOME": home, "DAM_AGENT_ID": agent, "DAM_SESSION_ID": session})
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("File shared: Setup.exe (12 bytes, SHA-256 " + hashlib.sha256(b"MZ installer").hexdigest(), result.stdout)
            self.assertIn("Shared files", result.stdout)
            [record] = json.loads(self.run_tool("--pending-files", env={"HOME": home}).stdout)
            self.assertEqual((agent, session, "Windows installer"), (record["agentId"], record["sessionId"], record["caption"]))
            self.assertEqual(0, self.run_tool("--ack-file", record["id"], env={"HOME": home}).returncode)
            self.assertEqual([], json.loads(self.run_tool("--pending-files", env={"HOME": home}).stdout))
            outside = self.run_tool("share-file", str(build))
            self.assertEqual(2, outside.returncode)
            self.assertIn("Run from an agent", outside.stderr)
            missing = self.run_tool("share-file", str(Path(home) / "missing.apk"),
                                    env={"HOME": home, "DAM_AGENT_ID": agent, "DAM_SESSION_ID": session})
            self.assertEqual(1, missing.returncode)
            self.assertIn("dam-tools:", missing.stderr)


class ArtifactDownloadTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.home = Path(temp.name)
        self.requests = self.home / "requests"
        self.requests.mkdir()
        self.agent, self.session = str(uuid.uuid4()), str(uuid.uuid4())
        self.record = dict(id=str(uuid.uuid4()), agentId=self.agent, sessionId=self.session, operation="fetch",
                           artifactId=str(uuid.uuid4()), token="test-only-capability")
        self.folder = self.requests / uuid.UUID(self.record["id"]).hex
        self.folder.mkdir()
        (self.folder / "request.json").write_text(json.dumps(self.record))
        self.data = b"a file from another runner"
        (self.folder / "content").write_bytes(self.data)
        self.response = dict(bytes=len(self.data), sha256=hashlib.sha256(self.data).hexdigest(),
                             artifact=dict(id=str(uuid.uuid4()), fileName="../../report.txt"))
        for name, value in (("ARTIFACT_REQUESTS", self.requests), ("ARTIFACT_CACHE", self.home / "cache")):
            patcher = patch.object(share, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def test_verified_download_is_private_and_stays_inside_callers_cache(self):
        target = share.receive_artifact(self.folder, self.response, self.agent)
        self.assertEqual(self.data, target.read_bytes())
        self.assertTrue(target.is_relative_to(share.ARTIFACT_CACHE / uuid.UUID(self.agent).hex))
        self.assertEqual("report.txt", target.name)
        self.assertEqual(0o600, target.stat().st_mode & 0o777)
        self.assertEqual(target, share.receive_artifact(self.folder, self.response, self.agent))
        with self.assertRaises(FileExistsError):
            share.receive_artifact(self.folder, self.response, self.agent, str(target))
        self.assertEqual(self.data, target.read_bytes())
        self.assertFalse(list(target.parent.glob(".pending-*")))

    def test_bad_checksum_truncation_oversize_and_symlink_never_install_content(self):
        target = self.home / "download.txt"
        for data in (b"changed", self.data[:-1], self.data + b"more"):
            (self.folder / "content").write_bytes(data)
            with self.assertRaises(ValueError):
                share.receive_artifact(self.folder, self.response, self.agent, str(target))
            self.assertFalse(target.exists())
            self.assertFalse(list(self.home.glob(".pending-*")))
        (self.folder / "content").unlink()
        (self.folder / "content").symlink_to(self.home / "outside")
        with self.assertRaises(OSError):
            share.receive_artifact(self.folder, self.response, self.agent, str(target))
        with self.assertRaises(ValueError):
            share.prepare_artifact_response(self.record["id"])

    def test_pending_is_bounded_ignores_partial_records_and_completed_requests(self):
        self.assertEqual([self.record], share.pending_artifacts())
        (self.folder / "response.json").write_text("{}")
        self.assertEqual([], share.pending_artifacts())
        with self.assertRaises(ValueError):
            share.artifact_folder("../outside")


if __name__ == "__main__":
    unittest.main()
