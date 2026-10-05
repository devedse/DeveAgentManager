import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
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


if __name__ == "__main__":
    unittest.main()
