#!/usr/bin/env python3
"""Verify the Jellyfin EDL plugin: .NET unit tier and a disposable real Jellyfin HTTP tier.

Usage:
  verify.py test [--filter EXPR] [--old-glibc]
                                     dotnet test in a disposable SDK container, or with that
                                     SDK in Rocky Linux 8 (glibc 2.28) for --old-glibc
  verify.py run [--inject-failure]   launch, doctor, drive, then always clean up
  verify.py launch                   start an instance and print its evidence dir
  verify.py doctor EVIDENCE_DIR      read-only health check of a launched instance
  verify.py drive EVIDENCE_DIR       request every synthetic item through both export routes
  verify.py cleanup EVIDENCE_DIR     remove run-owned resources, keep evidence

Exit codes: 0 pass, 1 fail, 2 blocked prerequisite, 64 usage.
"""

from __future__ import annotations

import sys

sys.dont_write_bytecode = True

import hashlib  # noqa: E402
import secrets  # noqa: E402
import subprocess  # noqa: E402
import uuid  # noqa: E402
import xml.etree.ElementTree as ElementTree  # noqa: E402
from datetime import datetime, timezone  # noqa: E402
from pathlib import Path  # noqa: E402

import evidence  # noqa: E402
import instance  # noqa: E402
import jellyfin_api  # noqa: E402
import journey  # noqa: E402
import library  # noqa: E402

REPO = Path(__file__).resolve().parents[4]
FIXTURE = REPO / "tests" / "fixtures" / "export-layout-v1"
EVIDENCE_ROOT = REPO / "artifacts" / "verify"
EXPECTED_SERVER_VERSION = "12.0.0"
PLUGIN_ID = "8b8e1f7794f84e8f8eafc7a8d8bb8d80"
ADMIN = "jfedl-admin"
TEST_PROJECT = "tests/Jellyfin.Plugin.Edl.Tests/Jellyfin.Plugin.Edl.Tests.csproj"


def git(*args: str) -> str:
    return subprocess.run(["git", "-C", str(REPO), *args], capture_output=True, text=True, check=True).stdout.strip()


def new_run(kind: str) -> tuple[Path, dict]:
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dt%H%M%S")
    run_id = f"jfedl-{stamp}-{secrets.token_hex(3)}"
    directory = EVIDENCE_ROOT / run_id
    directory.mkdir(parents=True)
    run = {"kind": kind, "runId": run_id, "commit": git("rev-parse", "HEAD"),
           "dirtyPaths": len(git("status", "--porcelain").splitlines()), "createdAt": datetime.now(timezone.utc).isoformat()}
    return directory, run


def save(directory: Path, run: dict) -> None:
    evidence.write_json(directory / "run.json", run)


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def client_for(run: dict, token: str | None = None) -> jellyfin_api.Jellyfin:
    return jellyfin_api.Jellyfin(f"http://127.0.0.1:{run['port']}", run["runId"], token)


def secrets_of(run: dict) -> dict:
    path = Path(run["scratch"]) / "secrets.json"
    return evidence.read_json(path) if path.exists() else {}


def command_test(argv: list) -> int:
    old_glibc = "--old-glibc" in argv
    directory, run = new_run("dotnet-unit-old-glibc" if old_glibc else "dotnet-unit")
    test_filter = f' --filter "{argv[argv.index("--filter") + 1]}"' if "--filter" in argv else ""
    save(directory, run)
    try:
        run["images"] = instance.preflight([instance.SDK_IMAGE, *([instance.OLD_GLIBC_IMAGE] if old_glibc else [])])
    except instance.Blocked as error:
        run["blocked"] = str(error)
        save(directory, run)
        print(f"BLOCKED: {error}\nevidence: {directory}")
        return 2
    run["exit"] = instance.sdk_run(run["runId"], REPO, [(directory, "/evidence")],
                                   f'dotnet test {TEST_PROJECT} -c Release{test_filter} '
                                   '--logger "trx;LogFileName=/evidence/dotnet-test.trx" '
                                   '--logger "console;verbosity=detailed"', directory / "dotnet-test.log", old_glibc)
    trx = directory / "dotnet-test.trx"
    if trx.exists():
        counters = ElementTree.parse(trx).getroot().find(".//{*}ResultSummary/{*}Counters")
        run["counters"] = {key: int(counters.get(key)) for key in ("total", "executed", "passed", "failed")}
    save(directory, run)
    ok = run["exit"] == 0 and run.get("counters", {}).get("failed") == 0
    print(f"{'PASS' if ok else 'FAIL'} {run['kind']} {run.get('counters')}\nevidence: {directory}")
    return 0 if ok else 1


