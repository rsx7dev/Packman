# Local SYSTEM/Silent Remote Test verification

This explicit Windows integration fixture runs a small real PSADT 4.1.7 package
through the production `RemoteTestViewModel` and `RemoteTestService`. It requires
an already elevated Windows node process and network access. It never asks for
elevation, changes UAC settings, or requires an attendant. An ordinary user
launch fails immediately without changing the PC.

Build and run on the Windows node, in an existing elevated administrator or SYSTEM context:

```powershell
dotnet test tests/manual/RemoteTestFixture/RemoteTestFixture.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Local verification failed: $LASTEXITCODE" }
```

`dotnet test` builds and executes this console fixture through an explicit MSBuild
target. A failed prerequisite or assertion returns a failing test command; it is
never reported as skipped. Normal builds do not execute the integration check.

The fixture checks that its paths and `Packman_RemoteTest` task are absent before
starting. It snapshots WinRM, stops and disables it, and creates staging with
SYSTEM/Administrators write access and Users read access. It uses isolated
settings; the owner's Packman settings are never written.

It selects **Run on this PC**, **System**, and **Silent**, then executes the real
Install command. The fixture records and accepts the first-install confirmation
through `IDialogService`; it does not test the rendered confirmation dialog.
There is no replacement deployment service, synthetic exit code or task script:
production code stages the package and runs its one-shot SYSTEM task.

The package copies Windows' `whoami.exe` to
`C:\Program Files\PackmanLocalProbe\PackmanLocalProbe.exe`. Checks require install
exit **3010**, its `InstallErrorText` restart meaning, the success result line,
the actual PSADT SYSTEM/Silent probe entry and PSADT log path. **Detect** searches
for the real installed executable; **Apply detection** must populate the publish
step's local path, filename and version. No publishing or Intune calls occur.
Uninstall must return **0**, report success, remove the probe and scheduled task,
and leave Detect without a matching executable.

A `finally` block attempts production uninstall if needed, restores the original
WinRM state, removes fixture staging/logs/source and newly created empty parents,
and verifies absence of the probe and fixture paths. A failed restoration makes
the fixture fail. Inspect `verification-evidence/verification.txt` beside the exe
for the exact outcome; a build alone is not a passing integration result.

The evidence directory also contains install, Detect/Apply, uninstall and
post-uninstall detection logs, plus the original PSADT logs. Copy it to the task's
`.orch/shots/` for review. These are test artifacts and must not be committed.
This SYSTEM/Silent verification follows the owner's revised scope; it does not
claim an unelevated UAC run or a remote WinRM deployment was exercised.
