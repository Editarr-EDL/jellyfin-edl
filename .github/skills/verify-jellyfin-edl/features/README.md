# Feature map

Each file describes one user-facing feature, how to reach it, how to drive it with `verify.py`, and its gotchas.

| Feature | File | Tier | Status |
| --- | --- | --- | --- |
| Personal and default EDL by item | [edl-by-item.md](edl-by-item.md) | Live and unit | Supported. `verify.py run` drives it with two users and an API key. |
| Personal and default MCF by item | [mcf-by-item.md](mcf-by-item.md) | Live and unit | Supported. `verify.py run` drives it beside EDL with the same users and API key. |
| Export-layout contract fixtures | [export-layout-contract.md](export-layout-contract.md) | Unit | EDL grammar vectors, MCF validation vectors, and all 19 selection cases run against the plugin. |

No feature is planned without a recipe. Add a row and a file when one ships, and extend `drive` instead of writing a new runner.
