# Captured reference evidence

This directory preserves evidence gathered from the inherited Kurrent Capacitor
surface. It is not the current architecture, deployment configuration, or
product roadmap for this repository.

`SURFACE.md` is the behavioral reference for the target web console; the
`ui-assets/` captures document its observed structural vocabulary, design
tokens, and screenshots. Current product requirements are stated in
[`docs/web-console.md`](../docs/web-console.md).

The contents remain subject to the repository's provenance and licensing
notices. MudBlazor is separately reusable under its own license, but Kurrent
and Capacitor names, favicons, and the Solina typeface are not ours. Keep them
as internal evidence only and replace protected marks and assets before any
external display. Do not edit captured files to make them look like current
documentation.

## Retained inherited guides and probes

The following documents retain detailed source material that is useful for
implementation research but is not a statement of the current Capacitor
runtime:

- [Inherited client README](VENDOR-README.md) records the vendor's published
  client surface and terminology.
- [Inherited plugin guide](../kcap/plugin-guide.md) records plugin-registered
  MCP tools, hooks, and skills.
- [Inherited package guide](../npm/kcap/package-guide.md) records the published
  package's setup and command surface.
- [Cursor hook probe guide](../scripts/cursor-hook-probe/probe-guide.md)
  records the bounded experiment used to establish Cursor hook behavior.
- [Captured console assets](ui-assets/evidence.md) and
  [captured iconography](ui-assets/icons-extracted/iconography.md) describe the
  internal visual evidence and its licensing boundary.

Read those materials with the [source hierarchy](../docs/evidence-and-decisions.md)
in mind: a Kurrent URL, auth assumption, command, or feature claim is evidence
to reconcile, not a Capacitor deployment or roadmap assertion.
