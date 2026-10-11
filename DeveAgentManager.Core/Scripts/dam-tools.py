#!/usr/bin/env python3
"""DeveAgentManager tools for agents running on this runner."""
import argparse
import fcntl
import hashlib
import stat
import subprocess
import signal
import time
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import sys
import uuid

ROOT = Path.home() / ".deveagentmanager/shared-images"
MAX_BYTES = 10 * 1024 * 1024
FILES = Path.home() / ".deveagentmanager/shared-files"
MAX_FILE_BYTES = 1024 * 1024 * 1024
# A copy interrupted by a crash leaves a staging folder; no real copy takes this long.
STALE_STAGING_SECONDS = 6 * 60 * 60


def image_type(data):
    if data.startswith(b"\x89PNG\r\n\x1a\n"):
        return "image/png"
    if data.startswith(b"\xff\xd8\xff"):
        return "image/jpeg"
    if data[:6] in (b"GIF87a", b"GIF89a"):
        return "image/gif"
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return "image/webp"
    raise ValueError("Use a PNG, JPEG, GIF or WebP image (up to 10 MiB).")


def clean(text, limit):
    return " ".join(text.split())[:limit]


def publish(path, agent, session, caption):
    agent = uuid.UUID(agent).hex
    session = uuid.UUID(session).hex
    path = Path(path).expanduser()
    # Open first and cap the actual read, rather than trusting a prior stat.
    with path.open("rb") as source:
        data = source.read(MAX_BYTES + 1)
    if not data or len(data) > MAX_BYTES:
        raise ValueError("Images must be between 1 byte and 10 MiB.")
    content_type = image_type(data)
    ident = uuid.uuid4().hex
    ROOT.mkdir(parents=True, mode=0o700, exist_ok=True)
    temporary = ROOT / (".pending-" + ident)
    temporary.mkdir(mode=0o700)
    try:
        (temporary / "image").write_bytes(data)
        (temporary / "image").chmod(0o600)
        record = dict(id=str(uuid.UUID(ident)), agentId=str(uuid.UUID(agent)), sessionId=str(uuid.UUID(session)),
                      fileName=clean(path.name, 128), caption=clean(caption, 500),
                      contentType=content_type, bytes=len(data),
                      createdAt=datetime.now(timezone.utc).isoformat())
        (temporary / "metadata.json").write_text(json.dumps(record))
        (temporary / "metadata.json").chmod(0o600)
        temporary.rename(ROOT / ident)
        return record
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)


def pending():
    records = []
    if ROOT.exists():
        for folder in sorted(ROOT.iterdir()):
            if len(records) >= 20:
                break
            try:
                if folder.is_symlink() or uuid.UUID(folder.name).hex != folder.name:
                    continue
                metadata = folder / "metadata.json"
                if metadata.is_symlink() or metadata.stat().st_size > 4096:
                    continue
                record = json.loads(metadata.read_text())
                if not isinstance(record, dict) or uuid.UUID(record.get("id", "")).hex != folder.name:
                    continue
                uuid.UUID(record["agentId"])
                uuid.UUID(record["sessionId"])
                if (not isinstance(record.get("caption"), str) or not isinstance(record.get("fileName"), str)
                        or not isinstance(record.get("bytes"), int) or not 0 < record["bytes"] <= MAX_BYTES
                        or (folder / "image").is_symlink()):
                    continue
                records.append(record)
            except (ValueError, OSError, KeyError, TypeError, AttributeError):
                continue
    return records


def acknowledge(ident, root=None):
    folder = (root or ROOT) / uuid.UUID(ident).hex
    if folder.is_symlink():
        raise ValueError("Invalid outbox folder.")
    if folder.exists():
        shutil.rmtree(folder)


def size_text(count):
    if count < 1024:
        return f"{count} bytes"
    for unit in ("KiB", "MiB", "GiB"):
        count /= 1024
        if count < 1024 or unit == "GiB":
            return f"{count:.1f} {unit}"


