import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import uuid
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("share", Path(__file__).with_name("share-image.py"))
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


if __name__ == "__main__":
    unittest.main()
