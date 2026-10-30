# STIG GPO Manager

A single-file Windows tool (`StigGpoManager.exe`) that automates the AD side of the quarterly
DISA STIG GPO import: drop in the DISA GPO zip, review what will change, then push it through
pilot to production, with rollback, cleanup and an append-only audit trail. It installs into
**Programs and Features**, adds a Start menu shortcut, and shows a **system tray icon** with the
active run's status (for example `Pilot - day 9 of 14`).

Built for air-gapped, accredited networks: no internet access, no telemetry, no runtime
downloads, and no third-party DLLs. It uses only what ships with Windows Server 2025 / Windows 11:
.NET Framework 4.8, Windows PowerShell 5.1, and the RSAT `GroupPolicy` and `ActiveDirectory` modules.

> **Test against a lab domain before any real use.** The same exe works in the lab and in production;
> only `config.json` differs.

---

## Contents

- [Requirements](#requirements)
- [Install](#install)
- [Configure (`config.json`)](#configure-configjson)
- [The quarterly procedure](#the-quarterly-procedure)
- [Rollback](#rollback)
- [Phase 2: scan results as a promotion gate](#phase-2-scan-results-as-a-promotion-gate)
- [Where data lives](#where-data-lives)
- [Command line](#command-line)
- [How it decides link order](#how-it-decides-link-order)
- [Building from source](#building-from-source)
- [Design and limits](#design-and-limits)

---

## Requirements

| | |
|---|---|
| OS | Windows Server 2025 (DC or member) or Windows 11 with RSAT |
| Runtime | .NET Framework 4.8 and Windows PowerShell 5.1 (both built into these OSes) |
| Modules | `GroupPolicy` and `ActiveDirectory` (installed on DCs; RSAT elsewhere) |
| Rights | Member of **Domain Admins**, running elevated (the exe always asks for elevation) |
| Domain | Single forest; the Central Store (`\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions`) must exist |

**Where to install:** one machine per network is enough. Use the **PDC emulator** (the tool sends
every change to the PDC anyway); GPOs and SYSVOL replicate to the other DCs. The tool's own data
(`WorkingFolder`) is ordinary files and does **not** replicate, so include it in that server's
backups. If the PDC role moves, install the tool on the new PDC and copy `WorkingFolder` across;
open runs resume there.

**Accreditation:** an unsigned custom exe usually needs software approval, and on hosts that
enforce AppLocker/WDAC a publisher or hash rule. Sign the exe with your enclave code-signing
certificate if you have one. `build.ps1` writes the exe's SHA-256 for the transfer record.

## Install

1. Copy `StigGpoManager.exe` onto the server (through your approved transfer process).
2. Run `StigGpoManager.exe /install` (or just double-click it and answer **Yes** to *Install now?*).
   This:
   - copies the exe to `C:\Program Files\StigGpoManager\`
   - adds **STIG GPO Manager** to *Programs and Features* (Settings > Apps); uninstalling there runs `/uninstall`
   - adds an all-users Start menu shortcut
   - registers the scheduled task **StigGpoManager Tray**, which starts the tray icon at logon for
     Administrators with highest privileges, so there is no UAC prompt
   - creates `C:\ProgramData\StigGpoManager\` (Administrators and SYSTEM only) and writes a sample `config.json` there
3. Edit `C:\ProgramData\StigGpoManager\config.json` for this domain (see below), then open the tool
   from the Start menu or the tray icon. The Start page runs the pre-flight checks.

`/install /quiet` and `/uninstall /quiet` skip the message boxes. Uninstall removes the program,
shortcut, scheduled task and Programs-and-Features entry. It **keeps** the config and the whole
`WorkingFolder` (runs, backups, reports, audit log), because those are audit evidence.

**Tray icon:** a left-click or double-click opens the main window. Right-click shows the status of
the active run, *Refresh status* and *Exit*. Closing the main window while the tray is running
only hides it. The tray only reads run files; it never changes AD.

## Configure (`config.json`)

The tool looks for the config at `/config <path>` if given, then
`%ProgramData%\StigGpoManager\config.json`, then `config.json` next to the exe. It is validated
on load, and every problem is listed on the Start page with a clear message: bad JSON with its
line and column, single backslashes, invalid regexes, OUs outside the domain, a missing
`{Version}`, and so on.

```json
{
  "Domain": "corp.example.com",
  "TargetDC": "auto-pdc",
  "ExceptionPrefix": "SITE - ",
  "DisaGpoPrefix": "DoD ",
  "WorkingFolder": "D:\\StigGpoManager",
  "Language": "en-US",
  "PilotGroup": "GPO-Pilot",
  "WriteGpoComment": true,
  "SoakDays": 14,
  "RequireScanGate": true,
  "Mappings": [
    {
      "Match": "WinSvr 2025.*Member Server.*Computer",
      "NameTemplate": "DoD WinSvr2025 MS STIG Comp {Version}",
      "PilotTargets": ["OU=Pilot,OU=Server2025,OU=Servers,OU=Computers,OU=ORG,DC=corp,DC=example,DC=com"],
      "ProdTargets": ["OU=Server2025,OU=Servers,OU=Computers,OU=ORG,DC=corp,DC=example,DC=com"]
    }
  ]
}
```

| Setting | Meaning |
|---|---|
| `Domain` | DNS name of the domain. Every target DN must be inside it, so a production config cannot be run against the lab and vice versa. |
| `TargetDC` | `auto-pdc` resolves the PDC emulator; otherwise give a DC FQDN. Every GroupPolicy/AD cmdlet gets `-Server` set to this DC so all changes land on one DC. |
| `ExceptionPrefix` | Name prefix of site exception GPOs. These **always win**: they are kept above every DISA GPO on every OU. |
| `DisaGpoPrefix` | Name prefix of DISA GPOs (used for ordering and cleanup). Must not overlap `ExceptionPrefix`. |
| `WorkingFolder` | Where packages, backups, reports, run state and `audit.log` live. Local path or UNC. Double the backslashes. |
| `Language` | ADML language folder copied to the Central Store (`en-US`). |
| `PilotGroup` | Security group for group-based pilot (see `PilotMode`). |
| `CentralStorePath` | Optional override of the Central Store path. |
| `WriteGpoComment` | Write the source package, version, import date and run ID into the new GPO's comment. Default `true`. |
| `SoakDays` | Planned soak length, shown in the tray (`Pilot - day 9 of 14`). Default 14. |
| `RequireScanGate` | Phase 2: block Promote until pilot scan results are imported and pass. Default `true`; set `false` to make scan results optional (open findings still block if results are imported). |

**Mappings.** One per DISA GPO you deploy.

| Field | Meaning |
|---|---|
| `Match` | Case-insensitive regex against the backup's GPO display name in `bkupInfo.xml`. The first matching mapping wins. |
| `NameTemplate` | New GPO name. Must contain `{Version}` exactly once, e.g. `DoD WinSvr2025 MS STIG Comp {Version}` becomes `... v1r4`. The template also identifies older versions of the same GPO in AD. |
| `PilotTargets` | OUs to link during pilot (OU-based pilot). |
| `ProdTargets` | OUs to link at Promote. Required. |
| `PilotMode` | Optional: `OU`, `Group` or `None`. Default: `OU` if `PilotTargets` is set, else `Group` if `PilotGroup` is set, else `None` (linked straight to production at Promote, with a warning). |
| `Notes` | Free text. |

`{Version}` is parsed (`v\d+r\d+`, case-insensitive) from the backup's display name, falling back
to the product folder name. If neither has a version, the row is skipped until you type one in.

Keys starting with `_` are treated as comments. Unknown keys produce a warning, which catches typos.

## The quarterly procedure

The tool handles the AD-side steps. Humans do the rest.

| Phase | Who |
|---|---|
| Download the DISA package, record its hash, read the revision history | Human (connected network) |
| Lab test of the new release | Human, **using this tool** against the lab domain |
| Transfer into the enclave | Human (approved data transfer process) |
| Central Store update, GPO import, review diff | **Tool** (steps 1-4) |
| Pilot linking | **Tool** (step 5) |
| Pilot scans, functional testing, 1-2 week soak | Human. The run is saved and survives closing the app and rebooting. |
| Import scan results / gate | **Tool** (Scan results page) |
| Promote, trigger policy refresh | **Tool** (step 6) |
| Production scans and remediation | Human (SCC / Evaluate-STIG) |
| SSP / POA&M paperwork | Human, supported by the run report and audit log |
| Cleanup of old versions | **Tool** (step 8, following quarter) |

Nothing touches AD until you click a **Push** button. Every Push first shows the exact changes,
including the before and after link order on every OU, and asks for confirmation. **Promote**
requires typing `PROMOTE` and **Delete** requires typing `DELETE`.

**Start / pre-flight.** On launch the tool checks:
- the GroupPolicy and ActiveDirectory modules
- elevation and Domain Admins membership
- that the PDC is reachable and is the domain in the config
- that the Central Store exists, every target OU exists and the pilot group exists
- that the working folder is writable
- optionally, `dcdiag` SysVolCheck, DFSREvent, Advertising and Replications

It also lists runs. If exactly one run is open it is resumed automatically.

1. **Load package.** Drop the DISA zip on the window or browse to it. The SHA-256 is shown; paste the
   expected hash to get MATCH / MISMATCH. The zip is extracted to `WorkingFolder\packages\<zip-name>`
   (entries that would escape that folder are rejected) and scanned **by content**: every
   `bkupInfo.xml` (GPO backups) and every `*.admx` and `<Language>\*.adml`, wherever they are.
   The grid shows each backup's display name, detected version, Computer/User settings, matched
   mapping and proposed GPO name. **Unmatched backups are red and skipped**; you can assign them a
   mapping, or edit the version. This creates the run.
2. **Central Store.** Each package template is compared with PolicyDefinitions. If the same file
   appears more than once in the package, the newest wins. Status is *New*, *Updated*, *Unchanged*
   (hash match) or *Skipped - store is newer*; the store is never downgraded unless you tick
   *Override* for that file. Push zips the whole PolicyDefinitions folder to
   `WorkingFolder\backups\` first, then copies the files and checks each hash.
3. **Import.** Pre-flight `Backup-GPO -All` to `WorkingFolder\backups\gpo-<timestamp>`, then one
   `Import-GPO -CreateIfNeeded` per selected backup into a **new** GPO with the templated name.
   **If a GPO with that name already exists, the import stops**; nothing is ever imported over an
   existing GPO. The source package, version and date are written into the GPO comment. No links are
   created. The tool also records the previous version: same mapping, older version, preferably the
   one currently linked.
4. **Review / diff.** A setting-level diff of `Get-GPOReport -ReportType Xml` between the previous and
   new version: **Added, Removed, Changed (old → new)**. It is cross-checked against every exception
   GPO linked to the same OUs:
   - *Exception may need review*: DISA changed a setting that an exception GPO overrides.
   - *Exception may be unnecessary*: the exception's value now matches DISA's value.
   - **REPLACEMENT-TYPE** (red): User Rights Assignment and Restricted Groups are *replaced, not
     merged*, by the winning GPO. If DISA changed one that an exception also defines, the tool lists
     the STIG entries **missing** from the exception's list.

   It also warns when an exception GPO is linked only on a parent OU of a pilot OU (see
   [Pilot caveat](#pilot-caveat-inherited-exceptions)). The grid can be filtered and searched;
   HTML and CSV reports go to `WorkingFolder\reports\`.
5. **Pilot.** Links each new GPO to its `PilotTargets` (OU-based). For group-based pilot it links at
   the `ProdTargets` with security filtering: `PilotGroup` gets Apply and Authenticated Users drops
   to Read. On every OU the new GPO goes **directly above the previous DISA version and below all
   exception GPOs**. The order is then re-read and verified; if it is wrong, the tool fixes it and
   reports what it did. A checklist reminds you to run gpupdate, SCC / Evaluate-STIG scans, persona
   tests and the soak period. You can now close the app or reboot; the run is saved.
6. **Promote.** Blocking pre-checks run first (the scan gate). Then, for each GPO: link it at every
   `ProdTargets` OU with the same ordering rule, **disable (not remove)** the previous version's link
   on that OU, and remove pilot-only links, or restore Authenticated Users Apply for a group-based
   pilot. The order is verified on every target. You are then offered a policy refresh
   (`Invoke-GPUpdate -Force -RandomDelayInMinutes 0` on every computer under the production OUs),
   with success or failure shown per machine. Some settings still need a logoff or reboot.
7. **Rollback**. See below.
8. **Cleanup** (following quarter). Lists DISA GPOs that are **two or more versions** older than the
   newest of their mapping and have no enabled links. The newest version and the one before it are
   always kept, and GPOs referenced by open runs are protected. Each selected GPO is backed up with
   `Backup-GPO`, re-checked, then deleted. Requires typing `DELETE`.

Close a run from the Start page when the quarter is finished. It stays on disk and can still be reported on.

**Single-DC domains:** if a change touches the `Domain Controllers` OU and the domain has only one
DC, the confirmation shows a prominent warning and requires ticking *I confirm a fresh system-state
backup was taken and verified*. That confirmation is stored in the run state and the audit log.

### Pilot caveat: inherited exceptions

GPOs linked **directly** on an OU beat GPOs **inherited** from parent OUs. If an exception GPO is
linked on `OU=Server2025` and the new DISA GPO is linked on its child `OU=Pilot,OU=Server2025`, the
DISA GPO wins on pilot machines during the pilot. The review and the pilot confirmation both warn
about this. Either also link the exception GPO to the pilot OU (in GPMC) or use group-based pilot
(`"PilotMode": "Group"`), which links at the production OU next to the exceptions.

## Rollback

Rollback is available after Pilot and after Promote, for selected GPOs or the whole run. It uses the
run's **recorded** state, not guesswork. Before the run first changes an OU, the tool records the
OU's full link order and enabled flags. Rollback then:

1. **re-enables** the previous-version links that this run disabled, but only if they were enabled
   in the recording; this is done first, so the OU is never without a STIG GPO
2. **disables** (does not delete) the new version's links
3. **restores the recorded link order**. New GPOs from other mappings that are not being rolled back
   stay next to the GPO they sit above. Exception GPOs still end up on top.
4. verifies the order and logs every change

To roll back from the command line:
`StigGpoManager.exe cli rollback --run <id> [--gpo "<name>"] --whatif` and then `--yes`.

If the tool itself is unavailable, the manual fallback is the same: in GPMC, enable the old
version's link and disable the new one on each OU listed in the run report's *Link changes* table,
which records the order before and after each change.

## Phase 2: scan results as a promotion gate

- **Exceptions register** (`WorkingFolder\exceptions.json`, edited on the *Exceptions register*
  page): V-ID, STIG/benchmark name (empty matches any STIG), exception GPO, **justification
  (required)**, POA&M / risk-acceptance reference, date added and who added it. Each save keeps the
  previous version in `exceptions-history\`.
- **Import scan results** on the *Scan results* page: drop SCC / Evaluate-STIG output for pilot
  machines (**CKL, CKLB, XCCDF results**). Each file is hashed and copied into
  `WorkingFolder\runs\<runId>\scans\` as evidence. Per host and V-ID the worst status across files
  counts (Open > Not_Reviewed > NotAFinding / Not_Applicable).
- **Gate:** an open finding that is in the register is *expected*. **Any open finding not in the
  register blocks Promote** until you fix it and re-import results, or add it to the register with a
  justification (*Add selected findings to register...*). The panel shows PASS / FAIL.
- **Before/after:** import results from before the pilot link as *Baseline* to see which V-IDs
  changed status because of the new GPOs.
- **Evidence:** the run report (Start page, *Run report*) includes the imported files and their
  hashes, the gate outcome, blocking and expected findings, status changes, every link change with
  before/after order, confirmations, backups and the run's audit entries.

## Where data lives

```
%ProgramFiles%\StigGpoManager\StigGpoManager.exe
%ProgramData%\StigGpoManager\config.json        (Administrators/SYSTEM only)
<WorkingFolder>\
  audit.log                 append-only, one JSON object per line
  exceptions.json           exceptions register (+ exceptions-history\)
  packages\<zip-name>\      extracted DISA packages
  backups\                  PolicyDefinitions-*.zip, gpo-<ts>\ (Backup-GPO -All), deleted-<ts>\
  reports\                  review-*.html/.csv, run-*.html
  runs\<runId>.json         run state (written atomically; .bak keeps the previous copy)
  runs\<runId>\scans\       imported scan results (evidence)
```

**Audit log:** every AD change and every important action (package load, Central Store copy,
import, link changes, permission changes, gpupdate, deletes, confirmations, gate evaluations) is
appended as `{"timestamp","user":"DOMAIN\\user","machine","runId","action","target","result","error","detail"}`.
The file is only ever opened for append, never rewritten or truncated. The *Audit log* page shows it.

**Run state** records the package name and hash, the selected backups, the created GPO names and
GUIDs, the previous versions, every link created, enabled, disabled, removed or reordered (with the
order before and after), the per-OU snapshots used for rollback, confirmations, backups, reports,
scan imports and the gate result.

## Command line

The same exe is scriptable. Run it from an **elevated** console. Commands that change AD print their
plan and act only with `--yes` (`--confirm PROMOTE` / `--confirm DELETE` for those two). `--whatif`
prints the plan and never acts.

```
StigGpoManager.exe cli preflight [--dcdiag]
StigGpoManager.exe cli load D:\in\U_STIG_GPO_Package_October_2026.zip --hash <sha256>
StigGpoManager.exe cli runs
StigGpoManager.exe cli central-store --run <id> --whatif
StigGpoManager.exe cli import --run <id> --yes
StigGpoManager.exe cli review --run <id>
StigGpoManager.exe cli pilot --run <id> --yes [--confirm-dc-backup]
StigGpoManager.exe cli import-scan --run <id> --stage Pilot D:\scans\*.cklb
StigGpoManager.exe cli gate --run <id>
StigGpoManager.exe cli promote --run <id> --confirm PROMOTE
StigGpoManager.exe cli refresh --run <id> --yes
StigGpoManager.exe cli rollback --run <id> [--gpo "<name>"] --yes
StigGpoManager.exe cli cleanup [--delete "<name>" ... --confirm DELETE]
StigGpoManager.exe cli report --run <id>
```

Exit codes: 0 success, 1 error, 2 blocked or confirmation required. The CLI imports every matched
backup; use the GUI to skip or reassign individual backups.

## How it decides link order

Link order 1 has the highest precedence.

- On every OU, **every exception GPO is above every DISA GPO**. An exception found below a DISA GPO is
  moved up, directly above the first DISA GPO, keeping the exceptions' relative order, and the move
  is reported.
- A new DISA version goes **directly above the previous version** of the same mapping on that OU. If
  there is no previous version on the OU, it goes directly below the last exception GPO.
- All other links keep their relative order.
- An Enforced DISA link is reported, because enforcement would let it beat exception GPOs on child OUs.
- Moves are made with the minimum number of `Set-GPLink -Order` calls. The order is re-read after
  every change and verified at the end.

## Building from source

The tool must be built on a **connected** machine (NuGet supplies build-time reference assemblies
only). Copy only the resulting exe into the enclave.

- Windows: install the .NET 8 SDK and run `.\build.ps1`
- Linux/macOS: install the Microsoft .NET 8 SDK (not the distro package, which lacks the WindowsDesktop
  SDK) and run `./build.sh`

Both run the unit tests, build `src/App` for `net48`, and write `dist\StigGpoManager.exe` plus its
SHA-256.

```
src/Core/   AD-independent logic: config, version parsing, package discovery, mapping, Central
            Store compare, GPO report parsing and diff, exception cross-check, link ordering, run
            state, audit log, reports, scan parsers and gate, and the workflow engine (StigEngine)
            over the IGpoDirectory interface
src/App/    The exe: PowerShellDirectory (the only code that calls GroupPolicy/AD cmdlets),
            WPF GUI, tray icon, installer, CLI
tests/      xUnit tests. They compile the same src/Core files and run the whole quarterly cycle
            (import, review, pilot, gate, promote, refresh, rollback, cleanup) against an in-memory
            fake AD with GPMC link-order semantics.
```

## Design and limits

- **Separation:** all logic is in `src/Core` and talks to AD only through `IGpoDirectory`. Every
  operation that changes AD has a read-only `Plan*` counterpart, which drives the confirmation
  dialogs and the CLI's `--whatif`. Long operations run off the UI thread, and log lines (including
  each cmdlet invoked) stream to the log pane.
- **DISA GPOs are never edited.** The tool only creates new GPOs from backups, links them, reorders
  them, enables and disables links, sets security filtering for group pilot, and deletes old versions
  in Cleanup. It never creates or edits exception GPOs.
- **No network access** outside the domain. Reports are self-contained HTML.
- **Not yet verified against a real domain.** The core logic and the full workflow are unit-tested
  against a fake AD, and the exe is built and checked to reference only in-box assemblies. The
  PowerShell gateway (`src/App/PowerShellDirectory.cs`), the WPF UI, the tray and the installer have
  not been run on Windows yet. Run the whole quarterly cycle in the lab domain first, including
  rollback and cleanup, and check the audit log and run report.
- The diff understands security settings (account policy, user rights, security options, restricted
  groups, services, audit), Administrative Templates, advanced audit and registry settings. Other
  extensions (firewall, preferences, ...) are compared leaf by leaf, so changes are still shown,
  with less friendly names.
- For a GPO linked to OUs in other domains or sites the tool only manages the OUs in `config.json`.
