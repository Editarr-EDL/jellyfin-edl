# jellyfin-edl

Jellyfin plugin that serves each signed-in user's EDL for a video, or the item's
default EDL when that user has none. A server API key gets only the default EDL.

## What It Does

Given a Jellyfin library item ID and optional media source ID, the plugin:

1. Takes the user from the request's Jellyfin session, or treats a server API key
   as the server, even when the request names a user. Requests with neither are
   refused before any lookup.
2. Resolves the item through Jellyfin's access rules for that user, or across
   every library for an API key, then the requested version among the item's
   media sources.
3. For a user, reads that version's personal EDL and, only when it is absent,
   the default. For an API key, reads only the default.
4. Validates the file and returns its exact bytes.

For `/media/Movies/Movie/Movie.mkv` and a user whose Jellyfin ID is `<id>`
(32 lowercase hex digits):

| Scope | Path |
| --- | --- |
| Personal | `/media/Movies/Movie/edl/<id>/Movie.user-<id>.edl` |
| Default | `/media/Movies/Movie/edl/Movie.edl` |
| Legacy, never read | `/media/Movies/Movie/Movie.edl` |

An invalid or unreadable personal file is reported and never replaced by the
default. A symbolic link or directory at either file path counts as invalid.
Only the final file is checked: `edl/` and `edl/<id>/` may be symbolic links to
folders elsewhere.

An API key's response depends only on the default file. It never reads a personal
file, so it neither returns personal bytes nor reveals whether personal files exist.

Upgrading from a release that served adjacent `Movie.edl` files: those files
stay on disk but are no longer served. Move them to `edl/Movie.edl`, or export
again from Editarr, to make them available.

## API Contract

- Route: `GET /Plugins/Edl/ByItem/{itemId}?mediaSourceId={versionId}`.
  `mediaSourceId` is optional and defaults to the item's own file. It must be one
  of the item's media sources visible to the requester.
- Auth: a signed-in user's access token, or a server API key. The user always
  comes from the token; the route takes no user parameter, and `userId` in the
  query is ignored.
- `200 OK`: `text/plain; charset=utf-8`, the file's exact bytes, and an `ETag`
  that differs for each user or API key, item, version, and format.
- `304 Not Modified`: `If-None-Match` matches that same response's `ETag`. A
  user's `ETag` never matches an API key's response, or the reverse.
- `401 Unauthorized`: no token.
- `403 Forbidden`: an authenticated request with neither a user nor an API key.
- `404 Not Found`: no item, version, or local file visible to the requester, or no
  personal or default file. For an API key, no default file, whatever personal
  files exist.
- `422 Unprocessable Entity`: the selected file is invalid, with JSON diagnostics.
- `500 Internal Server Error`: the selected file exists but cannot be read.
- Every response except Jellyfin's own 401 carries `Cache-Control: private, no-cache`.

Example 422 response shape:

```json
{
	"Diagnostics": [
		{
			"LineNumber": 3,
			"Message": "Unsupported action 'skip'"
		}
	]
}
```

## Supported EDL Variants

The validator currently supports:

- Whitespace-delimited rows with at least 3 fields: `start end action`
- Numeric actions: `0`, `1`, `2`, `3`
- Named actions: `cut`, `mute`, `scene`, `commercial`
- Comment lines starting with `#`, `;`, or `//`
- Blank lines

Validation rules:

- `start` and `end` must parse as invariant-culture numbers.
- `end` must be greater than `start`.
- Action token must be in the supported action set.

## Project Layout

- `src/Jellyfin.Plugin.Edl/`: plugin implementation
- `tests/Jellyfin.Plugin.Edl.Tests/`: unit tests
- `PLAN.md`: wave-based execution plan

## Build and Test (Docker)

Use Docker if local dotnet is not installed.

Build image:

```bash
docker build -t jellyfin-edl-dev -f Dockerfile.dev .
```

Build plugin project:

```bash
docker run --rm -v "$PWD:/workspace" -w /workspace mcr.microsoft.com/dotnet/sdk:10.0 \
	dotnet build ./src/Jellyfin.Plugin.Edl/Jellyfin.Plugin.Edl.csproj -c Release
```

Run tests:

```bash
docker run --rm -v "$PWD:/workspace" -w /workspace mcr.microsoft.com/dotnet/sdk:10.0 \
	dotnet test ./tests/Jellyfin.Plugin.Edl.Tests/Jellyfin.Plugin.Edl.Tests.csproj -c Release
```

## Integration Harness (Docker Compose)

Stage plugin artifacts and start Jellyfin:

```bash
./scripts/stage-plugin.sh
docker compose up -d
```

Run baseline smoke checks (Jellyfin up, plugin seen in logs, route reachable with auth enforced):

```bash
./scripts/jellyfin-smoke.sh
```

Create local media fixtures for fast API-path checks:

```bash
./scripts/create-fixtures.sh
```

The fixture script creates `dev/media` with valid/missing/invalid default EDL scenarios and a short setup guide.

Run authenticated endpoint checks with a real item ID. Use a signed-in user's access token, for example `AccessToken` from `POST /Users/AuthenticateByName`, to check personal files. An API key gets only the default file.

```bash
JELLYFIN_USER_TOKEN="<user_access_token>" \
JELLYFIN_ITEM_ID="<library_item_guid>" \
EXPECTED_STATUS="200" \
./scripts/jellyfin-smoke.sh
```

Optional negative-path checks:

```bash
JELLYFIN_USER_TOKEN="<user_access_token>" JELLYFIN_ITEM_ID="<guid_without_edl>" EXPECTED_STATUS="404" ./scripts/jellyfin-smoke.sh
JELLYFIN_USER_TOKEN="<user_access_token>" JELLYFIN_ITEM_ID="<guid_with_invalid_edl>" EXPECTED_STATUS="422" ./scripts/jellyfin-smoke.sh
```

Run the full 200/404/422 matrix in one command:

```bash
JELLYFIN_USER_TOKEN="<user_access_token>" \
JELLYFIN_ITEM_ID_VALID="<valid_guid>" \
JELLYFIN_ITEM_ID_MISSING_EDL="<missing_edl_guid>" \
JELLYFIN_ITEM_ID_INVALID_EDL="<invalid_edl_guid>" \
./scripts/run-edl-matrix.sh
```

Create a distributable plugin zip:

```bash
./scripts/package-plugin.sh
```

This writes output to `artifacts/packages/Jellyfin.Plugin.Edl.zip`.

Stop services:

```bash
docker compose down
```

## Notes and Limitations

- Only EDL is served. MCF selection follows the same layout and is not implemented yet.
- Filesystem path details are logged server-side but not returned in API errors.
- On Linux x64 and arm64 (glibc 2.27 or later, or musl) and on macOS x64 and arm64, the plugin opens an
  export file without following a final symbolic link and without waiting on a named pipe, checks that
  the open file is a regular file, and reads that same open file. A link, directory, or named pipe at
  the export path returns 422, even when it is swapped in during a request. An entry the server cannot
  open returns 500. On Linux the check calls `fstat`, or `__fxstat` on glibc before 2.33, which does
  not export `fstat`.
- Every other platform checks the entry and then opens it by path: Windows on any architecture, Linux
  on Arm32, ppc64le, or s390x, FreeBSD, and any other system .NET runs on. Someone with write access
  inside the library folder can replace an export file with a symbolic link between the check and the
  open, and the plugin then serves the link's target, which can be any file the server can read, such
  as another user's personal EDL. On the Unix platforms in this list, a named pipe at an export path
  also holds the request open until a writer connects. Windows keeps this behavior by decision; none
  of these platforms is tested.
