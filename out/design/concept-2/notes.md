# Concept 2: Command dock

**Idea.** In the current app, the 212 px left nav and the PageFrame footer (Back, hint, primary action) live at opposite edges of the window. Here they become one floating graphite dock at the bottom of the page sheet. On the left of the dock are the six destinations: Create, Upload, Remote test, Applications, Advanced and Settings. Only the active one shows its label; the others show their label as a tooltip. On the right is the current page's footer: the hint, Back, and the primary action (or the pager on Applications). The page gets the full width back, and the next action always sits in the same place. The inspector rail stays on the right as a grid of hairline cells: a small label at the top, a large value underneath.

**Layout logic kept.** The title bar keeps the connection pill (click it to sign out, then sign in again from Settings). Every page still has a header (breadcrumb, title, subtitle, actions, stepper or tabs), a body, a footer (now in the dock) and the inspector rail. Edit script still has no rail. No features were added or removed.

**Palette.**
- Mist canvas `#E6E8E9`
- White sheet `#FFFFFF`
- Graphite dock and ink `#1D2024` / `#15181C`
- Slate text `#5C646D`
- Hairlines `#DADEE1`
- Amber, the only brand colour: `#E3A04A` for fills, `#8A5306` for text
- Status colours: ok `#1D7A3B`, warn `#7A6100`, bad `#B42318`

There is a full dark theme (Settings ▸ Appearance). There are no shadows, except on the dock and on dialogs, which really do float.

**Type.** Instrument Sans and JetBrains Mono, the two families the app already bundles. Page titles are 34/600 with −0.022em tracking. Rail values are 30/500 with tabular figures. Body text is 14. Mono is used only for paths, versions, product codes, command lines and script. Labels are in sentence case, so the caps section labels are gone.

**States designed.** Search with no results (with Clear search), loading skeleton on Refresh, signed out (Applications, Detail, Review, Upload, Advanced, Settings), Publish disabled until ready, publishing step list with progress, Package generated, remote test running and finished, saved feedback in the dock.

**Borrowed from the references** (screenshots in `.orch/shots/`):
- Awwwards (`ref-awwwards-dock.png`): the floating dark segmented dock with one highlighted segment, set over the content. Borrowed for the layout and the active-segment treatment.
- The FWA (`ref-fwa-grid.png`): modular hairline cells with a small label and a large numeral (Points 83 / Days 5). Borrowed for the inspector rail's cell grid and its type scale.
- CSS Design Awards (`ref-cssda-home.png`): a calm grey canvas with one white surface lifted off it. Borrowed for the section rhythm: mist canvas, white sheet, few dividers.

**Current UI reference.** Packman is WPF and can't run on this Linux host, so `out/design/current/` holds the repo's own `docs/images` previews instead of live screenshots.
