# EDL by item

A Jellyfin client asks the plugin for the EDL the signed-in user should play with a library video. The plugin takes the user from the session, resolves the item and version through Jellyfin's access rules for that user, then serves the user's personal EDL, or the item's default EDL when the user has none. A server API key, such as a server-side tool's, gets only the item's default EDL.

## Sub-features

- `GET /Plugins/Edl/ByItem/{itemId}` serves the item's own file. `?mediaSourceId={id}` selects another of the item's media sources (versions).
- For `Movie.mkv` and user `<id>` (32 lowercase hex digits), `edl/<id>/Movie.user-<id>.edl` wins. `edl/Movie.edl` is read only when that personal path is absent. The adjacent `Movie.edl` is never read.
- A valid file returns 200 `text/plain; charset=utf-8` with the file's exact bytes and an `ETag` that differs per user or API key, item, version, and format. `If-None-Match` with that same `ETag` returns 304.
- An invalid selected file, or a directory, symbolic link, or named pipe at its path, returns 422 with a JSON `Diagnostics` array of `LineNumber` and `Message`. An invalid personal file never falls back to the default. Only the final file is checked for links. Decided 2026-10-21: `edl/` and `edl/<id>/` may be symbolic links to folders.
- A selected file that exists but cannot be inspected or read returns 500 and never falls back.
- A request without a token returns 401. A server API key sees every library and reads only the default file. It gets the default's bytes, 422 or 500 for an invalid or unreadable default, and 404 when there is no default, whatever personal files exist. A `userId` query parameter changes nothing. The route takes no user parameter. An authenticated request with neither a user nor an API key returns 403.
- An item or version the requester cannot see, a version without a local file, or no eligible file returns 404.
- Every response from the plugin carries `Cache-Control: private, no-cache`.

## How to get to it (user POV)

Sign in to Jellyfin and call the route with the session's access token in the `Authorization: MediaBrowser ..., Token="..."` header. The item ID is the library item GUID and the media source ID is a `MediaSources[].Id`, for example from `GET /Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path,MediaSources`.

## Driving it with verify.py

`verify.py run` launches Jellyfin 12.0.0 with two ordinary users, an API key, the 19 EDL and MCF selection cases plus one plugin-local MCF case, one two-version movie, and one movie in a library user B cannot see. The drive in `scripts/journey.py` requests every case on the EDL route, and on the MCF route, as both users and as the API key, with and without `?userId=`, and checks status, exact body hash, `Cache-Control`, and `ETag` behavior against `library.contract_expectation`. `drive.json` records each check.

The unit tier covers the same route in `tests/Jellyfin.Plugin.Edl.Tests/EdlControllerTests.cs`, `Contract/SelectionCaseTests.cs`, `Contract/SupplementalSelectionTests.cs`, and `Contract/EdlGrammarVectorTests.cs`. Those tests run the real resolver, selector, and parser with Jellyfin's managers mocked. They do not prove the live route.

## Gotchas

- Jellyfin assigns user IDs at creation, so launch rewrites the fixture's user IDs in every path. `items.json` and `drive.json` record the live IDs.
- Jellyfin groups `Movie - 1080p.mkv` and `Movie - 2160p.mkv` in a folder named `Movie` into one item. The drive fails its first version check if they are indexed as two items.
- Whether the server can read a mode 000 file depends on its user and the mount. Launch measures it with `cat` in the container. On Docker Desktop for macOS the root server could not read it, so the live drive gets a real 500 for the unreadable case. MU-05 assumed root would read it; run `jfedl-20261022t050137-3a6727` showed `UnauthorizedAccessException` instead.
- A 404 body is JSON problem details, not empty.
- Jellyfin can answer `/System/Info/Public` before startup finishes, and adding the library during the startup scan can cancel that scan without running the requested one. Launch waits for `/health` to report `Healthy` and requests `/Library/Refresh` while the scan task is idle and movies are missing.
- The server logs `Update Plugins` and may contact the default plugin repository. The libraries have internet metadata disabled.
- On Linux x64 and arm64 and on macOS x64 and arm64, `Services/ExportFile.cs` opens the final entry with `O_NOFOLLOW | O_NONBLOCK`, checks that descriptor for a regular file, and the selector reads the same descriptor. A link or named pipe swapped in during a request is still refused. The flag values and `st_mode` offset differ per OS and architecture. Linux binds `fstat`, or `__fxstat` with glibc's `_STAT_VER` (1 on x64, 0 on arm64) when glibc is older than 2.33 and exports no `fstat`. `verify.py test --old-glibc` runs the suite on glibc 2.28 for the host architecture, and `ExportFileTests` calls the `__fxstat` binding directly on modern glibc. `SupplementalSelectionTests` covers pipes and a swap after the check; the live drive creates neither. Because a directory must be opened to be checked, a directory the server cannot read returns 500, not 422.
- Every other platform checks the entry, then reopens it by path: Windows on any architecture, Linux on Arm32, ppc64le, or s390x, FreeBSD, and any other system .NET runs on. A link swapped in between is followed, so someone with write access inside the library folder can redirect the read to any file the server can read. On the Unix platforms among these, a named pipe at the path blocks the request until a writer connects. Decided 2026-10-21: Windows keeps this behavior and the README documents it. None of these platforms is tested.
- `docker-compose.yml` names `jellyfin/jellyfin:12.0.0`, which Docker Hub does not publish. The helper uses its own pinned digest and never uses the compose service.
- Verified on Docker Desktop for macOS on arm64 only.
