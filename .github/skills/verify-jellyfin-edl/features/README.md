# Feature map

Each file describes one user-facing feature, how to reach it, how to drive it with `verify.py`, and its gotchas.

| Feature | File | Tier | Status |
| --- | --- | --- | --- |
| EDL by item | [edl-by-item.md](edl-by-item.md) | Live and unit | Supported. `verify.py run` drives it. |
| Export-layout contract fixtures | [export-layout-contract.md](export-layout-contract.md) | Unit | Grammar vectors run against the plugin. Selection cases are planned inputs. |

Planned features have no live recipe yet.

- Personal and default EDL selection for the signed-in Jellyfin user. The plugin still uses the legacy adjacent sidecar.
- MCF selection. The plugin has no MCF route.

Add a row and a file when one of them ships, and extend `drive` instead of writing a new runner.
