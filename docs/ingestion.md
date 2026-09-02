# Session and transcript ingestion

## Goal

Make session data useful after it reaches the backend: retain source records,
derive a stable canonical event stream, preserve its order and accounting, and
project it into the dashboard's transcript, events, trace, and session-card
views.

The intended source inventory is Claude, Codex, Cursor, Copilot, Gemini, Kiro,
Kimi, Pi, OpenCode, and Antigravity. A source is not called supported merely
because it appears in a catalog: it needs a real fixture, a normalizer, and a
passing idempotent PostgreSQL integration test. Unsupported or unrecognized
payloads fail visibly; they are never silently treated as successful imports.

## Pipeline and fidelity

Lifecycle hooks establish session and subagent metadata. Transcript hooks
carry ordered raw lines, source line numbers, agent identity, vendor, and
origin (`live` or historical import). A normalizer may emit assistant content,
thinking, user messages, tool calls/results, background-command state, usage,
errors, and metadata from one source line.

Persist both the raw source payload and normalized records. Normalized records
retain source vendor, session and agent IDs, line number, logical sequence,
timestamp, model, token/cache/cost deltas, tool input/output and exit state,
error state, and content. This is what lets Events show provenance while
Transcript and Trace offer useful enriched views.

Current recovery code establishes Claude, Universal ACP dialects, and
Antigravity normalization. Native Codex, Kiro, and Kimi coverage is recovery
work, not a completed capability claim. Vendor adapters are registered in the
same import and live-hook composition points so historical and live paths do
not disagree about support.

## Ordering and replay rules

- A canonical event key is `(session_id, agent_id, line_number, logical_seq)`.
  `logical_seq` orders the multiple outputs derived from one source line.
- Replaying an identical batch produces no duplicate canonical events or
  inflated rollups.
- A source line is acknowledged only after all of its canonical events and
  projections persist successfully.
- The stored watermark is the contiguous processed source-line prefix, not the
  greatest line number observed. Gaps and malformed batches fail hard.
- Event persistence and watermark advancement occur in one PostgreSQL
  transaction.

## Read projections

The session list/search projection supplies title, repository/PR, owner,
vendor/model, state, counts, totals, and recency. Transcript is the
conversation projection; Events is the ordered canonical evidence; Trace
groups turn-level accounting while retaining non-turn rows. All projections
derive from the same persisted event stream so the dashboard does not invent
inconsistent totals.