def launch(directory: Path, run: dict) -> None:
    log = directory / "docker.log"
    run["images"] = instance.preflight([instance.SDK_IMAGE, instance.JELLYFIN_IMAGE])
    manifest = library.load_manifest(FIXTURE)
    scratch = instance.new_scratch(run["runId"])
    run_id = run["runId"]
    run.update({"scratch": str(scratch), "scratchIdentity": instance.scratch_identity(scratch),
                "container": f"{run_id}-jellyfin", "port": None,
                "volumes": [f"{run_id}-config", f"{run_id}-cache"], "image": instance.JELLYFIN_IMAGE,
                "imageTag": instance.JELLYFIN_TAG, "stopped": False})
    save(directory, run)
    staged = instance.build_plugin(run_id, REPO, scratch, directory / "build.log")
    run["pluginSha256"] = sha256_file(staged / "Jellyfin.Plugin.Edl.dll")
    video = instance.generate_video(run_id, scratch, log)
    (scratch / "media").mkdir()
    run["port"] = instance.start(run, log)
    save(directory, run)
    client = client_for(run)
    evidence.write_json(directory / "server-public.json", jellyfin_api.wait_ready(client, 240))
    hidden = {"password": secrets.token_urlsafe(24)}
    evidence.private_file(scratch / "secrets.json", hidden)
    jellyfin_api.complete_wizard(client, ADMIN, hidden["password"])
    hidden["token"], admin_id = jellyfin_api.authenticate(client, ADMIN, hidden["password"])
    evidence.private_file(scratch / "secrets.json", hidden)
    client.token = hidden["token"]
    users = {}
    for name in journey.USERS:
        hidden[f"{name}Password"] = secrets.token_urlsafe(24)
        evidence.private_file(scratch / "secrets.json", hidden)
        users[name] = uuid.UUID(jellyfin_api.create_user(client, f"jfedl-{name}", hidden[f"{name}Password"])).hex
        hidden[f"{name}Token"], _ = jellyfin_api.authenticate(client_for(run), f"jfedl-{name}", hidden[f"{name}Password"])
        evidence.private_file(scratch / "secrets.json", hidden)
    hidden["apiKey"] = jellyfin_api.create_api_key(client, "jfedl-verify")
    evidence.private_file(scratch / "secrets.json", hidden)
    export_ids = {manifest["principals"][name]["exportId"]: users[name] for name in journey.USERS}
    files = library.build(manifest, FIXTURE, scratch / "media", video, export_ids)
    evidence.write_json(directory / "library.json", {"videoSha256": sha256_file(video), "files": files})
    jellyfin_api.add_movie_library(client, "Synthetic", f"/media/{library.LIBRARY_DIR}")
    jellyfin_api.add_movie_library(client, "Restricted", f"/media/{library.RESTRICTED_DIR}")
    jellyfin_api.limit_libraries(client, users["userB"], [jellyfin_api.library_ids(client)["Synthetic"]])
    restricted = library.restricted_case(manifest)
    paths = ({library.media_path(case) for case in library.live_cases(manifest)} | set(library.version_paths())
             | {library.media_path(restricted, library.RESTRICTED_DIR)})
    items = jellyfin_api.wait_for_movies(client, paths, 300)
    reads = [instance.docker("exec", run["container"], "cat", path, check=False, log=log).returncode == 0
             for path in library.unreadable_paths(manifest, export_ids)]
    evidence.write_json(directory / "items.json", {"adminUserId": admin_id, "users": users, "items": items,
                                                  "serverReadsMode000": all(reads)})


