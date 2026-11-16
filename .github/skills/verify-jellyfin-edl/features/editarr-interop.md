# Editarr interoperability

Editarr publishes personal and item-default EDL and MCF files beside a media file.
This plugin serves them to signed-in Jellyfin users. The paired check proves both
sides agree on real bytes: the file Editarr published for a user is the file that
user receives, for both formats, through account linking, policy changes,
regeneration, withdrawal, copy, transfer, and review and account deletion.

## How to get to it (user POV)

A Jellyfin user plays a movie on a client that requests
`GET /Plugins/Edl/ByItem/{itemId}` or `GET /Plugins/Edl/Mcf/ByItem/{itemId}`.
The plugin answers with that user's personal file, else the item default when
the personal file is absent, else 404. An invalid or unreadable personal file
is an error, never a default. The adjacent legacy `Movie.edl` and `Movie.mcf`
are never read.

## Driving it

Editarr owns the journey. From the Editarr checkout:

```bash
VERIFY_PLUGIN_CHECKOUT=/path/to/this/checkout VERIFY_PLUGIN_PORT=9239 \
  node .github/skills/verify-editarr/scripts/verify.mjs auth --plugin-interop --ports=9220-9229
```

The journey calls this helper three ways and never reads its secrets into argv:

1. `verify.py launch --label editarr.verify.run=<run> --interop <Editarr scratch>/jellyfin-media --port N`.
   The tree holds `Movies/{Interop Movie, Versions (1080p and 2160p), Policy, Transfer}`
   and `Restricted/Hidden`, all copies of Editarr's `synthetic-av-v1` media.
2. `verify.py matrix <evidence> <cases> <phase>` once per phase. Editarr computes each
   expected digest from the file it published and checks it against the publication's
   `content_sha256` before it writes the case. Link and regeneration phases use
   `untilChanged` cases that poll while Editarr changes the file. The failure
   withdrawal phase orders the poll's `timeline` against Editarr's export states.
   Editarr refuses a phase whose record already exists, and accepts only exit 0
   with a passing record whose case names equal the cases it wrote.
3. `verify.py cleanup-label editarr.verify.run=<run>` in the journey's `finally`,
   even when launch failed or timed out partway. Editarr kills the helper's whole
   process group on a timeout, so no Docker command outlives it.

`VERIFY_PLUGIN_LABELS=KEY=VALUE[,KEY=VALUE]` adds Docker labels for a task.
Exit 2 from launch (no Docker, no image, no helper) makes the Editarr journey
exit 2 BLOCKED. That is not a pass.

## Evidence

The plugin run directory under `artifacts/verify/<run-id>/` holds `matrix-<phase>.json`
for every phase and `counterpart.json` with the Editarr commit and evidence path.
Editarr's evidence holds copies of each matrix file, `plugin-interop-result.json`
with both commits, the plugin DLL hash, the Jellyfin image digest, the
synthetic-media and export-layout manifest digests, and the legacy file digests.
Prove a commit pair only with `dirtyPaths` 0 on both sides.

## Gotchas

- Docker Desktop on macOS bind-mounts with the host user's permissions, so the root
  server cannot read a mode 000 personal file and the journey expects 500. A Linux
  host that can read it expects the personal bytes. The journey probes which with
  `docker exec cat` first.
- Jellyfin groups `Versions - 1080p.mkv` and `Versions - 2160p.mkv` into one item
  with two media sources, so `version` selects the physical file.
- The libraries keep realtime monitoring off, so the regeneration-failure phase can
  move the media file away briefly without Jellyfin dropping the item.
