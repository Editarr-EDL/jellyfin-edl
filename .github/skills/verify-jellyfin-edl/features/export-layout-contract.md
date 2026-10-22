# Export-layout contract fixtures

Editarr writes EDL and MCF files that this plugin serves. The two repositories share one versioned contract, `editarr-export-layout-1`, so both sides test the same bytes.

## Sub-features

- `tests/fixtures/export-layout-v1/` is a byte-exact copy of Editarr's `tests/fixtures/multi-user/export-layout-v1`. `provenance.json` names the source commit and the manifest SHA-256 `c3063858e3ecf2f8982c66a1140f52ab7d806e737ff5c7854b413cdecccf987e`. `.gitattributes` keeps Git from changing its line endings.
- `ExportLayoutFixtureTests` fails if the manifest or any of the 13 payloads changes.
- `EdlGrammarVectorTests` writes each of the 20 grammar vectors as a default export and requests it through `EdlController` with the real resolver, selector, and `EdlParser`. A valid vector must come back byte for byte; an invalid one is 422. It runs the 7 EDL payloads the same way.
- `SelectionCaseTests` writes each of the 19 selection cases to a temporary media directory with `SelectionCaseMaterializer`. The cases cover personal, default, legacy, malformed, unsupported-action, unreadable, directory, absent, local-ID, and two media-version layouts. Each of the 14 EDL cases must give its contract outcome from `ExportSelector` and the matching HTTP status and exact payload bytes from the controller. Each EDL case must also give a server API key, with and without the case's user named, what the case's default path alone decides: the default bytes, `default_invalid`, `default_unreadable`, or `none`.
- `ExportLayoutTests` checks `ExportLayout` against the manifest's media names, Jellyfin principals, and POSIX paths.
- `SupplementalSelectionTests` covers rules the frozen v1 manifest has no case for: a symbolic link (to a file, dangling, or to a directory) at the personal path, a symbolic link at the default path, a named pipe at the personal or default path, which must be refused without waiting for a writer, a personal file swapped for a symbolic link after the check, which must still serve the checked file's bytes, symbolic links at `edl/` and `edl/<id>/`, which are allowed, an unsearchable personal directory, an unreadable default, refused media names, and a server API key beside a blocked personal path. These cases live in C# and do not change v1.

## How to get to it (user POV)

A developer changing EDL selection or importing a new Editarr contract version runs the unit tier. The plugin implements personal and default EDL selection against these cases. MU-12 adds MCF through `IExportFormat`.

## Driving it with verify.py

```bash
python3 .github/skills/verify-jellyfin-edl/scripts/verify.py test --filter FullyQualifiedName~Contract
```

The detailed console log in `dotnet-test.log` lists each EDL case as `<name>: <outcome>, HTTP <status>`.

## Gotchas

- Replace the whole directory from a new Editarr contract version. Do not edit a payload or the manifest, and update `PinnedManifestSha256` and `library.PINNED_MANIFEST_SHA256` together.
- The five MCF cases are materialized but not selected yet. MU-12 adds their outcome checks.
- Unreadable entries are mode 000. The SDK container test runs as the invoking user, so the read fails and the case gives `personal_unreadable`. Run as root, that case fails.
- The materializer writes Unix modes, so these tests do not run on Windows.
- The manifest leaves NaN, Infinity, out-of-range numbers, non-UTF-8 encodings, and non-ASCII whitespace, digits, and letters unpinned.
