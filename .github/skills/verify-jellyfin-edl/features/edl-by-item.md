# EDL by item

A Jellyfin client asks the plugin for the EDL the signed-in user should play with a library video. The plugin takes the user from the session, resolves the item and version through Jellyfin's access rules for that user, then serves the user's personal EDL, or the item's default EDL when the user has none.

## Sub-features

- `GET /Plugins/Edl/ByItem/{itemId}` serves the item's own file. `?mediaSourceId={id}` selects another of the item's media sources (versions).
- For `Movie.mkv` and user `<id>` (32 lowercase hex digits), `edl/<id>/Movie.user-<id>.edl` wins. `edl/Movie.edl` is read only when that personal path is absent. The adjacent `Movie.edl` is never read.
- A valid file returns 200 `text/plain; charset=utf-8` with the file's exact bytes and an `ETag` that differs per user, item, version, and format. `If-None-Match` with that same `ETag` returns 304.
- An invalid selected file, or a directory or symbolic link at its path, returns 422 with a JSON `Diagnostics` array of `LineNumber` and `Message`. An invalid personal file never falls back to the default.
- A selected file that exists but cannot be inspected or read returns 500 and never falls back.
- A request without a token returns 401. An API key returns 403, even with a `userId` query parameter. The route takes no user parameter.
- An item or version the user cannot see, a version without a local file, or no personal or default file returns 404.
- Every response from the plugin carries `Cache-Control: private, no-cache`.

## How to get to it (user POV)

Sign in to Jellyfin and call the route with the session's access token in the `Authorization: MediaBrowser ..., Token="..."` header. The item ID is the library item GUID and the media source ID is a `MediaSources[].Id`, for example from `GET /Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path,MediaSources`.

## Driving it with verify.py

`verify.py run` launches Jellyfin 12.0.0 with two ordinary users, an API key, the 14 EDL selection cases, one two-version movie, and one movie in a library user B cannot see. The drive in `scripts/journey.py` requests every case as both users and checks status, exact body hash, `Cache-Control`, and `ETag` behavior against `library.contract_expectation`. `drive.json` records each check.

The unit tier covers the same route in `tests/Jellyfin.Plugin.Edl.Tests/EdlControllerTests.cs`, `Contract/SelectionCaseTests.cs`, `Contract/SupplementalSelectionTests.cs`, and `Contract/EdlGrammarVectorTests.cs`. Those tests run the real resolver, selector, and parser with Jellyfin's managers mocked. They do not prove the live route.

## Gotchas

- Jellyfin assigns user IDs at creation, so launch rewrites the fixture's user IDs in every path. `items.json` and `drive.json` record the live IDs.
- Jellyfin groups `Movie - 1080p.mkv` and `Movie - 2160p.mkv` in a folder named `Movie` into one item. The drive fails its first version check if they are indexed as two items.
- Whether the server can read a mode 000 file depends on its user and the mount. Launch measures it with `cat` in the container. On Docker Desktop for macOS the root server could not read it, so the live drive gets a real 500 for the unreadable case. MU-05 assumed root would read it; run `jfedl-20261022t050137-3a6727` showed `UnauthorizedAccessException` instead.
- A 404 body is JSON problem details, not empty.
- Jellyfin can answer `/System/Info/Public` before startup finishes, and adding the library during the startup scan can cancel that scan without running the requested one. Launch waits for `/health` to report `Healthy` and requests `/Library/Refresh` while the scan task is idle and movies are missing.
- The server logs `Update Plugins` and may contact the default plugin repository. The libraries have internet metadata disabled.
- A named pipe at an export path blocks that request at open, because .NET reports it as a normal file without opening it. No fixture or drive case creates one.
- `docker-compose.yml` names `jellyfin/jellyfin:12.0.0`, which Docker Hub does not publish. The helper uses its own pinned digest and never uses the compose service.
- Verified on Docker Desktop for macOS on arm64 only.
