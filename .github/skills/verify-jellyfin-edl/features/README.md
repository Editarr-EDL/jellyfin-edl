# Feature map

Each file describes one user-facing feature, how to reach it, how to drive it with `verify.py`, and its gotchas.

| Feature | File | Tier | Status |
| --- | --- | --- | --- |
| Personal and default EDL by item | [edl-by-item.md](edl-by-item.md) | Live and unit | Supported. `verify.py run` drives it with two users and an API key. |
| Export-layout contract fixtures | [export-layout-contract.md](export-layout-contract.md) | Unit | Grammar vectors and the 14 EDL selection cases run against the plugin. MCF cases are planned inputs. |

Planned features have no live recipe yet.

- MCF selection. The plugin has no MCF route.

Add a row and a file when one of them ships, and extend `drive` instead of writing a new runner.
