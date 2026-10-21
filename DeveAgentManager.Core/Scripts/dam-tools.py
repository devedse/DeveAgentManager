#!/usr/bin/env python3
"""DeveAgentManager tools for agents running on this runner."""
import argparse
import base64
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
ARTIFACT_REQUESTS = Path.home() / ".deveagentmanager/artifact-requests"
ARTIFACT_CACHE = Path.home() / ".deveagentmanager/artifact-cache"


def artifact_folder(ident):
    ident = uuid.UUID(ident).hex
    if ARTIFACT_REQUESTS.is_symlink():
        raise ValueError("Artifact request directory cannot be a symlink.")
    folder = ARTIFACT_REQUESTS / ident
    if folder.is_symlink() or not folder.is_dir():
        raise ValueError("Artifact request is unavailable.")
    return folder


def pending_artifacts():
    records = []
    if not ARTIFACT_REQUESTS.exists() or ARTIFACT_REQUESTS.is_symlink():
        return records
    for folder in sorted(ARTIFACT_REQUESTS.iterdir()):
        try:
            if folder.is_symlink() or uuid.UUID(folder.name).hex != folder.name:
                continue
            if time.time() - folder.stat().st_mtime > STALE_STAGING_SECONDS:
                shutil.rmtree(folder)
                continue
            metadata = folder / "request.json"
            if (folder / "response.json").exists() or metadata.is_symlink() or metadata.stat().st_size > 16384:
                continue
            record = json.loads(metadata.read_text())
            if uuid.UUID(record["id"]).hex != folder.name:
                continue
            uuid.UUID(record["agentId"])
            uuid.UUID(record["sessionId"])
            if (record.get("operation") not in ("list", "fetch")
                    or type(record.get("offset", 0)) is not int or record.get("offset", 0) < 0
                    or type(record.get("limit", 100)) is not int or not 1 <= record.get("limit", 100) <= 200
                    or type(record.get("all", False)) is not bool
                    or not isinstance(record.get("token"), str) or not 0 < len(record["token"]) <= 4096
                    or (record.get("sourceAgent") is not None and (not isinstance(record["sourceAgent"], str) or len(record["sourceAgent"]) > 128))
                    or record.get("kind") not in (None, "file", "image")):
                continue
            if record["operation"] == "fetch":
                uuid.UUID(record["artifactId"])
            if len(records) < 20:
                records.append({key: value for key, value in record.items() if key in (
                    "id", "agentId", "sessionId", "operation", "sourceAgent", "artifactId", "kind", "offset", "limit", "all", "token")})
        except (ValueError, OSError, KeyError, TypeError, AttributeError):
            continue
    return records


def prepare_artifact_response(ident):
    folder = artifact_folder(ident)
    if (folder / "request.json").is_symlink():
        raise ValueError("Invalid artifact request metadata.")
    for name in ("content", "response.json"):
        path = folder / name
        if path.exists() or path.is_symlink():
            if path.is_symlink() or not path.is_file():
                raise ValueError("Invalid artifact response path.")
    folder.chmod(0o700)


