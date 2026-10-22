# Export-layout contract fixtures

Editarr writes EDL and MCF files that this plugin serves. The two repositories share one versioned contract, `editarr-export-layout-1`, so both sides test the same bytes.

## Sub-features

- `tests/fixtures/export-layout-v1/` is a byte-exact copy of Editarr's `tests/fixtures/multi-user/export-layout-v1`. `provenance.json` names the source commit and the manifest SHA-256 `c3063858e3ecf2f8982c66a1140f52ab7d806e737ff5c7854b413cdecccf987e`. `.gitattributes` keeps Git from changing its line endings.
- `ExportLayoutFixtureTests` fails if the manifest or any of the 13 payloads changes.
- `EdlGrammarVectorTests` writes each of the 20 grammar vectors to a file and serves it through `EdlController` with the real `EdlParser`. It runs the 7 EDL payloads the same way.
- `SelectionCaseTests` writes each of the 19 selection cases to a temporary media directory with `SelectionCaseMaterializer`. The cases cover personal, default, legacy, malformed, unsupported-action, unreadable, directory, absent, local-ID, and two media-version layouts. For the 14 EDL cases it records what the current legacy selector returns next to the planned contract outcome.

## How to get to it (user POV)

A developer changing EDL selection or importing a new Editarr contract version runs the unit tier. MU-09 implements personal and default EDL selection against these cases. MU-12 adds MCF.

## Driving it with verify.py

```bash
python3 .github/skills/verify-jellyfin-edl/scripts/verify.py test --filter FullyQualifiedName~Contract
```

The detailed console log in `dotnet-test.log` lists each EDL case as `legacy selector <status>, planned contract <outcome>`.

## Gotchas

- Replace the whole directory from a new Editarr contract version. Do not edit a payload or the manifest, and update `PinnedManifestSha256` and `library.PINNED_MANIFEST_SHA256` together.
- The selection tests encode no selection logic. `Current_plugin_serves_only_the_adjacent_legacy_sidecar` pins today's behavior and must be replaced, not loosened, when personal selection ships.
- Unreadable entries are mode 000. The SDK container test runs as the invoking user, but a root process can still read them. Tests assert the mode, not a read failure.
- The materializer writes Unix modes, so these tests do not run on Windows.
- The manifest leaves NaN, Infinity, out-of-range numbers, non-UTF-8 encodings, and non-ASCII whitespace, digits, and letters unpinned.