def doctor(directory: Path) -> bool:
    run = evidence.read_json(directory / "run.json")
    client = client_for(run, secrets_of(run).get("token"))
    checks = []

    def check(name: str, probe) -> None:
        try:
            ok, detail = probe()
        except Exception as error:
            ok, detail = False, str(error)
        checks.append({"check": name, "ok": ok, "detail": detail})

    def container():
        state = instance.docker("inspect", "--format", '{{.State.Running}} {{index .Config.Labels "jfedl.run"}} {{.Image}}',
                                run["container"]).stdout.split()
        image = instance.docker("image", "inspect", "--format", "{{.Id}}", run["image"]).stdout.strip()
        return state == ["true", run["runId"], image], " ".join(state)

    def loopback():
        binding = instance.docker("port", run["container"], "8096/tcp").stdout.strip()
        return binding == f"127.0.0.1:{run['port']}", binding

    def version():
        info = client.request("GET", "/System/Info/Public", authenticated=False).json()
        return info.get("Version") == EXPECTED_SERVER_VERSION, info.get("Version")

    def plugin():
        response = client.request("GET", "/Plugins")
        if response.status != 200:
            return False, f"HTTP {response.status}"
        found = [p for p in response.json() if p.get("Id", "").replace("-", "").lower() == PLUGIN_ID]
        return bool(found) and found[0].get("Status") == "Active", [(p.get("Name"), p.get("Version"), p.get("Status")) for p in found]

    def deployed_dll():
        output = instance.docker("exec", run["container"], "sh", "-c",
                                 f"sha256sum /config/plugins/*/{instance.PLUGIN_FILES[0]}").stdout.split()
        return output[:1] == [run["pluginSha256"]], output

    check("container running, owned, pinned image", container)
    check("published on loopback only", loopback)
    check(f"server version {EXPECTED_SERVER_VERSION}", version)
    check("plugin active (authenticated /Plugins)", plugin)
    check("deployed DLL is this build", deployed_dll)
    ok = all(c["ok"] for c in checks)
    evidence.write_json(directory / "doctor.json", {"ok": ok, "checks": checks})
    print(f"doctor {'ok' if ok else 'FAILED'}: " + "; ".join(f"{c['check']}={c['ok']}" for c in checks))
    return ok


def drive(directory: Path) -> bool:
    """Run the two-user journey over both export routes and record every check in drive.json."""
    run = evidence.read_json(directory / "run.json")
    manifest = library.load_manifest(FIXTURE)
    state = evidence.read_json(directory / "items.json")
    hidden = secrets_of(run)
    clients = {"anonymous": client_for(run), "apiKey": client_for(run, hidden["apiKey"]),
               **{name: client_for(run, hidden[f"{name}Token"]) for name in journey.USERS}}
    checks = journey.run(clients, manifest, state["items"], state["users"], state["serverReadsMode000"])
    ok = all(check["ok"] for check in checks)
    evidence.write_json(directory / "drive.json", {"ok": ok, "routes": {fmt: "GET " + route.format("{itemId}") for fmt, route in journey.ROUTES.items()},
                                                  "users": state["users"],
                                                  "serverReadsMode000": state["serverReadsMode000"], "checks": checks})
    failed = [check["check"] for check in checks if not check["ok"]]
    print(f"drive {'ok' if ok else 'FAILED'}: {len(checks) - len(failed)}/{len(checks)} checks pass"
          + (f"; failed: {failed}" if failed else ""))
    return ok


def write_report(path: Path, report: dict) -> None:
    """Write a cleanup or redaction report, keeping any earlier attempts it replaces."""
    if path.exists():
        previous = evidence.read_json(path)
        report["earlierAttempts"] = previous.pop("earlierAttempts", []) + [previous]
    evidence.write_json(path, report)


