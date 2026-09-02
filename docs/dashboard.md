# Web dashboard: Sessions first

The target is the captured Blazor + MudBlazor web console described by
[`reference/SURFACE.md`](../reference/SURFACE.md) and
[`reference/ui-assets/README.md`](../reference/ui-assets/README.md). We build
the Sessions surface from its behavioral and structural evidence; we do not
turn the Avalonia desktop client into a replacement dashboard.

## First delivery

The first vertical slice is enriched session and transcript data, historical
and live, rendered from PostgreSQL. It deliberately excludes agent launch,
vendor CLI execution, terminal controls, Flows, and Work Items.

Top-level navigation remains visible as captured: **Sessions**, **Agents**,
**Insights**, **Flows**, and **Work Items**. Only Sessions is brought to a
working, data-backed state in this slice. Empty or unimplemented sections must
not imply that their remote actions exist.

## Sessions layout

Sessions is a master/detail view:

- The left navigation rail groups `All`, `My projects`, and `Other repos` by
  repository owner, with the captured global navigation and search affordance.
- The session list uses cards containing status, title, work-item chip when
  available, repository and PR, vendor and model, input/output/total tokens,
  context occupancy, diff, tool and error counts, and relative time.
- Repository selection and the captured all-users selector filter the same
  session projection. Global search matches session titles and transcript text,
  not vendor labels merely because the vendor is displayed.
- A selected session exposes share, copy-link, refresh, delete, status, and
  owner actions only when backed by a real server capability. Unsupported
  mutation actions are not decorative controls.

## Session detail contract

All six captured, URL-addressable tabs are required:

| Tab | Data shown |
| --- | --- |
| **Overview** | Session lifecycle, summary, repository/PR, owner, model/vendor, counts, and status |
| **Transcript** | Rendered conversation with model, token flow, cache read/write, cost, timestamp, and duration per message |
| **Events** | Ordered typed canonical event stream, including expandable tool input/output and source provenance |
| **Trace** | Turn rollups with start, duration, token flow, tool count, and first-class interleaved non-turn events |
| **Evaluation** | Persisted evaluation/judge results, or an explicit unavailable state until evaluation exists |
| **Details** | Complete persisted session metadata and ingestion state |

The dashboard updates the same projections as transcript ingestion occurs. A
live update cannot bypass persistence: an event is visible only after its
transaction and watermark state are durable.

## Reference and branding boundary

The captured UI is evidence, not source material to ship unchanged. MudBlazor
is reusable under its own license, but the captured Solina typeface, favicons,
Kurrent marks, and product naming are not ours. Keep the captures only as
internal reference and replace protected branding before any external display.
