# Eval direction — capability is core, design is open

This note records the operator decision for Capacitor's evaluation subsystem.

## What is decided

**Keep evaluation as a core Capacitor capability.** The standalone stack must be able to run evaluators over captured or replayed agent sessions, ground those evaluators in the recorded evidence, and persist the resulting observations so they can be queried and compared later.

Judge cost is not a design constraint that justifies cutting eval. The expected default is free hosted models and/or local models; paid hosted judges are optional.

## What is explicitly *not* decided

Do **not** preserve KCap's current 13-question taxonomy, aggregate score, evaluator catalog, or orchestration merely because they exist in the inherited client. Those have already been reconsidered elsewhere and may be replaced substantially or entirely.

The eval harness should support multiple execution shapes rather than hard-code one:

- **single-purpose evaluator** — KCap's useful current pattern: one fresh agent invocation receives one narrow question and can focus exclusively on that failure/success dimension;
- **multi-question evaluator** — one evaluator may answer several related questions when shared context makes that materially cheaper or better;
- **deterministic scorer/check** — no LLM where the property can be measured directly;
- **pairwise/blind comparison** — compare two replay arms without exposing treatment identity;
- **specialized evaluator packs** — different investigations can supply different questions/scorers rather than inheriting a universal taxonomy;
- **multiple models / repeated judges** — available when disagreement, calibration, or robustness matters.

These are options, not a committed architecture. We will decide the right mix empirically as the corpus-learning and historical-replay work develops.

## The KCap pattern worth keeping as a reference

KCap currently runs each eval question in its own headless agent invocation. That is valuable because the evaluator has **one job**: it is not simultaneously trying to score safety, efficiency, support, testing, and every other dimension. This reduces cross-objective distraction and is especially attractive for narrow questions such as "is this claim supported?" or "did this session contain avoidable rework?"

Preserve the ability to run that way. Do not turn it into a requirement that every future eval must use that shape.

## Boundary

The durable contract is therefore:

> Capacitor owns a configurable evaluation execution surface over its session corpus. Evaluator definitions, model backends, execution topology, scoring/aggregation, and experiment-specific judge packs are pluggable and may evolve.

This note governs the interpretation of older references to "keep eval execution" in `PROMPT.md`, `FLEET.md`, and `BACKLOG.md`: they mean **keep the capability**, not **freeze the inherited KCap eval product design**.