def finish(directory: Path, run: dict) -> bool:
    """Capture logs, redact the evidence, then remove run-owned resources.

    Success needs a completed redaction and a clean cleanup, recorded as
    `redacted` and `stopped` in run.json. Scratch holds the only copy of the
    secrets, so logs are captured and redacted only while it exists, and
    cleanup removes it only after redaction. The caller's run is scrubbed of
    the secrets in place before anything is saved, so an error that echoed a
    secret cannot be written back after redaction. A retry redoes what an
    interruption left unfinished; a repeat after success changes nothing.
    """
    if run.get("stopped") and run.get("redacted") and evidence.read_json(directory / "cleanup.json")["clean"]:
        return True
    have_secrets = bool(run.get("scratch")) and Path(run["scratch"]).exists()
    if not run.get("redacted") and run.get("scratch") and not have_secrets:
        raise RuntimeError("scratch secrets are gone before redaction completed; evidence kept for manual review")
    hidden = list(secrets_of(run).values()) if have_secrets else []
    run.update(evidence.scrub(run, hidden))
    if run.get("container") and have_secrets:
        logs = instance.docker("logs", run["container"], check=False)
        if logs.returncode == 0:
            run["redacted"] = False
            save(directory, run)
            (directory / "jellyfin.log").write_text(logs.stdout + logs.stderr)
    if not run.get("redacted"):
        write_report(directory / "redaction.json", evidence.redact(directory, hidden))
        run["redacted"] = True
        save(directory, run)
    report = instance.cleanup(run, directory / "docker.log") if run.get("scratch") else {"clean": True, "steps": [], "errors": []}
    write_report(directory / "cleanup.json", report)
    run["stopped"] = True
    save(directory, run)
    return report["clean"]


def command_run(argv: list) -> int:
    directory, run = new_run("jellyfin-http")
    save(directory, run)
    code = 1
    try:
        launch(directory, run)
        code = 0 if doctor(directory) and drive(directory) else 1
        if "--inject-failure" in argv:
            raise RuntimeError("injected failure after drive")
    except instance.Blocked as error:
        code, run["error"] = 2, f"BLOCKED: {error}"
    except Exception as error:
        code, run["error"] = 1, f"{type(error).__name__}: {error}"
    finally:
        try:
            clean = finish(directory, run)
        except Exception as error:
            clean = False
            print(f"cleanup failed, retry with verify.py cleanup: {type(error).__name__}: {error}")
    if not clean:
        code = code or 1
    print(f"{['PASS', 'FAIL', 'BLOCKED'][code]} jellyfin-http commit {run['commit']} {run.get('error', '')}\n"
          f"evidence: {directory}\ncleanup clean: {clean}")
    return code


def main(argv: list) -> int:
    if not argv:
        print(__doc__)
        return 64
    command, rest = argv[0], argv[1:]
    if command == "test":
        return command_test(rest)
    if command == "run":
        return command_run(rest)
    if command == "launch":
        directory, run = new_run("jellyfin-http")
        save(directory, run)
        try:
            launch(directory, run)
        except Exception as error:
            run["error"] = f"{type(error).__name__}: {error}"
            finish(directory, run)
            print(f"launch failed and was cleaned up: {run['error']}\nevidence: {directory}")
            return 2 if isinstance(error, instance.Blocked) else 1
        print(f"evidence: {directory}\nport: {run['port']}")
        return 0
    if command in ("doctor", "drive", "cleanup") and len(rest) == 1:
        directory = Path(rest[0]).resolve()
        try:
            if command == "cleanup":
                return 0 if finish(directory, evidence.read_json(directory / "run.json")) else 1
            return 0 if (doctor if command == "doctor" else drive)(directory) else 1
        except Exception as error:
            print(f"{command} failed: {type(error).__name__}: {error}")
            return 1
    print(__doc__)
    return 64


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
