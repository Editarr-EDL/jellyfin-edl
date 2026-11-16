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
RESTRICTED_CASES = ("edl-each-user-gets-own-file", "mcf-each-user-gets-own-file")
FORMATS = ("edl", "mcf")
_USER_A = "5a4de0c29b1e4f3a8c6d0e1f2a3b4c5d"
# Plugin-local, not in v1: the manifest has no unreadable MCF case.
LOCAL_CASES = [{
    "name": "local-mcf-unreadable-personal-does-not-fall-back", "media": "Movie.mkv", "principal": "userA", "format": "mcf",
    "entries": [{"path": f"mcf/{_USER_A}/Movie.user-{_USER_A}.mcf", "payload": "mcf-personal-a", "unreadable": True},
                {"path": "mcf/Movie.mcf", "payload": "mcf-default"}],
    "expect": {"outcome": "personal_unreadable"},
}]


def load_manifest(fixture_dir: Path) -> dict:
    """Read the imported manifest and refuse it unless its bytes match the pinned contract."""
    raw = (fixture_dir / "manifest.json").read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    if digest != PINNED_MANIFEST_SHA256:
        raise ValueError(f"export-layout-v1 manifest sha256 {digest} does not match the pinned contract")
    return json.loads(raw)


def live_cases(manifest: dict) -> list[dict]:
    """Every v1 selection case, EDL and MCF, then the plugin-local cases."""
    return manifest["selection"] + LOCAL_CASES


def localize(case: dict, export_ids: dict) -> dict:
    """Return the case with each fixture Jellyfin export ID in its entry paths replaced by a live user's ID."""
    def rename(path: str) -> str:
        for fixture_id, live_id in export_ids.items():
            path = path.replace(fixture_id, live_id)
        return path

    return dict(case, entries=[dict(entry, path=rename(entry["path"])) for entry in case["entries"]])


def contract_expectation(case: dict, export_id: str | None, manifest: dict, server_reads_mode_000: bool = False,
                         fmt: str | None = None) -> dict:
    """What editarr-export-layout-1 selects for one user and format: personal, then default only when personal is absent.

    export_id None is a server API key, which gets only the default whatever
    personal files exist. fmt defaults to the case's format; asking for the other
    format checks that its route ignores the case's files. Whether a mode 000
    file is unreadable depends on the server's user and filesystem, so with
    server_reads_mode_000 an unreadable entry is selected like a readable one.
    """
    fmt = fmt or case["format"]
    stem = os.path.splitext(case["media"])[0]
    entries = {entry["path"]: entry for entry in case["entries"]}
    candidates = [("default", f"{fmt}/{stem}.{fmt}")]
    if export_id is not None:
        candidates.insert(0, ("personal", f"{fmt}/{export_id}/{stem}.user-{export_id}.{fmt}"))
    for scope, path in candidates:
        entry = entries.get(path)
        if entry is None:
            continue
        if entry.get("directory"):
            return {"outcome": f"{scope}_invalid", "status": 422, "payload": None, "format": fmt}
        if entry.get("unreadable") and not server_reads_mode_000:
            return {"outcome": f"{scope}_unreadable", "status": 500, "payload": None, "format": fmt}
        if manifest["payloads"][entry["payload"]]["valid"]:
            return {"outcome": scope, "status": 200, "payload": entry["payload"], "format": fmt}
        return {"outcome": f"{scope}_invalid", "status": 422, "payload": None, "format": fmt}
    return {"outcome": "none", "status": 404, "payload": None, "format": fmt}


def media_path(case: dict, library_dir: str = LIBRARY_DIR) -> str:
    """Container path of the case's media file under the /media mount."""
    return f"/media/{library_dir}/{case['name']}/{case['media']}"


def version_paths() -> list[str]:
    """Container paths of the two-version movie, which Jellyfin groups into one item."""
    return [f"/media/{LIBRARY_DIR}/Movie/{name}" for name in VERSION_MEDIA]


def unreadable_paths(manifest: dict, export_ids: dict) -> list[str]:
    """Container paths of the mode 000 entries in the main library."""
    return [f"/media/{LIBRARY_DIR}/{case['name']}/{entry['path']}"
            for case in (localize(c, export_ids) for c in live_cases(manifest))
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


def restricted_case(manifest: dict) -> dict:
    """The two-user EDL and MCF cases as one movie, for the library user B cannot see."""
    by_name = {case["name"]: case for case in manifest["selection"]}
    edl, mcf = (by_name[name] for name in RESTRICTED_CASES)
    return dict(edl, name=RESTRICTED_CASES[0], format=None, entries=edl["entries"] + mcf["entries"])


def build(manifest: dict, fixture_dir: Path, media_root: Path, video: Path, export_ids: dict) -> list[str]:
    """Write the synthetic library for live users and return the written paths relative to media_root.

    Movies/<case>/ holds each EDL and MCF selection case and each plugin-local
    case. Movies/Movie/ holds both version media files with the version cases'
    entries. Restricted/<case>/ holds the two-user EDL and MCF cases together
    for a library one user cannot see.
    """
    written = []
    folders = [(case, media_root / LIBRARY_DIR / case["name"], [case["media"]]) for case in live_cases(manifest)]
    by_name = {case["name"]: case for case in manifest["selection"]}
    folders.append((by_name[VERSIONS_CASE], media_root / LIBRARY_DIR / "Movie", list(VERSION_MEDIA)))
    restricted = restricted_case(manifest)
    folders.append((restricted, media_root / RESTRICTED_DIR / restricted["name"], [restricted["media"]]))
    for case, case_dir, media in folders:
        prefix = case_dir.relative_to(media_root).as_posix()
        written += [f"{prefix}/{path}" for path in write_case(localize(case, export_ids), fixture_dir, manifest, case_dir, media, video)]
    return written
