# /goal — Stand up the whole Capacitor stack, ours, end to end

You are in a repository that already contains a complete, working, source-available
client for a system whose server half is closed. Your job is to build that server half,
cut the client loose from its vendor, and finish with a self-contained stack we own top
to bottom.

**The objective, stated plainly:** every place an agent runs across the operator's fleet
records into one corpus. This laptop is node 1 of N, not the system. Read
`reference/FLEET.md` first — it reframes the other two documents and wins where they
disagree.

**Read these three documents before doing anything else:**

- `reference/FLEET.md` — the fleet objective and what it changes: a networked server,
  headless machine credentials, a machine dimension the upstream model does not have,
  and cross-node identity problems that do not exist on one machine.

- `reference/SURFACE.md` — everything observable about the system, and which half we
  already have. Includes the full wire contract, the canonical model as observed on a
  live instance, the 32 analytics views, the console's stack, and the list of upstream
  couplings to cut.
- `reference/WAVES.md` — a **proposed** decomposition into six waves, each ending at a
  demonstrable gate. Read its "method" section carefully; it is not what you'd assume.
  And read its opening honestly: the constraints in it are hard, the gates are strong,
  **the sequence is a guess** made by someone who had never run an import.

Those two files are the brief. Everything below is orientation.

## Your first task is to disagree with the plan

Before building anything: reason over the goal yourself. Work out what this job actually
decomposes into, in what order, split how — from the evidence, not from `WAVES.md`.

Then compare. State where your decomposition differs from the proposal and why. Adopt
yours where you can defend it. Adopting the proposal wholesale is a legitimate outcome —
but only after you've genuinely tried to break it, because cheap agreement is the exact
failure this brief exists to prevent.

