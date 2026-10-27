# Concept 1 — Quiet Ledger

A conventional shell done carefully: title bar, 208 px nav, one white page sheet, a 312 px inspector rail. Same regions and the same controls as the real app (nav: Create, Upload, Remote test, Applications, Advanced, Settings; page header, body, footer, rail; Applications table + selected-row rail; Application detail with Overview / Package / Deployment). The rail folds behind a **Details** button when the page is under 1000 px; at 1280 it stays open.

**Palette** (light default, dark via Settings › Appearance) — neutral surfaces `#ECEDEF` shell / `#FFFFFF` page / `#F8F8F9` rail, hairlines `#E5E6EA`, ink `#16171A`, muted `#5E626C`. One brand colour, the app's amber: fill `#DFA24F` with dark text, text/links `#8A5508`. Green/amber/red appear only as status. Depth: Beautiful sm on the page sheet and active nav item, md/lg only on the toast and the delete dialog; no gradients.

**Type** — Instrument Sans (the app's bundled fallback for Segoe UI Variable) at 13.5/20; page title 22/600, section headings 14/600, sentence-case labels, no all-caps. JetBrains Mono (Cascadia in the real app) for versions, paths, IDs and command lines.

**Layout idea** — hierarchy comes from whitespace and a single amber marker, not boxes: tables are rows with hairlines, the selected row gets a soft amber wash and a 3 px bar, the primary action is the only filled amber button on a screen.

**States shown** — signed out (title-bar pill, Applications empty state, disabled write actions), no search match, loading skeleton and a refresh error banner (Refresh succeeds on the 1st press, fails on the 2nd; Try again recovers), delete confirmation, publish progress, test output. Focus is a 2 px amber ring; `/` focuses search; arrow keys move table selection.

## References
- Awwwards (awwwards.com): the quiet top bar with a pill search field and one filled button next to outlined ones; borrowed for the toolbar search + Refresh pairing. Screenshot: `.orch/shots/ref-awwwards-home.png`.
- CSS Design Awards (cssdesignawards.com): restrained warm-grey ground with generous margins and tiny tracked meta labels; borrowed for page margins (32 px) and muted meta text, not the all-caps tracking. Screenshot: `.orch/shots/ref-cssda-home.png`.
- The FWA (thefwa.com): reviewed; nothing borrowed beyond confirming the calm-neutral direction. Screenshot: `.orch/shots/ref-fwa-home.png`.
- Fidelity limit: the real app uses WPF-UI Fluent controls; these are HTML approximations of the existing `--pm-*` roles.
