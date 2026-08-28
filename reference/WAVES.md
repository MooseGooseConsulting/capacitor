# The wave workflow

## Status of this document: a hypothesis, not an instruction

This decomposition was written by an agent that had read parts of the client, driven the
live console for twenty minutes, and never run a single import. **Treat it as a
well-informed starting proposal, not as your plan.**

Your first task is to interrogate it and produce your own. Then say where yours differs
and why. If you agree with it entirely, say that too — but only after actually
challenging it, because agreement arrived at cheaply is the failure mode this document
exists to avoid.

Three different kinds of claim are mixed in here, and they deserve very different levels
of deference:

| | status |
|---|---|
| **Constraints** — the invariants in §Wave 3 and in `SURFACE.md` | **Hard.** Evidence-backed, read from the client's own source and comments. Don't relitigate; verify cheaply if you like. |
| **Gates** — what counts as progress | **Strong.** Each is demonstrable and hard to fake, which is their whole point. Propose better ones if you have them. |
| **Sequence** — six waves in this order, split this way | **Weak. This is a guess.** Yours will likely be better after Wave 1. |

Honest confidence, wave by wave:

- **Wave 1 (ground truth first)** — high. Nothing sensible happens before the analytics
  schema is read and probe sessions are captured.
- **Wave 2 (connect the halves before anything clever)** — high. Integration is the risk,
  and here it's mostly plumbing that already exists.
- **Waves 3–5 (model → breadth → console)** — **low.** Whether the model precedes
  breadth, whether the console waits for the third vendor, whether live capture belongs
  where I put it: these are guesses made before anyone ran anything.
- **Wave 6 (fourth vendor)** — high, because it tests the premise of the whole job rather
  than the quality of the work.

**The two-agent seam is also a guess.** I've proposed Client against Server because they
meet at eight routes and one file of wire types, both fully known before either starts.
Ingest-vs-model, or a split by vendor, might serve better. Decide for yourselves.

Each wave below ends at a **gate** — something demonstrable, not a document. Whatever
sequence you land on, keep that property.

---

## The method, stated correctly

You are not diffing files. You cannot mechanically diff raw JSONL against a rendered UI.

What you are doing is **establishing the standard from observation, then building to
match it**:

1. Observe kcap's behaviour for a specific real session — its events, its turns, its
   token accounting, its rendered transcript, its analytics rows.
2. Write that down as an executable expectation: a **conformance test** that asserts what
   the output must be for that input.
3. Build until the test passes.

The target is a **duplicate that matches**, verified per session, per vendor, per field.
The live kcap instance is the oracle. Where kcap is silent or contradictory, decide, and record the
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

**Client agent** cuts kcap coupling (§7 of SURFACE.md): telemetry, hosted URL
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

**Gate:** for one real Claude session, your turns and events match kcap's for that
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
`reference/ui-assets/components.css` shows how kcap layers on top of it.

Minimum: a session list, and a session detail with **Transcript** and **Trace**. Those two
tabs are what proves the model is real — everything else is reporting.

Use the downloaded fonts and favicons as placeholders. They are Kurrent's marks and get
replaced before this leaves the org.

**Gate:** open a session you captured yourself and read it.

---

## Wave 6 — Prove the gate is gone

Add a **fourth agent vendor that kcap does not support**, end to end: discovery, normalizer,
live hook, rendered in the console.

This is the acceptance test for the entire premise. The reason for the whole job is that
kcap's closed normalizer selector made new vendors impossible from the client side.
If a fourth vendor lands in a day, the premise held.

**Gate:** a coding agent kcap cannot record, recorded.

---

## Standing rules

- **This is a brief, not a checklist.** If reality diverges from a wave, re-plan the
  wave — don't force the plan.
- **Measure before you design.** If you're about to assume something about kcap's
  behaviour, check whether a tool call or a browser click would just tell you.
- **The live kcap instance is the oracle.** "It compiles" is not done. "It matches kcap for this
  session" is done.
- **Write down what you learn as you learn it**, in `reference/`. The failure mode that
  killed previous attempts at this was knowledge living in one agent's context and dying
  there.
- **Never assert a decision you didn't verify.** Record the evidence with the rationale,
  or mark it explicitly as an assumption. Documents asserting unfounded decisions are how
  the earlier attempts went wrong.
- **Don't touch `LICENSE.md` or the copyright notices.** Everything else in the inherited
  source is yours to change.
