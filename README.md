# jellyfin-edl

Jellyfin plugin that resolves and serves sidecar EDL files for video items.

## What It Does

Given a Jellyfin library item ID, the plugin performs this pipeline:

1. Resolve the item from `ILibraryManager`.
2. Confirm it is a video item.
3. Get the primary media path.
4. Replace the media extension with `.edl`.
5. Check whether that sidecar file exists.
6. Validate and parse EDL lines.
7. Return raw EDL content when valid.

## API Contract

- Route: `GET /Plugins/Edl/ByItem/{itemId}`
- Auth: authenticated users required
- Success: `200 OK`, `text/plain`, body is the raw EDL file content
- Missing or not eligible item: `404 Not Found`
- Invalid EDL content: `422 Unprocessable Entity` with JSON diagnostics

Example 422 response shape:

```json
{
	"diagnostics": [
		{
			"lineNumber": 3,
			"message": "Unsupported action 'skip'"
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

The fixture script creates `dev/media` with valid/missing/invalid EDL scenarios and a short setup guide.

Run authenticated endpoint checks with a real item ID:

```bash
JELLYFIN_API_KEY="<api_key>" \
JELLYFIN_ITEM_ID="<library_item_guid>" \
EXPECTED_STATUS="200" \
./scripts/jellyfin-smoke.sh
```

Optional negative-path checks:

```bash
JELLYFIN_API_KEY="<api_key>" JELLYFIN_ITEM_ID="<guid_without_edl>" EXPECTED_STATUS="404" ./scripts/jellyfin-smoke.sh
JELLYFIN_API_KEY="<api_key>" JELLYFIN_ITEM_ID="<guid_with_invalid_edl>" EXPECTED_STATUS="422" ./scripts/jellyfin-smoke.sh
```

Run the full 200/404/422 matrix in one command:

```bash
JELLYFIN_API_KEY="<api_key>" \
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

- No caching is implemented yet.
- Only `.edl` sidecars are handled.
- Filesystem path details are logged server-side but not returned in API errors.
