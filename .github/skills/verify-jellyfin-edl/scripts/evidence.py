"""Evidence files and secret redaction for verification runs."""

from __future__ import annotations

import json
import os
from pathlib import Path

REDACTED = b"[REDACTED]"


def write_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n")


def read_json(path: Path) -> dict:
    return json.loads(path.read_text())


def private_file(path: Path, value: object) -> None:
    """Write JSON readable only by the current user."""
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, "w") as handle:
        json.dump(value, handle)


def redact(evidence_dir: Path, secrets: list) -> dict:
    """Replace every secret in every evidence file, then prove none remains.

    Returns a report of scanned files and replacement counts. Raises if a
    secret survives, so a leaking run cannot report success.
    """
    needles = [secret.encode() for secret in secrets if secret]
    report = {"files": 0, "replacements": 0, "secrets": len(needles)}
    for path in sorted(p for p in evidence_dir.rglob("*") if p.is_file()):
        data = path.read_bytes()
        report["files"] += 1
        for needle in needles:
            count = data.count(needle)
            if count:
                data = data.replace(needle, REDACTED)
                report["replacements"] += count
        path.write_bytes(data)
    for path in evidence_dir.rglob("*"):
        if path.is_file() and any(needle in path.read_bytes() for needle in needles):
            raise RuntimeError(f"secret survived redaction in {path.name}")
    return report


def scrub(value: object, secrets: list) -> object:
    """Return a copy of JSON-shaped value with every secret in its strings replaced."""
    if isinstance(value, dict):
        return {key: scrub(item, secrets) for key, item in value.items()}
    if isinstance(value, list):
        return [scrub(item, secrets) for item in value]
    if isinstance(value, str):
        for secret in secrets:
            if secret:
                value = value.replace(secret, REDACTED.decode())
    return value
