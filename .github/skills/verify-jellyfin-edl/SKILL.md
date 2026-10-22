---
name: verify-jellyfin-edl
description: Verify the Jellyfin EDL plugin with its .NET tests and with a disposable real Jellyfin 12.0.0 server driven over authenticated HTTP by two synthetic users. Use after changing the plugin, its EDL route, request authorization, personal or default EDL selection, or the imported Editarr export-layout fixture.
---

# Verify the Jellyfin EDL plugin

The helper has two tiers. Report them separately, because neither proves the other.

| Tier | Command | Proves | Does not prove |
| --- | --- | --- | --- |
| Unit | `verify.py test` | Authorization, version resolution, selection, exact bytes, and validators through the controller with Jellyfin's managers mocked, plus the fixture contract | That Jellyfin loads the plugin, authenticates the request, or applies its real library access rules |
| Live | `verify.py run` | A real Jellyfin 12.0.0 server loads this build and answers `GET /Plugins/Edl/ByItem/{itemId}` for two signed-in users, a server API key, and no token | Anything the drive does not request |

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

The helper names two images in `scripts/instance.py`. Only Jellyfin is pinned by digest.

- `mcr.microsoft.com/dotnet/sdk:10.0` builds and tests the net10.0 projects. The `10.0` tag moves when Microsoft publishes a new SDK, so each run records the resolved image ID in `run.json` under `images`.
- `jellyfin/jellyfin@sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5`, published as `jellyfin/jellyfin:12.0.20260908-012347`. It reports server version 12.0.0, which matches the `Jellyfin.Controller` 12.0.0 package. Doctor fails on any other version.

## Launch

`verify.py launch` starts one instance and leaves it running. `verify.py run` launches, checks, drives, and always cleans up. Launch does this:

1. Checks Docker and both images. It pulls a missing image once and records the pull.
2. Verifies that `tests/fixtures/export-layout-v1/manifest.json` has the pinned SHA-256.
3. Builds the plugin in Release inside a disposable SDK container from a read-only copy of the checkout. It stages the same four files as `scripts/stage-plugin.sh`.
4. Encodes a two-second synthetic test pattern with the Jellyfin image's own FFmpeg.
5. Creates two labelled volumes and a container that publishes port 8096 on `127.0.0.1` at a random host port. It mounts the scratch media directory read-only and copies the plugin into `/config/plugins/Jellyfin.Plugin.Edl`.
6. Waits until `/health` answers `Healthy`, records `/System/Info/Public`, completes the startup wizard with a generated administrator password, and signs in.
7. Creates ordinary users `jfedl-userA` and `jfedl-userB` with generated passwords, signs each in, and creates an API key named `jfedl-verify`.
8. Writes the library, with every fixture Jellyfin export ID replaced by the live user's ID. `Movies/<case>/` holds each of the 14 EDL selection cases. `Movies/Movie/` holds `Movie - 1080p.mkv` and `Movie - 2160p.mkv` with the version cases' files, which Jellyfin groups into one item with two media sources. `Restricted/edl-each-user-gets-own-file/` repeats a two-user case.
9. Adds the `Synthetic` (`/media/Movies`) and `Restricted` (`/media/Restricted`) movie libraries with internet metadata and image fetchers disabled, then limits `jfedl-userB` to `Synthetic`.
10. Waits until all 17 media paths are media sources of indexed movies, then runs `cat` on the mode 000 fixture file inside the container and records whether the server can read it as `serverReadsMode000`.

Launch prints the evidence directory and the port.

## Doctor

```bash
python3 .github/skills/verify-jellyfin-edl/scripts/verify.py doctor artifacts/verify/<run-id>
```

Doctor changes nothing in the instance. It writes `doctor.json` in the evidence directory and replaces any earlier one, so copy a passing `doctor.json` before running doctor again after cleanup. Run it before the first drive and after any failure. It checks that:

- the container is running, carries this run's label, and uses the pinned image;
- the port is published on `127.0.0.1` only;
- `/System/Info/Public` reports version 12.0.0;
- authenticated `/Plugins` lists plugin `8b8e1f77-94f8-4e8f-8eaf-c7a8d8bb8d80` as `Active`;
- the deployed `Jellyfin.Plugin.Edl.dll` has the SHA-256 of this run's build.

## Drive

```bash
python3 .github/skills/verify-jellyfin-edl/scripts/verify.py drive artifacts/verify/<run-id>
```

The drive runs `scripts/journey.py`. Every expected result comes from `library.contract_expectation`, the editarr-export-layout-1 rule for the requester. For a user that is the personal file, then the default only when the personal path is absent, never the adjacent legacy file. For the API key it is the default alone, whatever personal files exist. A 200 must be `text/plain` with a body SHA-256 equal to the fixture payload's, which proves exact bytes. Every response except the 401 must carry `Cache-Control: private, no-cache`. The checks are:

