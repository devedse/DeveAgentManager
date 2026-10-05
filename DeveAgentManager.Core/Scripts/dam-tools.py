#!/usr/bin/env python3
"""Publish an image to this agent's conversation in DeveAgentManager."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import sys
import uuid

ROOT = Path.home() / ".deveagentmanager/shared-images"
MAX_BYTES = 10 * 1024 * 1024


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


def acknowledge(ident):
    folder = ROOT / uuid.UUID(ident).hex
    if folder.is_symlink():
        raise ValueError("Invalid image folder.")
    if folder.exists():
        shutil.rmtree(folder)


def main():
    # Internal transfer protocol; the Manager never asks the helper to read an arbitrary source path.
    if sys.argv[1:] == ["--pending"]:
        print(json.dumps(pending()))
        return
    if sys.argv[1:2] == ["--ack"] and len(sys.argv) == 3:
        acknowledge(sys.argv[2])
        return
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", help="Local PNG, JPEG, GIF or WebP")
    parser.add_argument("--caption", default="", help="Description shown with the image")
    parser.add_argument("--agent", default=os.environ.get("DAM_AGENT_ID", ""))
    parser.add_argument("--session", default=os.environ.get("DAM_SESSION_ID", ""))
    args = parser.parse_args()
    if not args.agent or not args.session:
        parser.error("Run from an agent, or supply --agent and --session UUIDs.")
    record = publish(args.path, args.agent, args.session, args.caption)
    print("Image shared: " + (record["caption"] or record["fileName"]))
    print("Open Shared images in the agent toolbar or conversation to view it.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError) as ex:
        print(f"Share image: {ex}", file=sys.stderr)
        sys.exit(1)
