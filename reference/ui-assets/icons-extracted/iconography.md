# Captured iconography

**There is no bespoke icon set to rebuild.** Every `<svg>` the console renders is a stock
MudBlazor Material icon — `mud-icon-root mud-svg-icon`, a 24x24 viewBox, and an unmodified
Material Design path. The only raster image on a session page is the signed-in user's WorkOS
avatar, and the "Kurrent Capacitor" wordmark is text in Solina rather than a logo asset.

So the visual identity is entirely CSS, fonts and colour — `../tokens/tokens.css`,
`../css/components.css`, `../fonts/`. MudBlazor is MIT, so the icons come free with the
component library.

What did need capturing is which icon carries which meaning, because that mapping is not
recoverable from the stylesheet. Observed on the sessions list; `head` is the first 46
characters of the path data, enough to identify the Material icon it is.

| meaning | size | head of path |
|---|---|---|
| org visibility ("Shared with org members") | medium | `M0,0h24v24H0V0z M12,7V3H2v18h20V7H12z M6,19H4v` |
| token flow (`3.6M -> 321.6k`) | small | `M15 4c-4.42 0-8 3.58-8 8s3.58 8 8 8 8-3.58 8-8` |
| context occupancy (`245.4k / 258.4k (95%)`) | small | `M13 2.05v3.03c3.39.49 6 3.39 6 6.92 0 .9-.18 1` |
| tool count | small | `M22.7 19l-9.1-9.1c.9-2.3.4-5-1.5-6.9-2-2-5-2.4` |
| completed / ok (uses `mud-success-text`) | small | `M12 2C6.47 2 2 6.47 2 12s4.47 10 10 10 10-4.47` |
| diff added/removed (`+237 -119`) | small | `M18,23H4c-1.1,0-2-0.9-2-2V7h2v14h14V23z M15,1H` |
| error count | small | `M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48` |
| empty state, "Select a session to view" | medium | `M21 6h-2v9H6v2c0 .55.45 1 1 1h11l4 4V7c0-.55-.` |
| rail expand / collapse ("Toggle repos") | small | `M10 6L8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z` / `M16.59 8.59L12 13.17 7.41 8.59 6 10l6 6 6-6z` |
| dismiss ("Dismiss setup card") | small | `M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 ` |
| nav drawer ("Toggle navigation menu") | medium | `M3 18h18v-2H3v2zm0-5h18v-2H3v2zm0-7v2h18V6H3z` |
| global search (Ctrl+K) | medium | `M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16` |
| help | medium | `M11 18h2v-2h-2v2zm1-16C6.48 2 2 6.48 2 12s4.48` |
| new project | small | `M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z` |
| info / trial banner | medium | `M11 7h2v2h-2zm0 4h2v6h-2zm1-9C6.48 2 2 6.48 2 ` |
| user filter ("All users") | small | `M16 11c1.66 0 2.99-1.34 2.99-3S17.66 5 16 5c-1` |
| select caret | small | `M7 10l5 5 5-5z` |

The search icon is the one carrying an extra bespoke class, `nav-search-button-icon`.
