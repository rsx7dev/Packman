# Attended local Remote Test verification

This helper prepares and restores a small real PSADT 4.1.7 fixture. It does not
run deployments or replace `RemoteTestService`. Install, detection and uninstall
must be exercised in the normal Packman UI. This is a manual Windows check with
network access and an attendant who can approve UAC; it is not a CI test.

Build on Windows:

```powershell
dotnet build tests/manual/RemoteTestFixture/RemoteTestFixture.csproj -c Release
```

1. Close Packman. Launch `RemoteTestFixture.exe` from the build output and click
   **Prepare fixture**. It backs up the existing settings, downloads the full
   PSADT runtime, creates temporary source/template/output folders and configures
   those paths. An existing IntuneWinAppUtil path is required. Authentication
   settings are preserved; no Intune requests or uploads are part of this test.
2. Coordinate the preparation prompt with the attendant, then click **Protect
   staging (UAC)**. This snapshots WinRM state, stops/disables WinRM and creates
   the fixture's staging directory with administrator/SYSTEM write access and
   Users read access. **Inspect** must report an unelevated helper, disabled and
   stopped WinRM, and a denied unelevated staging write.
3. Launch the normal unelevated Packman build. Create a package with manufacturer
   `Orchestra`, name `PackmanLocalProbe`, version `1.0.0`, and source folder
   `%TEMP%\PackmanLocalVerificationT0218\sources`. The template already defines
   its actions, so the missing installer insertion-marker warnings are expected.
4. Open **Remote test** from the generated package card, not the standalone
   navigation page. Select **Run on this PC**, **System**, and **Silent**. Leave
   staged-package deletion off so the protected staging ACL remains testable.
5. Click **Install**. Record the first-install confirmation before accepting it.
   Alert the attendant before accepting, and count UAC prompts. The run must
   prompt once, complete as SYSTEM, and stream real PSADT output into Packman.
   The fixture copies Windows' `whoami.exe` to
   `C:\Program Files\PackmanLocalProbe\PackmanLocalProbe.exe` and returns **3010**
   without rebooting. Save the log showing the command, SYSTEM SID, Silent mode,
   PSADT log path, exit code, restart meaning and success result.
6. Click **Discover detection**, then **Apply detection**. Verify the discovered
   local file rule reaches the wizard's publish fields; do not publish. Save the
   log before starting uninstall, which clears it.
7. Coordinate the uninstall UAC prompt, run **Uninstall**, and save its completed
   log: exit **0**, success result, PSADT log path and SYSTEM/Silent probe entry.
   **Inspect** must show no installed probe and no `Packman_RemoteTest` task.
8. Close Packman. Coordinate **Restore PC (UAC)**. It restores the settings bytes
   and original WinRM state, removes only fixture staging/logs and newly created
   empty parents, and removes the temporary template/source/packages. It refuses
   restoration if the probe application is still installed: uninstall first.
   Record the final inspection and compare it with baseline. Settings restoration
   is checked against the original bytes.
9. Save the evidence externally, then click **Remove saved fixture evidence** to
   delete the temporary recovery backups and prior restored fixture attempts.

The helper records its output and saves PSADT logs under
`%TEMP%\PackmanLocalVerificationT0218`. Preserve these as test evidence before
removing that folder; `settings.backup` is a local recovery copy and must not be
committed or included in shared evidence. Screenshots belong under `.orch/shots/`.
A completed build or fixture preparation is not evidence that the integration
steps passed. Record each actual outcome and any unanswered UAC prompt.
