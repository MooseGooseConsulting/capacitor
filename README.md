# Capacitor (working name)

A self-contained system for recording AI coding-agent sessions — client, server,
store, and console — that we own end to end.

## What this is

The client half began as `kurrent-io/kcap-cli`, which is source-available. The
server half is closed upstream, and building it is the job. See `NOTICE.md` for
provenance and licensing.

The reason for the work, in one paragraph: the client tags each batch of
transcript lines with a `vendor` string, and upstream's closed server routes that
to a per-vendor normalizer. You can write the client side of a new coding agent
in an afternoon and it goes nowhere, because you cannot write the normalizer.
Owning the server removes that gate.

## Start here

| | |
|---|---|
| `PROMPT.md` | the brief — what to build and how to judge it done |
| `reference/SURFACE.md` | everything observable about the system, both halves, mapped |
| `reference/WAVES.md` | a proposed decomposition, explicitly offered as a hypothesis to challenge |
| `reference/ui-assets/` | console fonts, favicons, stylesheets (placeholders) |
| `reference/UPSTREAM-README.md` | the inherited README, kept for reference |

## Status

Fork point committed. Nothing new built yet.

Pivot work still outstanding, and deliberately left to the build so it is done
with full context rather than half-done now:

- **Telemetry** — `src/Capacitor.Cli.Core/Telemetry/` is a 14-file PostHog client
  reporting to the vendor. Cut or repoint.
- **Hosted defaults** — a bare profile slug expands to `{slug}.kcap.ai`; tenant
  provisioning calls the vendor's API.
- **Auth** — WorkOS OAuth. A profile with `auth_provider: null` resolves to
  provider `None` and posts unauthenticated; that is the way in.
- **Update check** and **`kcap feedback`** both reach the vendor.
- **Naming and marks** — product name, favicons, Solina typeface.
