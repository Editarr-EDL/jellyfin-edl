"""Synthetic media library laid out from the MU-01 export-layout-v1 selection cases."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
from pathlib import Path

PINNED_MANIFEST_SHA256 = "c3063858e3ecf2f8982c66a1140f52ab7d806e737ff5c7854b413cdecccf987e"
LIBRARY_DIR = "Movies"


def load_manifest(fixture_dir: Path) -> dict:
    """Read the imported manifest and refuse it unless its bytes match the pinned contract."""
    raw = (fixture_dir / "manifest.json").read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    if digest != PINNED_MANIFEST_SHA256:
        raise ValueError(f"export-layout-v1 manifest sha256 {digest} does not match the pinned contract")
    return json.loads(raw)


def edl_cases(manifest: dict) -> list[dict]:
    return [case for case in manifest["selection"] if case["format"] == "edl"]


def legacy_expectation(case: dict, manifest: dict) -> dict:
    """What the current plugin returns: only the adjacent <stem>.edl is read, then validated."""
    legacy_path = os.path.splitext(case["media"])[0] + ".edl"
    entry = next((e for e in case["entries"] if e["path"] == legacy_path), None)
    if entry is None or entry.get("directory"):
        return {"status": 404, "payload": None}
    payload = manifest["payloads"][entry["payload"]]
    return {"status": 200 if payload["valid"] else 422, "payload": entry["payload"] if payload["valid"] else None}


def media_path(case: dict) -> str:
    """Container path of the case's media file under the /media mount."""
    return f"/media/{LIBRARY_DIR}/{case['name']}/{case['media']}"


def build(manifest: dict, fixture_dir: Path, media_root: Path, video: Path) -> list[str]:
    """Write one movie folder per EDL selection case and return the written relative paths."""
    written = []
    for case in edl_cases(manifest):
        case_dir = media_root / LIBRARY_DIR / case["name"]
        case_dir.mkdir(parents=True)
        shutil.copyfile(video, case_dir / case["media"])
        written.append(f"{LIBRARY_DIR}/{case['name']}/{case['media']}")
        for entry in case["entries"]:
            target = case_dir / entry["path"]
            if entry.get("directory"):
                target.mkdir(parents=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes((fixture_dir / manifest["payloads"][entry["payload"]]["file"]).read_bytes())
                if entry.get("unreadable"):
                    target.chmod(0)
            written.append(f"{LIBRARY_DIR}/{case['name']}/{entry['path']}")
    return written
