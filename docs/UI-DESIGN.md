# Packman desktop UI

The UI centers on the working sequence: select an installer, prepare its PSADT package, test it, configure Intune, review and publish. It keeps the Windows Fluent shell and amber accent. All navigation destinations remain: Create, Upload, Remote test, Applications, Advanced and Settings.

## Layout (three columns)

Every page is a `PageFrame` (`Views/Controls/PageFrame.cs`, template in `Themes/Styles.xaml`): a header panel (breadcrumb, optional leading tile, title, subtitle, actions, and a strip for the stepper or tabs), the body, an optional footer panel (Back, what the primary action does or doesn't do, primary action) and an optional inspector rail on the right. Panels sit on the Mica backdrop with 12px gaps. When the frame is narrower than 1080px (small or scaled screens, for example 1366×768 at 125%) the rail leaves its column and opens over the body from a "Details" button in the header.

The title bar carries the connection pill: Connected with the tenant, or Not connected with a Sign in link that opens Settings on Authentication. The old status bar is gone.

## Pages

- Create: numbered stepper (Package, Configure, Review & publish). Package reads metadata from the dropped installer; the rail shows what the form doesn't: installer type, product code, the resolved package folder, and readiness (installer, output share, template, Intune connection), with a warning for EXE installers that need real silent switches. After generating, the page offers Edit script, Remote test and Open folder before Configure. Configure shows the real command lines (Settings > Intune defaults, plus the chosen deploy mode), detection, requirements (unfolded), return codes, assignments and, for upgrades, supersedence. Review lists everything that will be sent, with copy buttons and links back; Publish to Intune stays disabled with a sign-in callout until a tenant is available (app registration connects on its own).
- Upload: the same sections and labels as Configure, including requirements and return codes, over a package picked from the share, with a publish readiness rail. It keeps its own multi-rule detection editor.
- Remote test: package folder, computer, context, deploy mode, actions and live output; the Test session rail is shared with the wizard's test tool.
- Edit script: one toolbar with the only Save (Ctrl+S still works), a compact file navigator and the Monaco editor. No rail, so the editor keeps the width.
- Applications: Application, Publisher, Version and Updated columns; selecting a row fills the rail, double click opens it.
- Application detail: tile, name and meta in the header with Republish content and Edit script; Overview / Package / Deployment tabs; deployment status and Intune publishing in the rail. Delete from Intune lives in a danger zone at the end of Overview. Signed out, every write action is disabled and the rail shows when the figures were last synced. Detection and assignment sections say that changes are written to Intune immediately.
- Settings: six section tabs, a configuration status rail, persistent save feedback in the footer. The connection test also checks write permission from the token's scopes or roles (DeviceManagementApps.ReadWrite.All, and group creation when per-package groups are on).
- Advanced: bulk add, device membership and apps-for-group tools with a directory action rail.

Inline actions use the accent (`LinkButton`), never blue. Colours come from theme tokens only; both themes are supported.

## Shared components

`Styles.xaml` owns type, buttons, fields, editable dropdowns, focus and definition rows. Browse, Search and Copy actions occupy separate columns with a 10-pixel gap and 40-pixel minimum height; action buttons no longer overlap input borders. `SectionHeader` wraps captions beneath headings. `ReturnCodeEditor` is shared by Settings and Configure. `PublishStatusControl` and `PublishStepList` share progress, cancellation and result presentation across both publishing entry points. `GroupPickerControl` retains a separate intent for every selected group and renders long group names in bounded rows.

## Packaging and Intune behavior

No UI action publishes while merely configuring a package. The shared build pipeline validates the PSADT launcher, script and module manifest before signing or invoking IntuneWinAppUtil. Selecting a different installer clears the previous MSI product code. Script parsing detects syntax errors and exact generated EXE flag placeholders without executing the script. This does not verify vendor switches, dependencies, signatures, device requirements or successful installation; remote and pilot testing remain necessary.

System/User execution context, MSI/file/registry detection, requirements, return-code mappings, group intents, signing, content upload, supersedence and the existing cleanup/error handling remain supported. If code signing is enabled, a certificate or signing failure stops publishing before content is built or uploaded. Intune installation must complete without user input. The UI explains the distinction between attended testing and unattended deployment; it does not silently rewrite saved commands.

Sources reviewed on 2026-09-06:

- [Microsoft: Win32 app configuration](https://learn.microsoft.com/en-us/intune/app-management/deployment/add-win32)
- [PSADT: deployment modes](https://psappdeploytoolkit.com/docs/explanation/deployment-modes)
- [PSADT: command-line parameters](https://psappdeploytoolkit.com/docs/reference/command-line-parameters)

The existing scope limits remain: wizard detection uses one rule; standalone publishing accepts multiple rules; assignment filters, dependencies and All Devices/All Users targets are not added by this redesign. These are useful future workflow additions, not extra UI dependencies.

## Verification

The solution can be cross-compiled with .NET 10 and `EnableWindowsTargeting=true`. The separate test project has been removed; Windows CI retains the offline desktop build, packaged-app startup/shutdown check and standalone `scripts/RenderUiPreviews.cs` utility.

Windows CI additionally loads and renders representative views, all Settings sections, Application Detail tabs and the editable computer dropdown in both palettes. It exports PNG previews as the `ui-previews` artifact. These previews do not authenticate, upload, test a remote device or execute a deployment script. Manual Windows validation should include keyboard navigation, 125%/150% display scaling, long tenant/group names, an actual MSI/EXE package, install/uninstall on a test device, and a pilot Intune assignment.
