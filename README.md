# jellyfin-edl

Jellyfin plugin that serves each signed-in user's EDL for a video, or the item's
default EDL when that user has none.

## What It Does

Given a Jellyfin library item ID and optional media source ID, the plugin:

1. Takes the user from the request's Jellyfin session. API keys and requests
   without a user are refused before any lookup.
2. Resolves the item through Jellyfin's access rules for that user, then the
   requested version among the item's media sources.
3. Reads that version's personal EDL. Only when it is absent, reads the default.
4. Validates the file and returns its exact bytes.

For `/media/Movies/Movie/Movie.mkv` and a user whose Jellyfin ID is `<id>`
(32 lowercase hex digits):

| Scope | Path |
| --- | --- |
| Personal | `/media/Movies/Movie/edl/<id>/Movie.user-<id>.edl` |
| Default | `/media/Movies/Movie/edl/Movie.edl` |
| Legacy, never read | `/media/Movies/Movie/Movie.edl` |

An invalid or unreadable personal file is reported and never replaced by the
default. A symbolic link or directory at either path counts as invalid.

Upgrading from a release that served adjacent `Movie.edl` files: those files
stay on disk but are no longer served. Move them to `edl/Movie.edl`, or export
again from Editarr, to make them available.

## API Contract

- Route: `GET /Plugins/Edl/ByItem/{itemId}?mediaSourceId={versionId}`.
  `mediaSourceId` is optional and defaults to the item's own file. It must be one
  of the item's media sources for the signed-in user.
- Auth: a signed-in user's access token. The user always comes from the token;
  the route takes no user parameter.
- `200 OK`: `text/plain; charset=utf-8`, the file's exact bytes, and an `ETag`
  that differs for each user, item, version, and format.
- `304 Not Modified`: `If-None-Match` matches that same response's `ETag`.
- `401 Unauthorized`: no token.
- `403 Forbidden`: an API key, with or without a user named anywhere in the request.
- `404 Not Found`: no item, version, or local file visible to the user, or no
  personal or default file.
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

Run authenticated endpoint checks with a real item ID. The token must be a signed-in user's access token, for example `AccessToken` from `POST /Users/AuthenticateByName`. An API key gets 403.

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
- The plugin checks the entry type before opening it, so a symbolic link swapped in
  during that window is followed once. A named pipe at an export path is not
  detected before opening and blocks that request.
