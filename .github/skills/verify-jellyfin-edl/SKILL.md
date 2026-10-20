---
name: verify-jellyfin-edl
description: Verify the Jellyfin EDL plugin with its .NET tests and with a disposable real Jellyfin 12.0.0 server driven over authenticated HTTP. Use after changing the plugin, its EDL route, EDL selection, or the imported Editarr export-layout fixture.
---

# Verify the Jellyfin EDL plugin

The helper has two tiers. Report them separately, because neither proves the other.

| Tier | Command | Proves | Does not prove |
| --- | --- | --- | --- |
| Unit | `verify.py test` | Parser, controller, and lookup behavior in xUnit, plus the fixture contract | That Jellyfin loads the plugin, authenticates the request, or resolves real items |
| Live | `verify.py run` | A real Jellyfin 12.0.0 server loads this build and answers `GET /Plugins/Edl/ByItem/{itemId}` for a signed-in user | Anything the drive does not request |

Run every command from the repository root. Paths below are relative to it.

```bash
python3 .github/skills/verify-jellyfin-edl/scripts/verify.py test
python3 .github/skills/verify-jellyfin-edl/scripts/verify.py run
```

Exit codes are 0 for pass, 1 for fail, 2 for a missing prerequisite, and 64 for usage. Exit 2 is not a pass.

## Prerequisites

- A running Docker daemon. Host .NET is not used.
- `python3` 3.9 or newer and `git`.
- Network access for NuGet restore inside the SDK container on every run, and for the first pull of the pinned images.

The helper pins two images in `scripts/instance.py`.

- `mcr.microsoft.com/dotnet/sdk:10.0` builds and tests the net10.0 projects. Each run records its image ID.
- `jellyfin/jellyfin@sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5`, published as `jellyfin/jellyfin:12.0.20260908-012347`. It reports server version 12.0.0, which matches the `Jellyfin.Controller` 12.0.0 package. Doctor fails on any other version.

## Launch

`verify.py launch` starts one instance and leaves it running. `verify.py run` launches, checks, drives, and always cleans up. Launch does this:

1. Checks Docker and both images. It pulls a missing image once and records the pull.
2. Verifies that `tests/fixtures/export-layout-v1/manifest.json` has the pinned SHA-256.
3. Builds the plugin in Release inside a disposable SDK container from a read-only copy of the checkout. It stages the same four files as `scripts/stage-plugin.sh`.
4. Encodes a two-second synthetic test pattern with the Jellyfin image's own FFmpeg.
5. Writes one movie folder per EDL selection case in the fixture (14 folders), with that case's sidecar files.
6. Creates two labelled volumes and a container that publishes port 8096 on `127.0.0.1` at a random host port. It mounts the media read-only and copies the plugin into `/config/plugins/Jellyfin.Plugin.Edl`.
7. Waits for `/System/Info/Public`, completes the startup wizard with a generated administrator password, signs in, and adds a movie library with internet metadata and image fetchers disabled.

The instance is ready when all 14 synthetic movies are indexed. Launch prints the evidence directory and the port.

## Doctor

```bash
python3 .github/skills/verify-jellyfin-edl/scripts/verify.py doctor artifacts/verify/<run-id>
```

Doctor is read-only. Run it before the first drive and after any failure. It checks that:

- the container is running, carries this run's label, and uses the pinned image;
- the port is published on `127.0.0.1` only;
- `/System/Info/Public` reports version 12.0.0;
- authenticated `/Plugins` lists plugin `8b8e1f77-94f8-4e8f-8eaf-c7a8d8bb8d80` as `Active`;
- the deployed `Jellyfin.Plugin.Edl.dll` has the SHA-256 of this run's build.

## Drive

```bash
python3 .github/skills/verify-jellyfin-edl/scripts/verify.py drive artifacts/verify/<run-id>
```

The drive sends one request without a token and expects 401. It then requests `GET /Plugins/Edl/ByItem/{itemId}` with the administrator token for each of the 14 items. The expected result comes from the current plugin's documented rule in `scripts/library.py` (`legacy_expectation`). Only the adjacent `<stem>.edl` is read. A valid file returns 200 `text/plain` with the payload's exact SHA-256. An invalid file returns 422. Anything else returns 404. Each result also records the case's planned contract outcome, which the current plugin does not implement.

The token lives only in `secrets.json` (mode 0600) in the run's scratch directory. Do not print it or pass it on a command line. To add a request, extend `drive` in `scripts/verify.py`, which uses `jellyfin_api.Jellyfin`.

## Evidence

Each run writes `artifacts/verify/<run-id>/`. The `artifacts/` directory is ignored by Git.

- `run.json` holds the run ID, the plugin commit, the count of uncommitted paths, the image IDs, the container and volume names, the port, the plugin DLL hash, and `stopped`.
- `doctor.json` and `drive.json` hold each check and each request with its status, content type, body hash, and body for 200 responses.
- `server-public.json`, `items.json`, and `library.json` record the server, the indexed item IDs, and the synthetic files.
- `build.log`, `docker.log`, and `jellyfin.log` hold the build, every Docker command, and the server log captured before removal.
- `cleanup.json` lists each cleanup step and any leftover containers, volumes, or scratch directory.
- `redaction.json` reports how many secrets were scanned and replaced. The helper fails if a secret survives.
- The unit tier writes `dotnet-test.log`, `dotnet-test.trx`, and the counters in `run.json`.

A proof for a commit needs `dirtyPaths` equal to 0 in `run.json`. A run on an uncommitted tree proves only the working copy.

## Cleanup

```bash
python3 .github/skills/verify-jellyfin-edl/scripts/verify.py cleanup artifacts/verify/<run-id>
```

Cleanup captures the server log, then removes only containers and volumes labelled `jfedl.run=<run-id>` and the recorded scratch directory under the system temp directory. It never removes evidence. Repeating it is safe. `verify.py run --inject-failure` fails after the drive and proves that cleanup still runs and the evidence survives. Never stop containers by name pattern or prune Docker.

## Helpers

- `scripts/verify.py` is the entry point described above.
- `scripts/instance.py` owns Docker: preflight, the SDK container, the plugin build, synthetic video, start, and cleanup.
- `scripts/jellyfin_api.py` owns HTTP: readiness, the startup wizard, sign-in, the library, and item polling.
- `scripts/library.py` lays out the fixture cases on disk and holds the legacy expectation.
- `scripts/evidence.py` writes evidence and redacts secrets.

The repository's `scripts/` directory remains the manual harness. The helper covers the same ground without calling it. It stages the four files `stage-plugin.sh` stages, and its drive covers the 401, 200, 404, and 422 checks of `jellyfin-smoke.sh` and `run-edl-matrix.sh`. Those scripts write build output into the checkout, target the unpublished `jellyfin/jellyfin:12.0.0` Compose service, and pass the API key on the `curl` command line.

Test the helper itself without writing bytecode into the checkout.

```bash
python3 -B -m unittest discover -s .github/skills/verify-jellyfin-edl/scripts
```

## Feature map

Read [features/README.md](features/README.md) before choosing what to drive.
