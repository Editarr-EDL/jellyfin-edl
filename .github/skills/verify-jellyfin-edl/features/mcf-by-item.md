# MCF by item

A Jellyfin client asks the plugin for the MovieContentFilter (MCF) file the signed-in user should use with a library video. The route mirrors EDL by item: the plugin takes the user from the session, resolves the item and version through Jellyfin's access rules for that user, then serves the user's personal MCF, or the item's default MCF when the user has none. A server API key gets only the default MCF. MCF is selected independently of EDL, so a user can get a personal EDL and the default MCF for the same version.

## Sub-features

- `GET /Plugins/Edl/Mcf/ByItem/{itemId}` serves the item's own file. `?mediaSourceId={id}` selects another of the item's media sources (versions).
- For `Movie.mkv` and user `<id>`, `mcf/<id>/Movie.user-<id>.mcf` wins. `mcf/Movie.mcf` is read only when that personal path is absent. The adjacent `Movie.mcf` is never read.
- A valid file returns 200 `text/vtt; charset=utf-8` with the file's exact bytes and an `ETag` that differs per user or API key, item, version, and format. An EDL `ETag` never matches an MCF response.
- Validity is MCF 1.0.0 or 1.1.0 by Editarr's strict parser rules, listed in the README's Supported MCF Versions section. An invalid file returns 422 with one diagnostic carrying Editarr's message and line, and an invalid personal file never falls back to the default.
- A link, directory, or named pipe at the file path returns 422, and a file that exists but cannot be read returns 500, as for EDL.
- Authorization, the API key's default-only reads, 401, 403, 404, and `Cache-Control: private, no-cache` match EDL by item.

## How to get to it (user POV)

Sign in to Jellyfin and call the route with the session's access token in the `Authorization: MediaBrowser ..., Token="..."` header, exactly as for EDL by item.

## Driving it with verify.py

`verify.py run` requests every case item on the MCF route as well as the EDL route. The live library holds the 5 MCF selection cases, the plugin-local `local-mcf-unreadable-personal-does-not-fall-back`, and the restricted movie's personal MCFs. The drive checks status, `text/vtt`, exact body hash, `Cache-Control`, and `ETag` behavior against `library.contract_expectation` with the route's format, and checks that each route ignores the other format's files.

The unit tier covers the route in `tests/Jellyfin.Plugin.Edl.Tests/McfControllerTests.cs`, `McfValidationTests.cs`, `Contract/SelectionCaseTests.cs`, and `Contract/SupplementalSelectionTests.cs` (`McfSupplementalSelectionTests`).

## Gotchas

- The validator follows Editarr's `scanner/formats/mcf_parser.py` without a media duration, so it does not compare cue times with the runtime. It accepts only ASCII digits, like Editarr since its commit b57d70e, and it skips `urlsplit`'s NFKC and bracketed-host checks on `SOURCE` and `IMDB` URLs. MU-12 measured those URL checks as the only differences, each one a URL the plugin accepts, on a 17,863-document corpus; the corpus and comparison scripts are evidence, not part of the repository.
- The category list in `Services/McfCategories.cs` is copied from Editarr's `category-registry-v1.json`. `McfValidationTests` pins its SHA-256. A registry change in Editarr needs a matching plugin change.
- `text/vtt` is the WebVTT media type. Browsers may treat the response as a caption track.
- Verified on Docker Desktop for macOS on arm64 only.
