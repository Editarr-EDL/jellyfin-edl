# Export-layout contract fixtures

Editarr writes EDL and MCF files that this plugin serves. The two repositories share one versioned contract, `editarr-export-layout-1`, so both sides test the same bytes.

## Sub-features

- `tests/fixtures/export-layout-v1/` is a byte-exact copy of Editarr's `tests/fixtures/multi-user/export-layout-v1`. `provenance.json` names the source commit and the manifest SHA-256 `c3063858e3ecf2f8982c66a1140f52ab7d806e737ff5c7854b413cdecccf987e`. `.gitattributes` keeps Git from changing its line endings.
- `ExportLayoutFixtureTests` fails if the manifest or any of the 13 payloads changes.
- `EdlGrammarVectorTests` writes each of the 20 grammar vectors as a default export and requests it through `EdlController` with the real resolver, selector, and `EdlParser`. A valid vector must come back byte for byte; an invalid one is 422. It runs the 7 EDL payloads the same way.
- `SelectionCaseTests` writes each of the 19 selection cases to a temporary media directory with `SelectionCaseMaterializer`. The cases cover personal, default, legacy, malformed, unsupported-action, unreadable, directory, absent, local-ID, and two media-version layouts. Each of the 14 EDL and 5 MCF cases must give its contract outcome from `ExportSelector` and the matching HTTP status and exact payload bytes from its format's route. Each case must also give a server API key, with and without the case's user named, what the case's default path alone decides: the default bytes, `default_invalid`, `default_unreadable`, or `none`. The other format's route must never serve the case's files.
- `ExportLayoutTests` checks `ExportLayout` against the manifest's media names, Jellyfin principals, and POSIX paths.
- `SupplementalSelectionTests` covers rules the frozen v1 manifest has no case for, once as `EdlSupplementalSelectionTests` and once as `McfSupplementalSelectionTests`: a symbolic link (to a file, dangling, or to a directory) at the personal path, a symbolic link at the default path, a named pipe at the personal or default path, which must be refused without waiting for a writer, a personal file swapped for a symbolic link after the check, which must still serve the checked file's bytes, symbolic links at the format folder and its `<id>/` folder, which are allowed, an unsearchable personal directory, an unreadable default, refused media names, and a server API key beside a blocked personal path. These cases live in C# and do not change v1.
- `McfValidationTests` checks the 6 MCF payloads' validity against the manifest, and serves 18 valid and 54 invalid MCF vectors as a user's personal file beside a valid default. Two vectors are Editarr's `valid-1.0.mcf` and `valid-1.1.mcf`, pinned by SHA-256. A valid vector comes back byte for byte; an invalid one is 422 with Editarr's parser message and line, never the default.

## How to get to it (user POV)

A developer changing EDL or MCF selection, MCF validation, or importing a new Editarr contract version runs the unit tier. The plugin implements personal and default selection for both formats against these cases through `IExportFormat`.

## Driving it with verify.py

```bash
python3 .github/skills/verify-jellyfin-edl/scripts/verify.py test --filter FullyQualifiedName~Contract
```

The detailed console log in `dotnet-test.log` lists each case as `<name>: <outcome>, HTTP <status>`.

## Gotchas

- Replace the whole directory from a new Editarr contract version. Do not edit a payload or the manifest, and update `PinnedManifestSha256` and `library.PINNED_MANIFEST_SHA256` together.
- v1 has no MCF grammar vectors, only one invalid MCF payload (an unsupported version), and no unreadable MCF case. MCF validity is pinned instead by `McfValidationTests` and, outside the repository, by MU-12's corpus comparison against Editarr's parser. A v2 fixture could carry shared MCF vectors.
- Unreadable entries are mode 000. The SDK container test runs as the invoking user, so the read fails and the case gives `personal_unreadable`. Run as root, that case fails.
- The materializer writes Unix modes, so these tests do not run on Windows.
- The manifest leaves NaN, Infinity, out-of-range numbers, non-UTF-8 encodings, and non-ASCII whitespace, digits, and letters unpinned.