def publish_file(path, agent, session, caption):
    agent = uuid.UUID(agent).hex
    session = uuid.UUID(session).hex
    path = Path(path).expanduser()
    if path.is_dir():
        raise ValueError(f"{path} is a folder. Archive it first, e.g. zip -r {path.name}.zip {path.name}")
    # Non-blocking, so a FIFO is refused instead of waiting for a writer.
    descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_NONBLOCK", 0))
    with os.fdopen(descriptor, "rb") as source:
        if not stat.S_ISREG(os.fstat(source.fileno()).st_mode):
            raise ValueError(f"{path} is not a regular file.")
        ident = uuid.uuid4().hex
        FILES.mkdir(parents=True, mode=0o700, exist_ok=True)
        temporary = FILES / (".pending-" + ident)
        temporary.mkdir(mode=0o700)
        try:
            digest = hashlib.sha256()
            size = 0
            # Cap what is actually read, rather than trusting a prior stat: the file may still be growing.
            with os.fdopen(os.open(temporary / "file", os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "wb") as target:
                while chunk := source.read(1024 * 1024):
                    size += len(chunk)
                    if size > MAX_FILE_BYTES:
                        raise ValueError("Files can be up to 1 GiB.")
                    digest.update(chunk)
                    target.write(chunk)
                target.flush()
                os.fsync(target.fileno())
            if size == 0:
                raise ValueError(f"{path.name} is empty.")
            record = dict(id=str(uuid.UUID(ident)), agentId=str(uuid.UUID(agent)), sessionId=str(uuid.UUID(session)),
                          fileName=clean(path.name, 128), caption=clean(caption, 500), bytes=size,
                          sha256=digest.hexdigest(), createdAt=datetime.now(timezone.utc).isoformat())
            (temporary / "metadata.json").write_text(json.dumps(record))
            (temporary / "metadata.json").chmod(0o600)
            temporary.rename(FILES / ident)
            return record
        finally:
            if temporary.exists():
                shutil.rmtree(temporary)


def is_sha256(value):
    return isinstance(value, str) and len(value) == 64 and all(c in "0123456789abcdef" for c in value)


def pending_files():
    records = []
    if FILES.exists():
        for folder in sorted(FILES.iterdir()):
            try:
                if folder.name.startswith(".pending-"):
                    if not folder.is_symlink() and time.time() - folder.stat().st_mtime > STALE_STAGING_SECONDS:
                        shutil.rmtree(folder)
                    continue
                if len(records) >= 20:
                    continue
                if folder.is_symlink() or uuid.UUID(folder.name).hex != folder.name:
                    continue
                metadata, data = folder / "metadata.json", folder / "file"
                if metadata.is_symlink() or data.is_symlink() or metadata.stat().st_size > 4096:
                    continue
                record = json.loads(metadata.read_text())
                if not isinstance(record, dict) or uuid.UUID(record.get("id", "")).hex != folder.name:
                    continue
                uuid.UUID(record["agentId"])
                uuid.UUID(record["sessionId"])
                if (not isinstance(record.get("caption"), str) or not isinstance(record.get("fileName"), str)
                        or type(record.get("bytes")) is not int or not 0 < record["bytes"] <= MAX_FILE_BYTES
                        or data.stat().st_size != record["bytes"] or not is_sha256(record.get("sha256"))):
                    continue
                records.append(record)
            except (ValueError, OSError, KeyError, TypeError, AttributeError):
                continue
    return records


def share_image(args, parser):
    if not args.agent or not args.session:
        parser.error("Run from an agent, or supply --agent and --session UUIDs.")
    record = publish(args.path, args.agent, args.session, args.caption)
    print("Image shared: " + (record["caption"] or record["fileName"]))
    print("The user sees it under Shared images in this agent's conversation.")


def share_file(args, parser):
    if not args.agent or not args.session:
        parser.error("Run from an agent, or supply --agent and --session UUIDs.")
    record = publish_file(args.path, args.agent, args.session, args.caption)
    print(f"File shared: {record['fileName']} ({size_text(record['bytes'])}, SHA-256 {record['sha256']})")
    print("The user can download it under Shared files in this agent's conversation once the Manager has copied it, "
          "usually within seconds.")



JOBS = Path.home() / ".deveagentmanager/jobs"


def atomic_job(path, value):
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    with temporary.open("x") as stream:
        os.chmod(temporary, 0o600)
        json.dump(value, stream)
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)
    descriptor = os.open(path.parent, os.O_RDONLY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def argv_json(value):
    args = json.loads(value)
    if not isinstance(args, list) or not args or len(args) > 256 or any(
            not isinstance(a, str) or "\x00" in a or len(a) > 32768 for a in args):
        raise ValueError("Command must be a nonempty JSON array of argument strings.")
    return args


def job_folder(ident):
    folder = JOBS / uuid.UUID(ident).hex
    if folder.is_symlink():
        raise ValueError("Invalid job folder.")
    return folder


def validate_completion(event, output_parent):
    # The supplied completion command must be the workload's supported read-only validator.
    # It runs on the workload machine, where its absolute path/digest checks are meaningful.
    from pathlib import PureWindowsPath, PurePosixPath
    fields = ("SchemaVersion", "EventId", "CampaignId", "ManifestSha256", "Directory", "Status",
              "ExpectedPhases", "CompletedPhases", "ExpectedCases", "CollectedCases", "Restoration", "Finished")
    if not isinstance(event, dict) or any(key not in event for key in fields):
        raise ValueError("Completion validator returned missing fields.")
    if event["SchemaVersion"] != 1 or event["EventId"] != event["CampaignId"] + ":terminal:1":
        raise ValueError("Completion identity/schema mismatch.")
    if event["Status"] not in ("COMPLETED", "INCOMPLETE", "RESTORATION_FAILED", "CANCELLED"):
        raise ValueError("Invalid terminal status.")
    digest = event["ManifestSha256"]
    if not isinstance(digest, str) or len(digest) != 64 or any(c not in "0123456789abcdefABCDEF" for c in digest):
        raise ValueError("Invalid manifest digest.")
    path_type = PureWindowsPath if PureWindowsPath(output_parent).drive else PurePosixPath
    path = path_type(event["Directory"])
    if not path.is_absolute() or path.parent != path_type(output_parent) or path.name != event["CampaignId"]:
        raise ValueError("Completion belongs to another job/output parent.")
    for key in ("ExpectedPhases", "CompletedPhases"):
        if type(event[key]) is not int or event[key] < 0:
            raise ValueError("Invalid phase count.")
    for key in ("ExpectedCases", "CollectedCases"):
        if event[key] is not None and (type(event[key]) is not int or event[key] < 0):
            raise ValueError("Invalid case count.")
    if event["Status"] == "COMPLETED" and (event["CompletedPhases"] != event["ExpectedPhases"] or
            event["CollectedCases"] != event["ExpectedCases"] or event["Restoration"] != "RESTORED"):
        raise ValueError("Incomplete campaign cannot be successful.")
    if not isinstance(event["Finished"], str):
        raise ValueError("Missing finish time.")
    return event


def complete_job(folder, record):
    with (folder / "completion.stdout").open("wb") as output, (folder / "completion.stderr").open("wb") as error:
        result = subprocess.run(record["completionCommand"], cwd=record["cwd"], stdout=output, stderr=error, timeout=120)
    if result.returncode != 0:
        raise ValueError("Completion validator failed; inspect job evidence. Remote transport failure does not prove workload exit.")
    raw = (folder / "completion.stdout").read_bytes()
    if len(raw) > 32768:
        raise ValueError("Oversized completion event.")
    event = validate_completion(json.loads(raw), record["outputParent"])
    previous = record.get("event")
    if previous is not None and previous != event:
        raise ValueError("Immutable job event changed.")
    record.update(event=event, outcome="VALIDATED", delivery="PENDING", error=None)
    atomic_job(folder / "record.json", record)


def run_job_worker(ident):
    folder = job_folder(ident)
    with (folder / "worker.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        record = json.loads((folder / "record.json").read_text())
        if record["outcome"] != "CREATED":
            raise ValueError("Job already launched; reconnect or validate completion, never relaunch.")
        # Persist ambiguous state before spawn: a crash here requires inspection, not replay.
        record.update(outcome="STARTING", workerPid=os.getpid(), workerIdentity=process_identity(os.getpid()))
        atomic_job(folder / "record.json", record)
        try:
            with (folder / "prepare.stdout").open("wb") as output, (folder / "prepare.stderr").open("wb") as error:
                prepared = subprocess.run(record["prepareCommand"], cwd=record["cwd"], stdout=output, stderr=error, timeout=120)
            if prepared.returncode != 0:
                raise ValueError("Empty unique output-parent preparation failed; workload not launched.")
            with (folder / "stdout").open("wb") as output, (folder / "stderr").open("wb") as error:
                process = subprocess.Popen(record["command"], cwd=record["cwd"], stdout=output, stderr=error, start_new_session=True)
                record.update(outcome="RUNNING", pid=process.pid, processIdentity=process_identity(process.pid), started=datetime.now(timezone.utc).isoformat())
                atomic_job(folder / "record.json", record)
                record["exitCode"] = process.wait()  # process exit wakes the controller, no agent polling
            record.update(outcome="EXITED", finished=datetime.now(timezone.utc).isoformat())
            atomic_job(folder / "record.json", record)
            complete_job(folder, record)
        except Exception as error:
            record.update(outcome="INSPECTION_REQUIRED", error=(type(error).__name__ + ": completion/preparation failed; inspect the exact private job logs."), delivery="PENDING")
            atomic_job(folder / "record.json", record)


def launch_job(args, parser):
    if not args.agent or not args.session:
        parser.error("Run from an agent or supply owner UUIDs.")
    ident = uuid.uuid4().hex
    JOBS.mkdir(parents=True, mode=0o700, exist_ok=True)
    folder = job_folder(ident)
    folder.mkdir(mode=0o700)
    record = dict(id=str(uuid.UUID(ident)), agentId=str(uuid.UUID(args.agent)), sessionId=str(uuid.UUID(args.session)),
                  machine=clean(args.machine, 128), caption=clean(args.caption, 200), outputParent=args.output_parent,
                  command=argv_json(args.command_json), prepareCommand=argv_json(args.prepare_json), completionCommand=argv_json(args.completion_json),
                  cwd=str(Path.cwd()), created=datetime.now(timezone.utc).isoformat(), outcome="CREATED", delivery="NONE")
    atomic_job(folder / "record.json", record)  # ownership precedes spawn
    with (folder / "controller.log").open("wb") as log:
        subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "--job-worker", ident],
                         stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True, close_fds=True)
    print(json.dumps(dict(jobId=record["id"], evidence=str(folder), outcome="CREATED")))