Three things are not yours to re-decide: the **invariants** (evidence-backed, read from
the client's own source), the **licensing constraints**, and the rule that **upstream is
the oracle**. Everything else — sequence, split, milestones, the two-agent seam — is
yours.

Re-plan between waves too. What you learn in one should change the next, and any plan
written before the first import will be partly wrong.

---

## What this system is

It records AI coding-agent sessions. Agents — Claude Code, Codex, Cursor, Copilot,
Gemini, Kiro, OpenCode, Antigravity, Pi, Kimi — write their conversations to disk. The
system captures them two ways: **live**, through hooks the agent itself fires, and
**historically**, by importing files already on disk. It normalizes them into a canonical
event/turn/subagent model, stores them, and serves a console plus an MCP query surface
over the result.

## The situation, and why this job exists

The client is in this repo and it is excellent. The server is closed. The consequence
that motivates everything:

> The client tags each batch of transcript lines with a `vendor` string. The server
> routes that string to a per-vendor normalizer behind a closed `INormalizerSelector`.
> You can write the client side of a new coding agent in an afternoon — and it goes
> nowhere, because you cannot write the normalizer.

**Own the server and that gate ceases to exist.** That is the point of the whole job, and
Wave 6 is its acceptance test.

## Why you start from this code instead of rewriting it

The client is the tedious, invariant-dense half: ten vendors' on-disk discovery, drain
throttling across short-lived processes, per-child watermark resume, subagent
interleaving, fail-closed child streams, AOT hook binaries, a SignalR watcher, a daemon,
an MCP server, a desktop app. All solved, all battle-tested against real data, all
unusually well commented — and the comments explain *why*.

It also collapses the hardest unknown. You are not inferring a wire contract. You **have**
it, exactly, because the client is the thing making the calls. The two halves meet at
eight routes and one file of wire types. There is very little to negotiate in the middle.

## The method — read this twice

You are **not** diffing files, and you cannot mechanically diff raw JSONL against a
rendered UI. What you are doing is:

1. **Observe** upstream's behaviour for a specific real session on the live instance.
2. **Write it down as an executable expectation** — a conformance test asserting what the
   output must be for that input.
3. **Build until it passes.**

The goal is a duplicate that *matches*, verified per session, per vendor, per field.
**Upstream is the oracle.** "It compiles" is not done; "it matches upstream for this
session" is done. Where upstream is silent or self-contradictory, decide — and record the
decision as an assumption, not as a fact.

You have an unusual advantage here: **both ends of the transform are observable.** The
raw agent files are on disk, and the normalized output for those same sessions is
readable through the live console and the connected MCP tools. Any time you catch
yourself speculating about server behaviour, stop — you have a way to measure it.

## What you have access to

- **This repo** — the client. 772 commits of upstream history, remote already detached.
- **A live instance** — `https://moosegoose.kcap.ai`, authenticated in the browser,
  ~123 real sessions. Drive it.
- **The `kcap` MCP tools** — `kcap-analytics`, `kcap-sessions`, `kcap-review`,
  `kcap-memory`, `kcap-workitems`, `kcap-flows`, connected to that same instance.
  `get_analytics_schema` alone returns ~87,000 characters describing the model in the
  system's own words. Read all of it before designing any schema.
- **The installed binary** — `kcap.exe` on PATH, runnable. It is hand-patched; don't
  reinstall it from npm.
- **Raw agent data on disk**, plus a second machine (`hephastus`) holding data this one
  lacks. Details in SURFACE.md §6.
- **`reference/ui-assets/`** — the console's fonts, favicons and stylesheets, already
  downloaded.

## Scope: all of the features, but not all of them

Aim for the whole shape — capture, normalize, store, query, show. **Do not aim for every
feature in the product.** Some of what's here is capture; some is product built on top of
capture (evals, review flows, a hosted agent runtime, terminal multiplexing, permission
telemetry, work items, analytics dashboards, memory). Some of that is core and some is
peripheral, and **you do not get to decide which alone.**

Wave 4 has the procedure: enumerate the complete surface from evidence, propose in / out /
later with a cost per item, then **stop and get the operator's confirmation.** That is the
only place in this job where you should block and wait.

Do not assume you know which features are cheap. The operator's read on difficulty will
differ from yours, and being wrong in either direction is expensive: cutting something
essential wastes the run, building something peripheral wastes a day.

## Prior art in the operator's org — read, but verify

`MooseGooseConsulting/agent-corpus` (`docs/kcap/`, `deploy/FIDELITY.md`),
`MooseGooseConsulting/llm-archiver` (existing parsers), and
`MooseGooseConsulting/agent-control-plane` (`capture/artifacts/` format dossiers) all
contain relevant material — and all contain documents written by earlier agents, some
recording "decisions" no human ever made and which turned out to be false. **If a document
asserts a decision without evidence, treat it as a claim to check, not a constraint to
inherit.** Ask the operator.

## Licensing — brief but real

`LICENSE.md` is Kurrent License v1. It grants "use, copy, distribute, make available, and
prepare derivative works," so building on this is permitted. Two limits: don't provide it
to third parties as a hosted service exposing a substantial set of its features, and don't
remove or obscure the licensing and copyright notices. **Keep `LICENSE.md` intact**, note
provenance in the README, and treat the bundled fonts, favicons and product name as
placeholders to be replaced before anything leaves the org.

---

## Done means

- **The stack runs standalone** — our client, our server, our store, our console, on one
  machine, with no dependency on the vendor's hosted service.
- A real import of real on-disk sessions lands and renders.
- Correct on the invariants: import twice and get no duplicates; deliver a transcript
  before its session-start and have it reconcile; import a session with subagents and see
  them nested one level deep.
- **Three vendors normalized end to end**, each verified against the live instance's own
  output for the same sessions.
- A console that renders a captured session — Transcript and Trace at minimum.
- **A fourth vendor the upstream product does not support, added end to end.** The
  acceptance test for the normalizer premise.
- **A second machine enrolled headlessly and recording into the same corpus**, its
  sessions distinguishable from this laptop's. The acceptance test for the fleet
  objective. A stack that records one machine perfectly has not met the goal.
- A written record in `reference/` of the feature cut, the decisions taken, and every
  assumption still unverified.

Start with Wave 1.
