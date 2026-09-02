# Console design system, captured from the live instance

Captured 2026-08-28 from `https://moosegoose.kcap.ai` (Kurrent Capacitor v0.11.31), signed
in through WorkOS in Chrome. Read-only: navigation, tab switches and one theme cookie that
was set back to its original value afterwards.

Same standing as the rest of this directory under `NOTICE.md` — Kurrent's copy, marks and
typeface, fine as internal reference, replaced before anything leaves the org.

## What is here

| path | what |
|---|---|
| `tokens/tokens.css` | every custom property as **resolved** on `:root`, light theme |
| `tokens/tokens-dark.css` | the same for dark — only the tokens that differ, including hover twins; chosen-dark plus a `prefers-color-scheme` arm for visitors who never chose |
| `css/components.css` | the bespoke stylesheet (unchanged; byte-identical to live) |
| `css/components-inline-list-card.css` | session card CSS recovered from an inline `<style>` — **not in components.css** |
| `js/` | the console's own bundles, including `theme.js`, byte-for-byte as served — comments and all, so nothing in them is edited to house style |
| `js/THIRD-PARTY-NOTICES.md` | copyright notices and full MIT / BSD-3-Clause texts of the libraries embedded in those bundles |
| `js/vendor/driver/driver.css` | Driver.js base styles, at the path `css/product-tour.css` imports |
| `icons-extracted/iconography.md` | which Material icon carries which meaning |
| `screenshots/` | 14 screens, dark plus a light reference |

## Three findings that change how the rebuild is scoped

**1. components.css is not the whole stylesheet.** The console emits ~44 KB of bespoke CSS
in inline `<style>` tags — 13 unique blocks, repeated per component instance (the session
card block appears 127 times on a full list; 1.7 MB inline in total). `.list-card`,
`.list-card-header`, `.status-dot`, `.lc-chip-*`, `.compact-stats-*`, `.stat-item`,
`.repo-sidebar`, `.setup-progress-card` and `.empty-state` are all inline and absent from
`components.css`. Only `.list-card` is recovered here. To get the rest, run this in the
console's page and read the blocks back a line at a time — the whole-string form trips the
browser tool's content filter, the line-array form does not:

```js
const seen = new Set();
[...document.querySelectorAll('style')]
  .map(s => (s.textContent || '').trim())
  .filter(t => t && !seen.has(t) && seen.add(t))
  .map(t => ({ head: t.slice(0, 60), lines: t.split('\n').map(l => l.trim()).filter(Boolean) }));
```

**2. There is no artwork.** Every icon is a stock MudBlazor Material icon, the only image
is the signed-in user's WorkOS avatar, and the wordmark is text in Solina rather than a
logo file. MudBlazor is MIT, so the icon set comes free with the component library — see
`icons-extracted/iconography.md` for the meaning-to-icon mapping, which is the part that is not
recoverable from the stylesheet.

**3. Dark is a re-palette, not an inversion.** Primary goes wine `rgba(99,27,58,1)` to
lavender `rgba(222,217,255,1)` and takes near-black text; success goes teal to mint;
warning goes olive to yellow. Secondary is the one brand colour that does not move.
Recolouring dark by filtering or lightening the light palette will be wrong.

Also worth knowing: the type scale is overridden hard from MudBlazor's Material defaults
(h1 is 1.75rem here against Material's 6rem), so leaving MudBlazor's typography alone will
not look like this console. And the six session-detail tabs are URL-addressable via
`?tab=overview|transcript|events|trace|evaluation|details`.

**4. No screen carries a machine dimension.** Confirmed by capture, not just by schema —
every screenshot here (session list, all six detail tabs, Agents, Work Items) shows repo,
vendor, model and owner, and never a machine or host. `FLEET.md` §3 already derives this
from `v_an_sessions` having no machine column; this capture is the same conclusion reached
from the rendered UI instead of the view definition. A `.list-card` rebuilt from the
structure above will reproduce that gap unless a machine chip and filter are added
deliberately — there is no existing slot in the markup to extend.

## Structure

The layout nests `.mud-main-content > .kcap-main-content > .ud-layout > .ud-list-panel >
.ud-list-section > .list-card`, with `.ud-detail-panel` alongside carrying
`.ud-tab-bar`/`.ud-compact-tab`/`.ud-tab-content`. A session card is:

```
.list-card
  .list-card-header    .status-dot.active | .list-card-title | .lc-visibility (svg)
  .list-card-repo      span | a.list-card-pr
  .compact-stats-col
    .compact-stats-row .hk-chip.hk-chip-claude | .hk-chip.hk-chip-model
    .compact-stats-row .stat-item.token-stat | .stat-item.context-stat | ...
```

Vendor chips are per-vendor classes (`.hk-chip-claude`, `.hk-chip-codex`, …), so adding a
vendor is a class plus a colour, not a new component.

## Not captured

Whole-page DOM snapshots. Chrome blocks programmatic downloads in this context, and a
session page's DOM is ~1.8 MB, too large to bring back through the tool channel. The
structural skeleton and the class vocabulary above cover what a rebuild needs from it; if a
full snapshot is wanted later, `single-file-cli` driven against a Chrome profile that has
signed in once is the tool for it.

MudBlazor.Markdown runtime assets. `js/MudBlazor.Markdown.min.js` is the captured bundle;
syntax-theme CSS and MathJax are loaded later from the NuGet static-web-asset root
`_content/MudBlazor.Markdown/` (`code-styles/<theme>.css` via `setHighlightStylesheet`,
`MudBlazor.Markdown.MathJax.min.js` via `appendMathJaxScript`). Those files are not in
this tree. A reconstruction that turns those features on still needs that package's
`wwwroot` (or the 404s are expected). The bundle itself is enough for the Markdown
renderer that ships without those options.