def pending_jobs():
    records = []
    if JOBS.exists():
        for folder in sorted(JOBS.iterdir()):
            if len(records) >= 20:
                break
            try:
                if folder.is_symlink() or uuid.UUID(folder.name).hex != folder.name:
                    continue
                source = folder / "record.json"
                if source.is_symlink() or source.stat().st_size > 262144:
                    continue
                record = json.loads(source.read_text())
                if uuid.UUID(record["id"]).hex != folder.name:
                    continue
                if record["outcome"] in ("STARTING", "RUNNING", "EXITED"):
                    try:
                        alive = record.get("workerIdentity") == process_identity(record["workerPid"])
                    except (OSError, KeyError):
                        alive = False
                    if not alive:
                        with (folder / "worker.lock").open("a") as lock:
                            try:
                                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                            except BlockingIOError:
                                continue
                            record = json.loads(source.read_text())
                            if record["outcome"] in ("STARTING", "RUNNING", "EXITED"):
                                record.update(outcome="INSPECTION_REQUIRED", delivery="PENDING",
                                    error="Owned job controller disappeared; local/remote workload exit is unknown. Inspect evidence; do not relaunch.")
                                atomic_job(source, record)
                if record["delivery"] != "PENDING":
                    continue
                # Launch commands/transport credentials never cross the publication boundary.
                records.append(public_job(record))
            except (ValueError, OSError, KeyError, TypeError):
                continue
    return records


