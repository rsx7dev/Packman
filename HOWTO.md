# PACKMAN — Quick start

Installer file to published Intune app, in the shortest path that works. Every step links to the
full explanation in **[README.md](README.md)**.

> **Before you start:** Windows, the .NET 10 Desktop Runtime, a PSADT v4 template folder
> **including the runtime**, `IntuneWinAppUtil.exe`, and an Intune account with the required Graph
> permissions — see [Requirements](README.md#requirements).

---

## 1. Set it up once

Open **Settings** and complete two sections, then press **Save settings**:

1. **Authentication** — press **Sign in** (interactive needs no app registration), then
   **Test connection** to check Graph read access. Sign-in asks for read access only; Packman asks
   for write permission the first time you publish or change an app, and for directory access when
   you open **Advanced**. A dedicated app registration is recommended over the shared default client:
   see [Dedicated app registration](README.md#dedicated-app-registration).
2. **Network Paths** — **Intune Applications Path** (where packages are written), **PSADT Template
   Path**, and **IntuneWinAppUtil Path** (see [Get IntuneWinAppUtil.exe](#get-intunewinapputilexe)).

Code signing, group-assignment defaults, Intune defaults and the theme are optional. Until you
dismiss it, the **Create** page shows a **Before your first package** card listing anything still
missing, with a link to the setting that fixes it.
→ [First-run setup](README.md#first-run-setup--the-settings-page)

### Get IntuneWinAppUtil.exe

Packman does not bundle or download Microsoft's Win32 Content Prep Tool. Get it from its only
official source, Microsoft's repository
[microsoft/Microsoft-Win32-Content-Prep-Tool](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool)
(**Releases**, or `IntuneWinAppUtil.exe` in the repository root), then check the file before you
point Settings at it:

```powershell
Get-AuthenticodeSignature .\IntuneWinAppUtil.exe | Format-List Status, SignerCertificate
Get-FileHash -Algorithm SHA256 .\IntuneWinAppUtil.exe
```

`Status` must be `Valid` with a *Microsoft Corporation* signer. Record the SHA256 and compare it on
every packaging machine (or against the release you downloaded it from), so they all build with the
same tool. Keep it outside the share Packman writes packages to.

## 2. Generate the package

**Create Package** → drag your **MSI or EXE** onto the Source installer field. Name, manufacturer,
version and icon are filled from the installer; correct anything you disagree with, pick
**x64/x86** and **SYSTEM/USER**, then **Generate package**. The button stays disabled until the
installer, name, manufacturer, version, output share and template are set; its tooltip says which is
missing. Dropping a different installer replaces the values the last one filled in, but keeps what
you typed.

→ [Creating a package](README.md#creating-a-package) — including exactly what an MSI fills in
versus an EXE, and where the package lands on disk.

## 3. Fix the script (EXE packages only)

An MSI package usually runs as generated. For an **EXE**, the Installer panel in the right rail
names the installer type and lists its silent switches; **Add** them to **Install arguments** and
**Uninstall arguments** before generating. Anything left empty is written as the `<silent flags>` /
`<uninstall flags>` placeholders — open **Edit script**, replace them with the real switches, then
Ctrl+S. Configure and Review flag leftover placeholders, and **Publish** stays disabled until they
are gone.

→ [Editing the deploy script](README.md#editing-the-deploy-script)

## 4. Test it on a real machine (recommended)

**Remote Test** → enter a computer name, then **Ping**. Choose **System** or **User** to match the
app’s Intune install behavior and the **PSADT deploy mode** you will publish with, then **Run
install** — it stages the package in `C:\ProgramData\Packman` (SYSTEM and Administrators only),
runs the same `Invoke-AppDeployToolkit.exe` command line Intune will, and streams the PSADT log live.
Package problems such as a leftover EXE placeholder show as warnings at the top of the log, and
**Cancel** stops a run. Check the discovered detection rule and use
**Use for publishing** to carry it into Configure for the wizard’s package. Verify **Run uninstall**
on the test device too; test devices need WinRM and admin-share access.

→ [Remote testing](README.md#remote-testing)

## 5. Publish

**Continue to configure**, then:

1. Check the **Name in Intune**.
2. Confirm the **detection method** — MSI packages default to the product code; anything else needs
   a path.
3. Use **Silent** for unattended Intune deployment and verify that both commands need no user input.
4. Add **assignment** groups, each with its own intent (Required / Available / Uninstall).
5. **Review deployment**, check the summary, **Build & publish**.

→ [Publishing to Intune](README.md#publishing-to-intune)

---

## Where to go next

| I want to… | See |
|---|---|
| Roll out a new version of an existing package | [Upgrading a package](README.md#upgrading-a-package-to-a-new-version) |
| Publish a package I already built | [Upload to Intune (standalone)](README.md#upload-to-intune-standalone) |
| Browse, filter, edit or delete tenant apps | [Applications](README.md#applications--browsing-your-tenant) |
| Bulk-add PCs to a group, or see what targets a group | [Advanced](README.md#advanced--directory-tools) |
| Know what Packman deliberately does not do | [Limits](README.md#what-packman-cannot-do) |
| Fix an error message | [Troubleshooting](README.md#troubleshooting) |
| Find the logs | [Where things live on disk](README.md#where-things-live-on-disk) |
