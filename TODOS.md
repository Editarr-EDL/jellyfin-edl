# TODOs

## Remaining From Plan Verification

- [x] Run plugin in a live Jellyfin instance and confirm plugin loads and endpoint is discoverable.
- [ ] Perform end-to-end API checks with real item IDs:
  - valid video item with valid sidecar returns 200 text/plain
  - missing sidecar returns 404
  - non-video item returns 404
  - invalid EDL returns 422 with diagnostics
- [x] Capture startup and request logs for smoke verification (no plugin startup errors/warnings affecting behavior).

## Follow-up Items

- [x] Add Docker Compose based Jellyfin integration harness for repeatable runtime verification.
- [ ] Decide if parser action/token support should be expanded or narrowed, then codify tests.
- [x] Add packaging instructions for deploying the built plugin into Jellyfin plugin directories.
- [x] Add fixture bootstrap scripts for local valid/missing/invalid EDL scenarios.
- [x] Add one-command authenticated status-matrix runner for 200/404/422 checks.
