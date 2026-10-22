"""Synthetic media library laid out from the MU-01 export-layout-v1 selection cases."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
from pathlib import Path

PINNED_MANIFEST_SHA256 = "c3063858e3ecf2f8982c66a1140f52ab7d806e737ff5c7854b413cdecccf987e"
LIBRARY_DIR = "Movies"
RESTRICTED_DIR = "Restricted"
VERSIONS_CASE = "version-1080p-has-personal"
VERSION_MEDIA = ("Movie - 1080p.mkv", "Movie - 2160p.mkv")
RESTRICTED_CASE = "edl-each-user-gets-own-file"


def load_manifest(fixture_dir: Path) -> dict:
    """Read the imported manifest and refuse it unless its bytes match the pinned contract."""
    raw = (fixture_dir / "manifest.json").read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    if digest != PINNED_MANIFEST_SHA256:
        raise ValueError(f"export-layout-v1 manifest sha256 {digest} does not match the pinned contract")
    return json.loads(raw)


def edl_cases(manifest: dict) -> list[dict]:
    return [case for case in manifest["selection"] if case["format"] == "edl"]


def localize(case: dict, export_ids: dict) -> dict:
    """Return the case with each fixture Jellyfin export ID in its entry paths replaced by a live user's ID."""
    def rename(path: str) -> str:
        for fixture_id, live_id in export_ids.items():
            path = path.replace(fixture_id, live_id)
        return path

    return dict(case, entries=[dict(entry, path=rename(entry["path"])) for entry in case["entries"]])


def contract_expectation(case: dict, export_id: str | None, manifest: dict, server_reads_mode_000: bool = False) -> dict:
    """What editarr-export-layout-1 selects for one user: personal, then default only when personal is absent.

    export_id None is a server API key, which gets only the default whatever
    personal files exist. Whether a mode 000 file is unreadable depends on the
    server's user and filesystem, so with server_reads_mode_000 an unreadable
    entry is selected like a readable one.
    """
    stem = os.path.splitext(case["media"])[0]
    entries = {entry["path"]: entry for entry in case["entries"]}
    candidates = [("default", f"edl/{stem}.edl")]
    if export_id is not None:
        candidates.insert(0, ("personal", f"edl/{export_id}/{stem}.user-{export_id}.edl"))
    for scope, path in candidates:
        entry = entries.get(path)
        if entry is None:
            continue
        if entry.get("directory"):
            return {"outcome": f"{scope}_invalid", "status": 422, "payload": None}
        if entry.get("unreadable") and not server_reads_mode_000:
            return {"outcome": f"{scope}_unreadable", "status": 500, "payload": None}
        if manifest["payloads"][entry["payload"]]["valid"]:
            return {"outcome": scope, "status": 200, "payload": entry["payload"]}
        return {"outcome": f"{scope}_invalid", "status": 422, "payload": None}
    return {"outcome": "none", "status": 404, "payload": None}


def media_path(case: dict, library_dir: str = LIBRARY_DIR) -> str:
    """Container path of the case's media file under the /media mount."""
    return f"/media/{library_dir}/{case['name']}/{case['media']}"


def version_paths() -> list[str]:
    """Container paths of the two-version movie, which Jellyfin groups into one item."""
    return [f"/media/{LIBRARY_DIR}/Movie/{name}" for name in VERSION_MEDIA]


def unreadable_paths(manifest: dict, export_ids: dict) -> list[str]:
    """Container paths of the mode 000 entries in the main library."""
    return [f"/media/{LIBRARY_DIR}/{case['name']}/{entry['path']}"
            for case in (localize(c, export_ids) for c in edl_cases(manifest))
            for entry in case["entries"] if entry.get("unreadable")]


def write_case(case: dict, fixture_dir: Path, manifest: dict, case_dir: Path, media: list[str], video: Path) -> list[str]:
    """Write one movie folder with the given media files and the case's entries; return the written paths."""
    case_dir.mkdir(parents=True)
    written = []
    for name in media:
        shutil.copyfile(video, case_dir / name)
        written.append(name)
    for entry in case["entries"]:
        target = case_dir / entry["path"]
        if entry.get("directory"):
            target.mkdir(parents=True)
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((fixture_dir / manifest["payloads"][entry["payload"]]["file"]).read_bytes())
            if entry.get("unreadable"):
                target.chmod(0)
        written.append(entry["path"])
    return written


def build(manifest: dict, fixture_dir: Path, media_root: Path, video: Path, export_ids: dict) -> list[str]:
    """Write the synthetic library for live users and return the written paths relative to media_root.

    Movies/<case>/ holds each EDL selection case. Movies/Movie/ holds both
    version media files with the version cases' entries. Restricted/<case>/
    holds one more copy of a two-user case for a library one user cannot see.
    """
    written = []
    folders = [(case, media_root / LIBRARY_DIR / case["name"], [case["media"]]) for case in edl_cases(manifest)]
    by_name = {case["name"]: case for case in edl_cases(manifest)}
    folders.append((by_name[VERSIONS_CASE], media_root / LIBRARY_DIR / "Movie", list(VERSION_MEDIA)))
    folders.append((by_name[RESTRICTED_CASE], media_root / RESTRICTED_DIR / RESTRICTED_CASE, [by_name[RESTRICTED_CASE]["media"]]))
    for case, case_dir, media in folders:
        prefix = case_dir.relative_to(media_root).as_posix()
        written += [f"{prefix}/{path}" for path in write_case(localize(case, export_ids), fixture_dir, manifest, case_dir, media, video)]
    return written
