# design-sync notes - Packman

Repo-specific facts for the next sync. Read before running anything.

## What this design system is

- Packman is a **WPF / .NET 10 desktop app** (C#, XAML, WPF-UI Fluent controls). It has no
  React or JavaScript component library, so this is a **tokens-only** sync (user's explicit
  choice, 2026-09-02): colours for both themes, typography, per-control recipe tokens, the
  bundled fonts and the icon geometries. Zero components by design - do not try to
  reimplement the XAML controls as React lookalikes.
- The source of truth is `Packman/Themes/DarkTheme.xaml`, `LightTheme.xaml`, `Styles.xaml`,
  `Icons.xaml` and `Packman/Fonts/*.ttf`. `.design-sync/gen-tokens.mjs` converts them
  deterministically into the synthetic package `.design-sync/pkg/` (`packman-tokens`):
  `tokens/{colors,typography,components}.css`, `fonts.css`, `styles.css`, `docs/icons.md`.
  `cfg.buildCmd` runs that script; re-run it whenever the XAML or fonts change and commit the
  regenerated files (they are committed so the package is reviewable and self-contained).

## How the converter is wired

- `.ds-sync/` is staged from the skill and gets `packman-tokens` as a **file: dependency**
  so `node_modules/packman-tokens` is a symlink to `.design-sync/pkg`:
  `cd .ds-sync && npm i esbuild ts-morph @types/react react react-dom playwright@1.56.1 ../.design-sync/pkg`
  Run the converter with `--node-modules .ds-sync/node_modules` (no `--entry` needed).
- `playwright@1.56.1` matches the pre-installed browsers under `/opt/pw-browsers` in the
  claude.ai/code container (`PLAYWRIGHT_BROWSERS_PATH` is set there). Elsewhere run
  `npx playwright install chromium` once. The render check has nothing to render (0 previews)
  but validate still requires playwright to be importable.
- Token naming: XAML key minus the `Color`/`Brush` suffix, kebab-cased, `--pm-` prefix
  (`Card2Color` -> `--pm-card-2`). Control recipes are `--pm-<style-key>-<prop>`
  (`PrimaryButton.Padding` -> `--pm-primary-button-padding`). WPF thickness order `l,t,r,b`
  is converted to CSS `t r b l`; `h,v` to `v h`.
- Dark is `:root` (the app starts in Dark, App.xaml). Light is `[data-theme="light"]`. The
  app's "System" option is not mirrored with a media query on purpose - designs should be
  explicit about the theme they show.

## Known validate output

- `[FONT_MISSING] "Cascadia Mono"` - expected. The XAML font stacks put the Windows system
  faces first (Segoe UI Variable, Cascadia Mono, Consolas) and the bundled OFL faces last;
  the bundled Instrument Sans / JetBrains Mono / Martian Mono are what renders everywhere
  else. Accepted as a substitute by design (user chose tokens-only from the repo's own fonts).
- The generated README body says "React library" and "All 0 components" - the prepended
  `conventions.md` corrects that in its first lines; the body text is the converter's template.

## Upload status

- 2026-09-02: first run in claude.ai/code. `DesignSync` was **not authorized** in that remote
  session (the tool asks for `/design-login` from an interactive Claude Code session on the
  same machine, or Claude Design's "Send to Claude Code Web"). The bundle was built and
  validated locally (`ds-bundle/`, validate exit 0) but **nothing was uploaded** and no project
  exists yet - `config.json` has no `projectId`. The next authorized run is a first-time import:
  create a fresh project, pin its id, upload via the incremental path.

## Re-sync risks

- Generated CSS in `.design-sync/pkg/` goes stale silently if `Packman/Themes/*.xaml` changes
  and nobody re-runs `gen-tokens.mjs`. When in doubt, run it - a no-op diff is the check.
- `gen-tokens.mjs` reads template `CornerRadius` from the control's chrome `Border` (named
  `bd`, else the one bound to the template Background/BorderBrush). A restyled template that
  renames that Border can pick the wrong radius - eyeball `tokens/components.css` after a
  Styles.xaml change.
- Font faces are matched from file names `<Family>-<Weight>.ttf`; a new family or weight
  makes the generator throw until its `FAMILY` / `FILE_WEIGHT` maps are extended.
- WPF-UI's own Fluent control templates (`ui:ThemesDictionary`, `ui:ControlsDictionary`) are
  not represented - only Packman's overriding dictionaries are. Controls the app takes
  straight from WPF-UI (dialogs, scrollbars, toggles) have no recipe here.
- Toolchain: node 22, esbuild/ts-morph latest at staging time; nothing is fetched from the
  network during the build itself.

## Drift log

- 2026-09-03: main (PR #3/#4 merges) removed 4 colour roles (PaneColor, PrimaryGlowColor,
  CodeKeywordColor, CodeStringColor), the LogoGradientBrush alias, the MonoValue text style
  and the CardSoft, BrowseButton, FilterPill, PillCount and StatusListRow control styles, and
  restyled BackButton. Tokens regenerated; `conventions.md` edited to drop the removed names
  (counts, code colours, mono-value, filter pill, count pill, status row). 234 tokens now.
- 2026-09-23: three-column redesign added the `PanelColor` role (both themes), 11 icons (IconList,
  IconSliders, IconInfo, IconAlert, IconCopy, IconSave, IconUndo, IconArrowL, IconArrowR, IconPlay,
  IconMore), H2 grew to 16px with new H3 and RailTitle styles, and new styles PanelCard,
  SectionPanel, SectionRule, FooterHint, LinkButton, Callout*, Chip, KvRow(Mono), FieldRow.
  Tokens regenerated.