def ack_job(ident, delivery):
    if delivery not in ("SENT", "UNCERTAIN", "STALE", "REVIEWED"):
        raise ValueError("Invalid delivery acknowledgement.")
    folder = job_folder(ident)
    with (folder / "worker.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        record = json.loads((folder / "record.json").read_text())
        if record["delivery"] == "PENDING":
            record["delivery"] = delivery
            atomic_job(folder / "record.json", record)



def public_job(record):
    return {key: record.get(key) for key in ("id", "agentId", "sessionId", "caption", "machine", "outputParent",
        "outcome", "exitCode", "created", "finished", "event", "error", "delivery")}


def process_identity(pid):
    stat = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
    return Path("/proc/sys/kernel/random/boot_id").read_text().strip() + ":" + stat[19]


def cancel_job(ident):
    folder = job_folder(ident)
    record = json.loads((folder / "record.json").read_text())
    pid = record.get("pid")
    if record["outcome"] != "RUNNING" or not isinstance(pid, int) or record.get("processIdentity") != process_identity(pid) or os.getpgid(pid) != pid:
        raise ValueError("Owned process identity changed or job is not running; no process stopped.")
    # This terminates only the owned local command/transport. A remote workload may
    # remain live; its validator, never this signal, determines terminal completion.
    os.killpg(pid, signal.SIGTERM)
    print("Cancellation requested for the owned local process group. Remote completion remains unverified.")

def retry_completion(ident):
    folder = job_folder(ident)
    with (folder / "worker.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        record = json.loads((folder / "record.json").read_text())
        if record.get("event") is not None:
            print(json.dumps(public_job(record)))
            return
        complete_job(folder, record)  # retry validation/notification only, never the workload
        print(json.dumps(public_job(record)))

def main():
    if sys.argv[1:2] == ["--job-worker"] and len(sys.argv) == 3:
        run_job_worker(sys.argv[2])
        return
    if sys.argv[1:] == ["--pending-jobs"]:
        print(json.dumps(pending_jobs()))
        return
    if sys.argv[1:2] == ["--ack-job"] and len(sys.argv) == 4:
        ack_job(sys.argv[2], sys.argv[3])
        return
    # Internal transfer protocol for the Manager; it never asks the tool to read an arbitrary source path.
    if sys.argv[1:] == ["--pending-images"]:
        print(json.dumps(pending()))
        return
    if sys.argv[1:2] == ["--ack-image"] and len(sys.argv) == 3:
        acknowledge(sys.argv[2])
        return
    if sys.argv[1:] == ["--pending-files"]:
        print(json.dumps(pending_files()))
        return
    if sys.argv[1:2] == ["--ack-file"] and len(sys.argv) == 3:
        acknowledge(sys.argv[2], FILES)
        return
    parser = argparse.ArgumentParser(prog="dam-tools", description=__doc__, epilog="Prefer your harness built-in background tasks for ordinary builds, tests and verification. "
                                     "Use run-job only for restart survival or durable ownership with validated completion evidence.")
    commands = parser.add_subparsers(dest="command", required=True, metavar="COMMAND")

    share = commands.add_parser("share-image", help="Show an image to the user in this agent's conversation",
                                description="Show an image (a screenshot, chart or diagram) to the user. "
                                            "It is copied, so the file can be deleted afterwards.")
    share.add_argument("path", help="Local PNG, JPEG, GIF or WebP, up to 10 MiB")
    share.add_argument("--caption", default="", help="What the image shows")
    share.add_argument("--agent", default=os.environ.get("DAM_AGENT_ID", ""), help=argparse.SUPPRESS)
    share.add_argument("--session", default=os.environ.get("DAM_SESSION_ID", ""), help=argparse.SUPPRESS)
    share.set_defaults(run=lambda args: share_image(args, share))

    file = commands.add_parser("share-file", help="Give the user a file to download, such as a built APK or installer",
                               description="Give the user a file to download from this agent's conversation, such as an "
                                           "APK, installer, archive or report. It is copied, so the file can be deleted "
                                           "afterwards. Archive a folder first.")
    file.add_argument("path", help="Local file, up to 1 GiB")
    file.add_argument("--caption", default="", help="What the file is")
    file.add_argument("--agent", default=os.environ.get("DAM_AGENT_ID", ""), help=argparse.SUPPRESS)
    file.add_argument("--session", default=os.environ.get("DAM_SESSION_ID", ""), help=argparse.SUPPRESS)
    file.set_defaults(run=lambda args: share_file(args, file))

    job = commands.add_parser("run-job", help="Durable fallback for work that must survive harness restarts",
                              description="Prefer your harness built-in background tasks for ordinary work. "
                                          "Use this fallback for restart survival or durable ownership with validated completion evidence. "
                                          "The Manager attempts to notify the original conversation when it is idle; "
                                          "inspect job-status if notification has not arrived.")
    job.add_argument("--prepare-json", required=True, help="JSON argv array which allocates and checks an empty output parent on the workload machine")
    job.add_argument("--command-json", required=True, help="JSON argv array; no shell or workload relaunch")
    job.add_argument("--completion-json", required=True, help="JSON argv array for the workload's read-only terminal evidence validator")
    job.add_argument("--output-parent", required=True, help="Empty unique output parent on the workload machine, allocated before launch")
    job.add_argument("--machine", required=True, help="Non-secret workload machine label")
    job.add_argument("--caption", default="Background verification")
    job.add_argument("--agent", default=os.environ.get("DAM_AGENT_ID", ""), help=argparse.SUPPRESS)
    job.add_argument("--session", default=os.environ.get("DAM_SESSION_ID", ""), help=argparse.SUPPRESS)
    job.set_defaults(run=lambda args: launch_job(args, job))
    status = commands.add_parser("job-status", help="Inspect the exact durable job without relaunching")
    status.add_argument("id")
    status.set_defaults(run=lambda args: print(json.dumps(public_job(json.loads((job_folder(args.id) / "record.json").read_text())))))
    reviewed = commands.add_parser("job-reviewed", help="Acknowledge that this result was already reviewed in the current turn; suppress later automatic delivery")
    reviewed.add_argument("id")
    reviewed.set_defaults(run=lambda args: ack_job(args.id, "REVIEWED"))
    cancel = commands.add_parser("job-cancel", help="Request cancellation of the owned local command; remote exit still requires validation")
    cancel.add_argument("id")
    cancel.set_defaults(run=lambda args: cancel_job(args.id))
    retry = commands.add_parser("job-completion", help="Retry only read-only completion validation after transport recovery")
    retry.add_argument("id")
    retry.set_defaults(run=lambda args: retry_completion(args.id))
    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError) as ex:
        print(f"dam-tools: {ex}", file=sys.stderr)
        sys.exit(1)
