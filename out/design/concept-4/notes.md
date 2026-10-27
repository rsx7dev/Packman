# Concept 4 — Command Deck

A keyboard-first reinterpretation of the same Packman shell. The persistent left nav is replaced by a **command palette over one workspace**: a slim title bar, a command bar with a `Ctrl K` input and quick actions, one flat page surface, a hairline-separated inspector pane, and a status bar that shows the screen's keyboard hints. All six destinations and every page (Applications, Create, Upload, Remote test, Advanced, Settings, Edit script, Application detail) stay, reachable through the palette, the hash router and the same controls.

**Palette** (light default, dark via Settings › Appearance) — a cool near-white canvas `#FAFBFC`, white pane `#FFFFFF`, neutral chrome bars `#F1F2F5`, hairlines `#E5E7EB`, ink `#13151A`, muted `#5A606A`. Amber is still the only brand colour, but its role changes: it is no longer the filled primary button. Primary actions are **ink-filled**; amber marks the focused command, the active tab/stepper/selection bar and inline links (`#D99A3D` fill, `#8A5A0F` text). Green/amber/red appear only as status. Depth comes from hairlines and one layered neutral shadow (Beautiful lg) on the palette and dialog, md on the toast; no gradients.

**Type** — Instrument Sans (Segoe UI Variable fallback) for prose and titles; JetBrains Mono (Cascadia fallback) promoted to the *command layer*: column heads, section labels, kbd chips, meta, versions, paths and command lines are all mono. Titles 20/600 sentence case; labels stay sentence case, never tracked caps.

**Layout idea** — minimal chrome, flat deck. No cards; content sits directly on the canvas, separated by hairlines and whitespace. Section headers are 11.5 px mono labels. The right pane is a plain column, not a box. The status bar (30 px) carries context keyboard hints, so the app always shows how to drive it without the mouse.

**States** — signed out (title-bar pill, Applications empty state, disabled write actions), no search match, loading skeleton, refresh error (Refresh succeeds on the 1st press, fails on the 2nd; Try again recovers), delete confirmation, publish progress, remote-test output. `Ctrl K` opens the palette, `Ctrl N` new package, `Ctrl S` saves script, `/` filters, `↑↓⏎` move and open rows and commands.

## References
- Awwwards (awwwards.com): the quiet top bar where a single search field is the primary nav affordance; borrowed for the command-bar input as the main entry point. Screenshot: `.orch/shots/ref-awwwards-home.png`.
- CSS Design Awards (cssdesignawards.com): restrained neutral ground with small mono meta labels and generous margin; borrowed for the muted mono status/meta vocabulary and the flat, hairline-divided sections (not the all-caps tracking). Screenshot: `.orch/shots/ref-cssda-home.png`.
- The FWA (thefwa.com): one idea per screen, nothing decorative competing with the work; borrowed for the "one workspace, one focus" discipline — minimal chrome so the page content is the hero. Screenshot: `.orch/shots/ref-fwa-home.png`.
- The command-palette pattern itself comes from Spotlight/Raycast-style apps, not these galleries; the galleries contributed the calm surface and typography direction only.
- Fidelity limit: the real app is WPF-UI Fluent; these are HTML approximations of the existing `--pm-*` roles, with the shell re-skinned as a command deck.
