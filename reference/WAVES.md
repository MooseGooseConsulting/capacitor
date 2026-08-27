# The wave workflow

How to run this job. Six waves. Each ends at a **gate** — something demonstrable, not a
document — and you do not enter the next wave until the gate is met.

Within a wave, plan and reason before you build. Between waves, re-plan: what you learn
in wave *n* should change wave *n+1*, and a wave plan written before wave 1 will be
wrong.

**Two agents.** The seam is **Client** against **Server**. They meet at the eight routes
and the wire types in `src/Capacitor.Cli.Core/Models.cs` — both fully known before either
starts, which is why the middle needs almost no negotiation. Agree the contract in
Wave 1 and then work independently against it.

---

## The method, stated correctly

You are not diffing files. You cannot mechanically diff raw JSONL against a rendered UI.

What you are doing is **establishing the standard from observation, then building to
match it**:

1. Observe upstream's behaviour for a specific real session — its events, its turns, its
   token accounting, its rendered transcript, its analytics rows.
2. Write that down as an executable expectation: a **conformance test** that asserts what
   the output must be for that input.
3. Build until the test passes.

The target is a **duplicate that matches**, verified per session, per vendor, per field.
Upstream is the oracle. Where upstream is silent or contradictory, decide, and record the
decision as an assumption.

---

## Wave 1 — Ground truth and the contract

**Both agents. Nothing gets built this wave.**

- Read `reference/SURFACE.md`.
- Pull `get_analytics_schema` in full (~87k chars, 32 views). Read all of it.
- Pick **three probe sessions** on the live server whose raw files you also have on
  disk — ideally one Claude, one Codex, one with subagents. For each, capture: the raw
  input files, and the full normalized output (`list_turns`, `get_turn`,
  `get_session_transcript`, plus the console's Events and Trace tabs).
- Read the client's ingestion path end to end: `IImportSource`, `SessionImporter`,
  `AgentHookPoster`, `Models.cs`, `WatchCommand`.
- **Settle the open question by measurement:** POST a batch tagged with a vendor the
  server has never seen. Does it reject, or silently accept lines it cannot normalize?
  Use a throwaway session and hide it afterwards. The answer sets how defensive the
  normalizer plane must be.
- Write down the wire contract as a **shared, frozen interface document** both agents
  build against.

**Gate:** the contract document exists; three probe sessions are captured input-and-output
side by side; the unknown-vendor behaviour is known, not assumed.

---

## Wave 2 — The skeleton meets in the middle

**Server agent** builds the eight routes over a real store, doing no normalization —
accept batches, persist raw lines keyed by `(session_id, agent_id, line_number)`, answer
`last-line` honestly.

**Client agent** cuts the upstream coupling (§7 of SURFACE.md): telemetry, hosted URL
defaults, update check, feedback. Adds a local profile with `auth_provider: null` so the
client posts unauthenticated. Gets the client building and running against localhost.

**Gate — the one that de-risks everything else:** `kcap import` against the local server
completes, and `last-line` afterwards reports the right watermark. Raw lines land. No
normalization yet. **The two halves are connected.**

Do this before anything clever. It is the integration risk, and it is nearly all
plumbing that already exists.

---

## Wave 3 — The canonical model

**Server agent leads; this is the critical path.**

Design the event / turn / subagent model from the probe captures and the 32 views. It
must satisfy, non-negotiably:

- Idempotent on `(session_id, agent_id, line_number)` — position-addressed, not
  content-addressed.
- A transcript may arrive **before** its session-start; create an owner-only placeholder
  that a later start reconciles.
- Subagents are **flat**, one level.
- Every event carries model, token delta, cache read/write, cost, timestamp.
- Turns roll up from events with duration and tool count; non-turn entries interleave.
- Sessions chain (`previous_session_id` / `next_session_id`).
- Session ids stored dashless.

Then write the **Claude normalizer** and prove it against probe session #1.

**Client agent** meanwhile: prove the resend invariants against the wave-2 skeleton —
import twice, drain a spool, force a `--reimport`, and confirm no duplicates.

**Gate:** for one real Claude session, your turns and events match upstream's for that
same session — turn count, per-turn tool counts, token totals, event ordering. Not "looks
similar." Matches.

---

## Wave 4 — Breadth, and the feature cut

**Server agent:** Codex and OpenCode normalizers. Codex proves parent-thread subagent
linkage; OpenCode proves the SQLite substrate. Three vendors is the proof that the
normalizer plane is a plane and not a Claude special case.

**Client agent:** the live path. Hooks firing into the local server, the SignalR watcher
streaming, spool-and-drain under a deliberately killed server.

**Both, in parallel with the above — the feature cut:**

1. Enumerate the **complete** feature surface from evidence: console navigation, the six
   per-session tabs, `kcap --help`, the 32 views, the MCP tool list, the route inventory.
2. Propose in / out / later, one line of justification and cost each.
3. **Stop and get the operator's confirmation before building against it.** This is the
   only place in the whole job you should block and wait.

Do not assume you know which features are cheap. The operator's read on difficulty will
differ from yours, and being wrong either way is expensive.

**Gate:** three vendors normalized and verified end to end; live capture working; the cut
approved.

---

## Wave 5 — The console

Blazor + MudBlazor. MudBlazor is MIT and gives you the component vocabulary for free;
`reference/ui-assets/components.css` shows how upstream layers on top of it.

Minimum: a session list, and a session detail with **Transcript** and **Trace**. Those two
tabs are what proves the model is real — everything else is reporting.

Use the downloaded fonts and favicons as placeholders. They are Kurrent's marks and get
replaced before this leaves the org.

**Gate:** open a session you captured yourself and read it.

---

## Wave 6 — Prove the gate is gone

Add a **fourth vendor that upstream does not support**, end to end: discovery, normalizer,
live hook, rendered in the console.

This is the acceptance test for the entire premise. The reason for the whole job is that
upstream's closed normalizer selector made new vendors impossible from the client side.
If a fourth vendor lands in a day, the premise held.

**Gate:** a coding agent kcap cannot record, recorded.

---

## Standing rules

- **This is a brief, not a checklist.** If reality diverges from a wave, re-plan the
  wave — don't force the plan.
- **Measure before you design.** If you're about to assume something about upstream's
  behaviour, check whether a tool call or a browser click would just tell you.
- **Upstream is the oracle.** "It compiles" is not done. "It matches upstream for this
  session" is done.
- **Write down what you learn as you learn it**, in `reference/`. The failure mode that
  killed previous attempts at this was knowledge living in one agent's context and dying
  there.
- **Never assert a decision you didn't verify.** Record the evidence with the rationale,
  or mark it explicitly as an assumption. Documents asserting unfounded decisions are how
  the earlier attempts went wrong.
- **Don't touch `LICENSE.md` or the copyright notices.** Everything else in the inherited
  source is yours to change.