def receive_artifact(folder, response, agent, output=None):
    size, expected = response.get("bytes"), response.get("sha256")
    if type(size) is not int or not 0 < size <= MAX_FILE_BYTES or not is_sha256(expected):
        raise ValueError("Invalid artifact size or checksum.")
    item = response["artifact"]
    name = Path(item["fileName"].replace("\\", "/")).name
    if name in ("", ".", ".."):
        name = "artifact"
    target = Path(output).expanduser().absolute() if output else ARTIFACT_CACHE / uuid.UUID(agent).hex / uuid.UUID(item["id"]).hex / name
    target.parent.mkdir(parents=True, mode=0o700, exist_ok=True)
    temporary = target.with_name(".pending-" + uuid.uuid4().hex)
    source = folder / "content"
    try:
        digest, count = hashlib.sha256(), 0
        with os.fdopen(os.open(source, os.O_RDONLY | os.O_NOFOLLOW), "rb") as data:
            if not stat.S_ISREG(os.fstat(data.fileno()).st_mode):
                raise ValueError("Invalid artifact content.")
            with os.fdopen(os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "wb") as dest:
                while chunk := data.read(1024 * 1024):
                    count += len(chunk)
                    if count > size:
                        raise ValueError("Artifact exceeded its expected size.")
                    digest.update(chunk)
                    dest.write(chunk)
                dest.flush()
                os.fsync(dest.fileno())
        if count != size or digest.hexdigest() != expected:
            raise ValueError("Artifact download did not match its checksum.")
        # Never overwrite user work, even if a destination appeared while we were transferring.
        try:
            os.link(temporary, target)
        except FileExistsError:
            if output or target.is_symlink():
                raise
            with os.fdopen(os.open(target, os.O_RDONLY | os.O_NOFOLLOW), "rb") as cached:
                cached_hash = hashlib.sha256()
                while chunk := cached.read(1024 * 1024):
                    cached_hash.update(chunk)
                if os.fstat(cached.fileno()).st_size != size or cached_hash.hexdigest() != expected:
                    raise ValueError("Cached artifact changed. Choose a new --output path.")
        return target
    finally:
        temporary.unlink(missing_ok=True)


def request_artifacts(args, parser, operation):
    if args.timeout <= 0 or not args.timeout < float("inf") or getattr(args, "offset", 0) < 0:
        parser.error("Use a positive finite timeout and a non-negative offset.")
    agent, session = os.environ.get("DAM_AGENT_ID", ""), os.environ.get("DAM_SESSION_ID", "")
    if not agent or not session:
        parser.error("Run from a DAM agent so its read permissions can be checked.")
    record = dict(id=str(uuid.uuid4()), agentId=str(uuid.UUID(agent)), sessionId=str(uuid.UUID(session)),
                  operation=operation, sourceAgent=args.agent, offset=getattr(args, "offset", 0),
                  limit=getattr(args, "limit", 100), all=getattr(args, "all", False))
    token_path = Path.home() / ".deveagentmanager/artifact-access" / (uuid.UUID(agent).hex + ".token")
    if not token_path.is_file() or token_path.is_symlink() or token_path.stat().st_size > 4096:
        raise ValueError("Artifact access is not installed yet. Wait for runner tools to update, or restart this agent.")
    record["token"] = token_path.read_text().strip()
    if operation == "fetch":
        record.update(artifactId=str(uuid.UUID(args.id)), kind=args.kind)
    if ARTIFACT_REQUESTS.is_symlink():
        raise ValueError("Artifact request directory cannot be a symlink.")
    ARTIFACT_REQUESTS.mkdir(parents=True, mode=0o700, exist_ok=True)
    folder = ARTIFACT_REQUESTS / uuid.UUID(record["id"]).hex
    folder.mkdir(mode=0o700)
    atomic_job(folder / "request.json", record)
    deadline = time.monotonic() + args.timeout
    response_path = folder / "response.json"
    while time.monotonic() < deadline:
        if response_path.is_file() and not response_path.is_symlink():
            if response_path.stat().st_size > 2 * 1024 * 1024:
                raise ValueError("Artifact response exceeded its size limit.")
            response = json.loads(response_path.read_text())
            try:
                if "error" in response:
                    raise ValueError(response["error"])
                if operation == "list":
                    print(json.dumps(response))
                else:
                    print(receive_artifact(folder, response, agent, args.output))
            finally:
                shutil.rmtree(folder)
            return
        time.sleep(0.2)
    raise ValueError("Artifact request timed out; the Manager or runner may be offline. It only requests a read; no agent work was launched.")


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
    print("The user sees it under Shared images in every session of this agent.")


