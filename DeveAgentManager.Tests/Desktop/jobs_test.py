"""Host-safe durable job contracts; no SSH, Manager, or workload driver."""
import importlib.util
import json
import pathlib
import sys
import tempfile
import unittest
import uuid

spec = importlib.util.spec_from_file_location("dam_tools", sys.argv.pop(1))
tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tools)


class Jobs(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="dam-jobs-")
        tools.JOBS = pathlib.Path(self.temp.name)
        self.ident = uuid.uuid4().hex
        self.folder = tools.job_folder(self.ident)
        self.folder.mkdir()
        self.event = dict(SchemaVersion=1, EventId="QueueCache-Campaign-test:terminal:1", CampaignId="QueueCache-Campaign-test",
                          ManifestSha256="a" * 64, Directory="C:\\Results\\owned\\QueueCache-Campaign-test", Status="COMPLETED",
                          ExpectedPhases=2, CompletedPhases=2, ExpectedCases=3, CollectedCases=3, Restoration="RESTORED",
                          Finished="2026-01-01T00:01:00Z")

    def tearDown(self):
        self.temp.cleanup()

    def record(self, command=None, event=None, exit_code=0):
        value = self.event if event is None else event
        return dict(id=str(uuid.UUID(self.ident)), agentId=str(uuid.uuid4()), sessionId=str(uuid.uuid4()),
                    caption="Test", machine="lab", outputParent="C:\\Results\\owned", cwd=self.temp.name,
                    outcome="CREATED", delivery="NONE", command=command or [sys.executable, "-c", "pass"],
                    prepareCommand=[sys.executable, "-c", "pass"],
                    completionCommand=[sys.executable, "-c", f"import sys;print({json.dumps(json.dumps(value))});sys.exit({exit_code})"])

    def test_valid_failure_and_cancellation_events_are_delivered(self):
        for status in ("COMPLETED", "INCOMPLETE", "RESTORATION_FAILED", "CANCELLED"):
            event = dict(self.event, Status=status)
            record = self.record(event=event)
            tools.atomic_job(self.folder / "record.json", record)
            tools.run_job_worker(self.ident)
            publication = tools.pending_jobs()[0]
            self.assertEqual("VALIDATED", publication["outcome"])
            self.assertEqual(status, publication["event"]["Status"])
            self.assertNotIn("command", publication)
            tools.ack_job(self.ident, "SENT")
            self.assertEqual([], tools.pending_jobs())

    def test_nonzero_workload_with_valid_failure_event(self):
        record = self.record(command=[sys.executable, "-c", "raise SystemExit(1)"], event=dict(self.event, Status="INCOMPLETE"))
        tools.atomic_job(self.folder / "record.json", record)
        tools.run_job_worker(self.ident)
        self.assertEqual(1, tools.pending_jobs()[0]["exitCode"])

    def test_transport_loss_and_missing_event_do_not_relaunch_work(self):
        count = self.folder / "count"
        record = self.record(command=[sys.executable, "-c", f"from pathlib import Path;Path({str(count)!r}).write_text('one');raise SystemExit(255)"], exit_code=1)
        tools.atomic_job(self.folder / "record.json", record)
        tools.run_job_worker(self.ident)
        self.assertEqual("INSPECTION_REQUIRED", tools.pending_jobs()[0]["outcome"])
        with self.assertRaises(ValueError):
            tools.run_job_worker(self.ident)
        record = json.loads((self.folder / "record.json").read_text())
        record["completionCommand"] = self.record()["completionCommand"]
        tools.atomic_job(self.folder / "record.json", record)
        tools.retry_completion(self.ident)
        self.assertEqual("one", count.read_text())
        self.assertEqual("VALIDATED", tools.pending_jobs()[0]["outcome"])

    def test_invalid_or_cross_job_evidence_is_never_published(self):
        for field, value in (("Directory", "C:\\Other\\QueueCache-Campaign-test"), ("EventId", "other"),
                             ("ManifestSha256", "invalid"), ("CollectedCases", None), ("Restoration", "FAILED")):
            with self.assertRaises(ValueError):
                tools.validate_completion(dict(self.event, **{field: value}), "C:\\Results\\owned")
        for field in self.event:
            event = dict(self.event)
            del event[field]
            with self.assertRaises(ValueError):
                tools.validate_completion(event, "C:\\Results\\owned")

    def test_worker_death_publishes_unknown_exit_without_stopping_other_processes(self):
        record = dict(self.record(), outcome="RUNNING", workerPid=99999999, workerIdentity="missing")
        tools.atomic_job(self.folder / "record.json", record)
        self.assertEqual("INSPECTION_REQUIRED", tools.pending_jobs()[0]["outcome"])
        self.assertIsNone(tools.pending_jobs()[0]["exitCode"])

    def test_already_reviewed_result_is_not_reported_as_delivered(self):
        tools.atomic_job(self.folder / "record.json", dict(self.record(), outcome="VALIDATED", delivery="PENDING", event=self.event))
        tools.ack_job(self.ident, "REVIEWED")
        self.assertEqual([], tools.pending_jobs())
        self.assertEqual("REVIEWED", json.loads((self.folder / "record.json").read_text())["delivery"])

    def test_restart_pending_acknowledgement_and_uncertain_delivery(self):
        tools.atomic_job(self.folder / "record.json", dict(self.record(), outcome="VALIDATED", delivery="PENDING", event=self.event))
        self.assertEqual(self.event, tools.pending_jobs()[0]["event"])
        tools.ack_job(self.ident, "UNCERTAIN")
        tools.ack_job(self.ident, "SENT")
        self.assertEqual("UNCERTAIN", json.loads((self.folder / "record.json").read_text())["delivery"])
        self.assertEqual([], tools.pending_jobs())


if __name__ == "__main__":
    unittest.main()
