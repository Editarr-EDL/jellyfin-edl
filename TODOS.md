# TODOs

## Remaining From Plan Verification

- [x] Run plugin in a live Jellyfin instance and confirm plugin loads and endpoint is discoverable.
- [x] Perform end-to-end API checks with real item IDs in a disposable Jellyfin
  12.0.0. The `verify.py run` drive of run `jfedl-20261031t185130-9578cd` (plugin
  `3dff426`, no uncommitted paths) passed all 207 checks in its `drive.json`.
  - Check `edl-personal-before-default edl as userA` shows that a valid sidecar returns 200 `text/plain`.
  - Check `edl-nothing-present edl as userA` shows that a missing sidecar returns 404.
  - Check `edl-malformed-personal-does-not-fall-back edl as userA` shows that an
    invalid EDL returns 422 JSON. `EdlParserTests.Validate_ReturnsInvalidDiagnostics_ForMalformedEntries`
    asserts each diagnostic's `LineNumber` and `Message`.
- [ ] Prove that a non-video item returns 404. No drive check, matrix case, or C#
  test requests a non-video item.
- [x] Capture startup and request logs for smoke verification (no plugin startup errors/warnings affecting behavior).

## Follow-up Items

- [x] Add Docker Compose based Jellyfin integration harness for repeatable runtime verification.
- [ ] Decide if parser action/token support should be expanded or narrowed, then codify tests.
- [x] Add packaging instructions for deploying the built plugin into Jellyfin plugin directories.
- [x] Add fixture bootstrap scripts for local valid/missing/invalid EDL scenarios.
- [x] Add one-command authenticated status-matrix runner for 200/404/422 checks.