def share_file(args, parser):
    if not args.agent or not args.session:
        parser.error("Run from an agent, or supply --agent and --session UUIDs.")
    record = publish_file(args.path, args.agent, args.session, args.caption)
    print(f"File shared: {record['fileName']} ({size_text(record['bytes'])}, SHA-256 {record['sha256']})")
    print("The user can download it under Shared files in every session of this agent once the Manager has copied it, "
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

TASKS = Path.home() / ".deveagentmanager/tasks"
MAX_TASKS = 50
MAX_TASK_NAME = 120
MAX_TASK_DESCRIPTION = 1000
TASKS_MARKER = "__DAM_TASKS__"


def now_iso():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def task_text(text, limit):
    # Keeps line breaks in descriptions; other control characters become spaces.
    lines = ["".join(" " if ord(c) < 32 else c for c in line).strip() for line in str(text).splitlines()]
    return "\n".join(line for line in lines if line)[:limit]


def duration_seconds(text):
    """'90s', '5m', '1h30m' or '2h'; a plain number is minutes."""
    value = str(text).strip().lower().replace(" ", "")
    if value.replace(".", "", 1).isdigit():
        return int(float(value) * 60)
    total, number = 0, ""
    for c in value:
        if c.isdigit() or c == ".":
            number += c
        elif c in "hms" and number:
            total += float(number) * {"h": 3600, "m": 60, "s": 1}[c]
            number = ""
        else:
            raise ValueError(f"Use a duration like 90s, 5m or 1h30m, not '{text}'.")
    if number or not value:
        raise ValueError(f"Use a duration like 90s, 5m or 1h30m, not '{text}'.")
    return int(total)


def tasks_path(agent):
    if TASKS.is_symlink():
        raise ValueError("Task directory cannot be a symlink.")
    return TASKS / (uuid.UUID(agent).hex + ".json")


def read_tasks(agent, path=None):
    path = path or tasks_path(agent)
    if not path.exists():
        return dict(revision=0, updatedAt=None, tasks=[])
    if path.is_symlink() or path.stat().st_size > 1024 * 1024:
        raise ValueError("Invalid task list.")
    state = json.loads(path.read_text())
    if not isinstance(state, dict) or not isinstance(state.get("tasks"), list) or type(state.get("revision")) is not int:
        raise ValueError("Invalid task list.")
    return state


class TaskLock:
    """One change at a time per agent, so concurrent updates never lose each other."""
    def __init__(self, agent):
        TASKS.mkdir(parents=True, mode=0o700, exist_ok=True)
        self.path = TASKS / (uuid.UUID(agent).hex + ".lock")

    def __enter__(self):
        self.stream = open(self.path, "a")
        fcntl.flock(self.stream, fcntl.LOCK_EX)
        return self

    def __exit__(self, *_):
        fcntl.flock(self.stream, fcntl.LOCK_UN)
        self.stream.close()


def set_task_fields(task, tasks, name=None, percent=None, description=None, left=None, taken=None):
    stamp = now_iso()
    if name is not None:
        name = task_text(name, MAX_TASK_NAME).replace("\n", " ")
        if not name:
            raise ValueError("A task needs a name.")
        task["name"] = name
    if description is not None:
        task["description"] = task_text(description, MAX_TASK_DESCRIPTION)
    if percent is not None:
        percent = int(round(float(percent)))
        if not 0 <= percent <= 100:
            raise ValueError("Use a percentage from 0 to 100.")
        task["percent"] = percent
        if percent > 0 and not task.get("startedAt"):
            # Agents often only report a task once it is done: it began when it was added or the previous one finished.
            finished = [other["finishedAt"] for other in tasks if other is not task and other.get("finishedAt")]
            task["startedAt"] = min(stamp, max([task.get("addedAt") or stamp] + finished))
        task["finishedAt"] = (task.get("finishedAt") or stamp) if percent == 100 else None
        if percent == 100:
            task["leftSeconds"], task["leftAt"] = None, None
    if left is not None:
        task["leftSeconds"], task["leftAt"] = duration_seconds(left), stamp
    if taken is not None:
        task["takenSeconds"] = duration_seconds(taken)
    task["updatedAt"] = stamp
    return task


def new_task(ident, item, tasks):
    task = dict(id=ident, name="", percent=0, description="", addedAt=now_iso(), startedAt=None, finishedAt=None,
                takenSeconds=None, leftSeconds=None, leftAt=None, updatedAt=None)
    return set_task_fields(task, tasks, name=item.get("name"), percent=item.get("percent", 0),
                           description=item.get("description", ""), left=item.get("left"), taken=item.get("taken"))


def change_tasks(agent, change):
    """Applies change(tasks) to the agent's list under its lock and saves it with a new revision."""
    with TaskLock(agent):
        state = read_tasks(agent)
        tasks = change(state["tasks"])
        if len(tasks) > MAX_TASKS:
            raise ValueError(f"Track at most {MAX_TASKS} tasks; group smaller steps.")
        state = dict(revision=state["revision"] + 1, updatedAt=now_iso(), tasks=tasks)
        atomic_job(tasks_path(agent), state)
        return state


def find_task(tasks, ident):
    for task in tasks:
        if task["id"] == int(ident):
            return task
    raise ValueError(f"There is no task {ident}. Run 'dam-tools tasks' to see them.")


def task_items(text):
    items = json.loads(text)
    if not isinstance(items, list) or not all(isinstance(item, dict) for item in items):
        raise ValueError("Pass a JSON array of tasks, e.g. [{\"name\": \"Build\", \"percent\": 0}].")
    return items


def short_duration(seconds):
    seconds = int(seconds)
    if seconds < 60:
        return f"{seconds}s"
    hours, minutes = divmod(seconds // 60, 60)
    return f"{hours}h{minutes:02d}m" if hours else f"{minutes}m"


def describe_tasks(state):
    tasks = state["tasks"]
    if not tasks:
        return "No tasks. Add them with 'dam-tools tasks set \"First\" \"Second\"' or 'dam-tools tasks add \"Name\"'."
    done = sum(1 for task in tasks if task["percent"] == 100)
    overall = round(sum(task["percent"] for task in tasks) / len(tasks))
    lines = [f"Tasks: {done} of {len(tasks)} done, {overall}% (revision {state['revision']})"]
    now = datetime.now(timezone.utc)
    for task in tasks:
        detail = []
        taken = " taken" if task["percent"] == 100 else " so far"
        if task.get("takenSeconds") is not None:
            detail.append(short_duration(task["takenSeconds"]) + taken)
        elif task.get("startedAt"):
            end = datetime.fromisoformat(task["finishedAt"].replace("Z", "+00:00")) if task.get("finishedAt") else now
            detail.append(short_duration((end - datetime.fromisoformat(task["startedAt"].replace("Z", "+00:00"))).total_seconds()) + taken)
        if task["percent"] < 100 and task.get("leftSeconds") is not None:
            detail.append("~" + short_duration(task["leftSeconds"]) + " left")
        lines.append(f"{task['id']:>3}. [{task['percent']:>3}%] {task['name']}" + (f"  ({', '.join(detail)})" if detail else ""))
        for line in task.get("description", "").splitlines():
            lines.append("       " + line)
    return "\n".join(lines)


def run_tasks(args, parser):
    agent = os.environ.get("DAM_AGENT_ID", "")
    if not agent:
        parser.error("Run from a DAM agent: its tasks show in the Manager next to its terminal.")
    action = args.tasks_action or "show"
    if action == "show":
        state = read_tasks(agent)
        print(json.dumps(state, indent=2) if args.json else describe_tasks(state))
        return
    if action == "set":
        if args.json is not None:
            items = task_items(sys.stdin.read() if args.json == "-" else args.json)
        else:
            items = [dict(name=name) for name in args.names]
        state = change_tasks(agent, lambda _: [new_task(index + 1, item, []) for index, item in enumerate(items)])
    elif action == "add":
        def add(tasks):
            tasks.append(new_task(max((task["id"] for task in tasks), default=0) + 1, dict(
                name=args.name, percent=args.percent or 0, description=args.description or "", left=args.left, taken=args.taken), tasks))
            return tasks
        state = change_tasks(agent, add)
    elif action in ("update", "done"):
        def update(tasks):
            set_task_fields(find_task(tasks, args.id), tasks, name=getattr(args, "name", None),
                            percent=100 if action == "done" else args.percent, description=args.description,
                            left=getattr(args, "left", None), taken=args.taken)
            return tasks
        state = change_tasks(agent, update)
    elif action == "apply":
        def apply(tasks):
            for item in task_items(sys.stdin.read() if args.json == "-" else args.json):
                if item.get("id") is None:
                    tasks.append(new_task(max((task["id"] for task in tasks), default=0) + 1, item, tasks))
                else:
                    set_task_fields(find_task(tasks, item["id"]), tasks, name=item.get("name"), percent=item.get("percent"),
                                    description=item.get("description"), left=item.get("left"), taken=item.get("taken"))
            return tasks
        state = change_tasks(agent, apply)
    elif action == "remove":
        state = change_tasks(agent, lambda tasks: [task for task in tasks if task["id"] != find_task(tasks, args.id)["id"]])
    else:
        state = change_tasks(agent, lambda _: [])
    print(describe_tasks(state))


def changed_tasks(known):
    """For the Manager: every listed agent whose task revision differs from what it saw, as one marked line each."""
    for item in known:
        agent, _, revision = item.partition("=")
        try:
            state = read_tasks(agent)
            if state["revision"] != int(revision):
                print_tasks_line(agent, state)
        except (ValueError, OSError):
            continue


def print_tasks_line(agent, state):
    print(f"{TASKS_MARKER} {uuid.UUID(agent).hex} " + base64.b64encode(json.dumps(state, separators=(",", ":")).encode()).decode())


def clear_tasks(agent):
    """For the Manager: the user cleared the list. A new revision, so the agent sees it on its next read."""
    print_tasks_line(agent, change_tasks(agent, lambda _: []))


def restore_tasks(agent):
    """For the Manager: this Runner has no list for the agent (it moved here, or the Runner was rebuilt), so the
    Manager uploaded its copy next to it. It goes back with the same revision, unless the agent started a new list."""
    upload = TASKS / (uuid.UUID(agent).hex + ".restore")
    try:
        with TaskLock(agent):
            state = read_tasks(agent)
            if state["revision"] == 0:
                saved = read_tasks(agent, upload)
                state = dict(revision=saved["revision"], updatedAt=saved.get("updatedAt"), tasks=saved["tasks"][:MAX_TASKS])
                atomic_job(tasks_path(agent), state)
    finally:
        upload.unlink(missing_ok=True)
    print_tasks_line(agent, state)


def main():
    if sys.argv[1:] == ["--pending-transfers"]:
        print(json.dumps(dict(files=pending_files(), requests=pending_artifacts())))
        return
    if sys.argv[1:2] == ["--prepare-artifact-response"] and len(sys.argv) == 3:
        prepare_artifact_response(sys.argv[2])
        return
    if sys.argv[1:2] == ["--job-worker"] and len(sys.argv) == 3:
        run_job_worker(sys.argv[2])
        return
    if sys.argv[1:2] == ["--changed-tasks"]:
        changed_tasks(sys.argv[2:])
        return
    if sys.argv[1:2] == ["--clear-tasks"] and len(sys.argv) == 3:
        clear_tasks(sys.argv[2])
        return
    if sys.argv[1:2] == ["--restore-tasks"] and len(sys.argv) == 3:
        restore_tasks(sys.argv[2])
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
    parser = argparse.ArgumentParser(prog="dam-tools", description=__doc__, epilog="Use your harness built-in background tasks whenever it supports them. "
                                     "Use run-job only when the harness has no built-in background-job support. Long runtimes, restart survival and validation needs are not exceptions.")
    commands = parser.add_subparsers(dest="command", required=True, metavar="COMMAND")

    listing = commands.add_parser("list-artifacts", help="List this agent's shared files and images across all its sessions")
    listing.add_argument("--agent", help="Source agent name or id (default: this agent)")
    listing.add_argument("--all", action="store_true", help="Discover all agents and artifacts your owner may read")
    listing.add_argument("--offset", type=int, default=0, help="Skip this many artifacts")
    listing.add_argument("--limit", type=int, choices=range(1, 201), metavar="1-200", default=100)
    listing.add_argument("--timeout", type=float, default=300, help="Seconds to wait for the Manager")
    listing.set_defaults(run=lambda args: request_artifacts(args, listing, "list"))
    fetch = commands.add_parser("fetch-artifact", help="Download a shared file or image, including from another runner")
    fetch.add_argument("id", help="Artifact id from list-artifacts")
    fetch.add_argument("--agent", help="Source agent name or id (default: this agent)")
    fetch.add_argument("--kind", choices=("file", "image"), help="Only needed when an id is ambiguous")
    fetch.add_argument("--output", help="New local file path; refuses to overwrite an existing file")
    fetch.add_argument("--timeout", type=float, default=300, help="Seconds to wait for the Manager")
    fetch.set_defaults(run=lambda args: request_artifacts(args, fetch, "fetch"))

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

    tasks = commands.add_parser("tasks", help="Show the user your progress: a list of tasks with %% done and time left",
                                description="Keep a short task list for the user, shown next to this agent's terminal. "
                                            "Set it when you start multi-step work, and update a task each time you finish "
                                            "it, with a description of what you did. Adjust % done and time-left estimates "
                                            "as you go. Without arguments it prints the current list; the user may clear it.")
    tasks.add_argument("--json", action="store_true", help="Print the list as JSON")
    tasks.set_defaults(run=lambda args: run_tasks(args, tasks), tasks_action=None)
    actions = tasks.add_subparsers(dest="tasks_action", metavar="ACTION")
    show = actions.add_parser("show", help="Print the current list (also without an action)")
    show.add_argument("--json", action="store_true", help="Print the list as JSON")
    replace = actions.add_parser("set", help="Replace the whole list: names, or a JSON array with --json",
                                 description="Replace the list. Names start at 0%; a JSON array can set everything at once: "
                                             "[{\"name\": \"Build\", \"percent\": 40, \"description\": \"...\", \"left\": \"10m\"}]. "
                                             "Use --json - to read it from standard input.")
    replace.add_argument("names", nargs="*", help="Task names, in order")
    replace.add_argument("--json", help="JSON array of tasks (name, percent, description, left, taken), or - for stdin")
    add = actions.add_parser("add", help="Add a task at the end")
    add.add_argument("name")
    update = actions.add_parser("update", help="Change one task: its %%, name, description or time estimates")
    update.add_argument("id", type=int, help="Task number from 'dam-tools tasks'")
    update.add_argument("--name")
    done = actions.add_parser("done", help="Mark a task 100%% done, ideally with what you did")
    done.add_argument("id", type=int, help="Task number from 'dam-tools tasks'")
    for action in (add, update, done):
        action.add_argument("--description", help="What the task is, or what was done")
        action.add_argument("--taken", help="Time taken, e.g. 15m (otherwise measured from its first progress)")
    for action in (add, update):
        action.add_argument("--percent", type=float, help="0 to 100")
        action.add_argument("--left", help="Estimated time left, e.g. 10m or 1h30m")
    many = actions.add_parser("apply", help="Change several tasks in one go from a JSON array",
                              description="Each item with an id updates that task (only the fields given); one without an id "
                                          "is added. E.g. [{\"id\": 2, \"percent\": 100, \"description\": \"Done: ...\"}, {\"name\": \"Next\"}].")
    many.add_argument("--json", required=True, help="JSON array, or - for stdin")
    remove = actions.add_parser("remove", help="Remove a task")
    remove.add_argument("id", type=int)
    actions.add_parser("clear", help="Remove all tasks")
    for action in (show, replace, add, update, done, many, remove):
        action.set_defaults(run=lambda args: run_tasks(args, tasks))

    job = commands.add_parser("run-job", help="Fallback only for harnesses without built-in background jobs",
                              description="Use your harness built-in background tasks whenever it supports them. "
                                          "Use this fallback only when the harness has no built-in background-job support. Long runtimes, restart survival and validation needs are not exceptions. "
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