- no token gives 401;
- each of the 14 case items as user A, user B, and the API key, with the case's planned outcome recorded for its own principal, plus the API key again with `?userId=` naming the case's user (56 requests). The API key gets the default bytes where a default exists, 404 for `version-1080p-has-personal` and the other cases without one, and 422 for the malformed default;
- the two-version item is one item, each version by `mediaSourceId` as each user and the API key, and a `mediaSourceId` outside the item gives 404 to a user and to the API key;
- the restricted item gives 404 to user B, who has a personal file there, user A's personal file to user A, and the default to the API key, which sees every library;
- user A's own `ETag` gives 304; user B sending user A's `ETag` gets user B's file; user B sending user A's `ETag` for identical default bytes gets 200 and a different `ETag`; user A sending the 2160p `ETag` for 1080p gets the 1080p file;
- the API key sending user A's `ETag` for identical default bytes gets 200 and an `ETag` unlike either user's; user A sending the API key's `ETag` gets 200; the API key repeating its own `ETag` gets 304, also with `?userId=` naming user A.

The mode 000 case expects `personal_unreadable` (500) when `serverReadsMode000` is false and the personal file otherwise. On Docker Desktop for macOS the root server could not read it, probably because the bind mount applies the host user's permissions, so the drive requests a real 500 there.

The tokens and API key live only in `secrets.json` (mode 0600) in the run's scratch directory. Do not print them or pass them on a command line. To add a request, extend `journey.run`, which uses `jellyfin_api.Jellyfin`.

## Evidence

Each run writes `artifacts/verify/<run-id>/`. The `artifacts/` directory is ignored by Git. Evidence files are ordinary files (mode 0644) and hold no secrets.

- `run.json` holds the run ID, the plugin commit, the count of uncommitted paths, the image IDs, the container and volume names, the port, the scratch path and its device and inode (`scratchIdentity`), the plugin DLL hash, any `error`, and the cleanup state `redacted` and `stopped`.
- `doctor.json` and `drive.json` hold each check and each request with its status, content type, `Cache-Control`, `ETag`, and body hash.
- `server-public.json`, `items.json`, and `library.json` record the server, the user IDs, the indexed item and media source IDs, `serverReadsMode000`, and the synthetic files.
- `build.log`, `docker.log`, and `jellyfin.log` hold the build, every Docker command, and the server log.
- `cleanup.json` lists each cleanup step, its errors, and any leftover containers, volumes, or scratch directory.
- `redaction.json` reports how many secrets were scanned and replaced. The helper fails if a secret survives.
- When cleanup is retried, `cleanup.json` and `redaction.json` keep the replaced reports under `earlierAttempts`.
- The unit tier writes `dotnet-test.log`, `dotnet-test.trx`, and the counters in `run.json`.

A proof for a commit needs `dirtyPaths` equal to 0 in `run.json`. A run on an uncommitted tree proves only the working copy.

## Cleanup

```bash
python3 .github/skills/verify-jellyfin-edl/scripts/verify.py cleanup artifacts/verify/<run-id>
```

Cleanup captures the server log, redacts the evidence, and then removes only containers and volumes labelled `jfedl.run=<run-id>` and the recorded scratch directory. It never removes evidence.

The scratch `secrets.json` (mode 0600) is the only copy of the administrator password and token, both user passwords and tokens, and the API key, and redaction needs it. Cleanup therefore redacts first. Before it saves anything, it also replaces the secrets in the run record it holds in memory, so an `error` that echoed a secret keeps its text with `[REDACTED]` and is never written back after redaction. It keeps scratch until the containers and volumes are gone, so a retry can still capture and redact the server log. It removes scratch only when the path is a real directory, not a symlink, directly under the system temp directory, with the device and inode recorded at launch. The directory must also contain a `.jfedl-run` marker naming this run, or be empty with no marker, which is what an interrupted final `rmdir` leaves. It removes the marker last and never follows a symlink inside scratch.

Cleanup succeeds only when redaction completed and every resource is gone. Any other outcome exits 1, keeps the evidence, and reports the errors in `cleanup.json` or on the console. Rerun `verify.py cleanup` after fixing the cause; it repeats only the unfinished work. Repeating it after a success changes nothing. If scratch disappears before redaction finished, cleanup refuses to report success, because it can no longer prove the evidence is clean. `verify.py run --inject-failure` fails after the drive and proves that cleanup still runs and the evidence survives. Never stop containers by name pattern or prune Docker.

## Helpers

- `scripts/verify.py` is the entry point described above.
- `scripts/instance.py` owns Docker: preflight, the SDK container, the plugin build, synthetic video, start, and cleanup.
- `scripts/jellyfin_api.py` owns HTTP: readiness, the startup wizard, sign-in, users, library access, the API key, the libraries, and item polling.
- `scripts/library.py` lays out the fixture cases on disk for the live user IDs and holds the contract expectation.
- `scripts/journey.py` is the two-user drive.
- `scripts/evidence.py` writes evidence and redacts secrets.

The repository's `scripts/` directory remains the manual harness. It needs a signed-in user's token in `JELLYFIN_USER_TOKEN` to reach personal files, because an API key reads only defaults. It writes build output into the checkout, targets the unpublished `jellyfin/jellyfin:12.0.0` Compose service, and passes the token on the `curl` command line.

Test the helper itself without writing bytecode into the checkout.

```bash
python3 -B -m unittest discover -s .github/skills/verify-jellyfin-edl/scripts
```

## Feature map

Read [features/README.md](features/README.md) before choosing what to drive.
