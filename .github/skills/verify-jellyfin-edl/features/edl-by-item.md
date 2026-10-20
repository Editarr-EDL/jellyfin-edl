# EDL by item

A Jellyfin client asks the plugin for the EDL of a library video. The plugin resolves the item's primary media path, reads the adjacent `<stem>.edl`, validates it, and returns it.

## Sub-features

- `GET /Plugins/Edl/ByItem/{itemId}` returns 200 `text/plain` with the file's lines for a valid sidecar.
- A sidecar that fails `EdlParser.Validate` returns 422 with JSON `diagnostics`.
- A missing item, a non-video item, an item without a local path, or a missing sidecar returns 404.
- A request without a Jellyfin token returns 401.

## How to get to it (user POV)

Sign in to Jellyfin and call the route with the session's access token in the `Authorization: MediaBrowser ..., Token="..."` header. The item ID is the library item GUID, for example from `GET /Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path`.

## Driving it with verify.py

`verify.py run` launches Jellyfin 12.0.0 with 14 synthetic movies, one per EDL selection case in `tests/fixtures/export-layout-v1`. It drives the route once without a token and once per movie with the administrator token. `drive.json` records each status and body hash against the legacy expectation.

The unit tier covers the same route in `tests/Jellyfin.Plugin.Edl.Tests/EdlControllerTests.cs` and `Contract/SelectionCaseTests.cs`. Those tests call the controller directly. They do not prove the live route.

## Gotchas

- The plugin ignores personal and default files. Cases whose planned contract outcome is `personal` or `default` return the legacy file or 404 today. That is the expected baseline until personal selection ships.
- Jellyfin in this image runs as root. A mode 000 fixture file is still readable inside the container, so the live tier cannot produce `personal_unreadable` without a non-root server.
- The controller joins lines with a newline. It drops a trailing newline and converts CRLF. The exact body hash matches only payloads with LF line endings and no trailing newline, such as `edl-legacy`.
- A 404 body is JSON problem details, not empty.
- The server logs `Update Plugins` and may contact the default plugin repository. The library itself has internet metadata disabled.
- `docker-compose.yml` names `jellyfin/jellyfin:12.0.0`, which Docker Hub does not publish. The helper uses its own pinned digest and never uses the compose service.
- Verified on Docker Desktop for macOS on arm64 only.
