# DICOM Log Analyser

**Maintainer / developer attribution:** Eduardo Dias
**Application type:** browser-based log analyser with two deployment modes (portable local, central Docker)
**Platform:** .NET 10 LTS (ASP.NET Core, Kestrel), SQLite with FTS5, vanilla HTML/CSS/JavaScript UI (dark theme by default)
**Targets:** Windows 10/11 (x64, ARM64), macOS (Apple silicon, Intel), Linux x64/ARM64 for the container

> **Data protection notice.** Treat every original log, imported bundle, index and export as containing protected
> health information (PHI): patient names, IDs, accession numbers, DICOM UIDs, IP addresses and host names. This
> application processes everything on hardware you control. It sends nothing to external services, has no telemetry
> and no AI features. Read [Before using real logs](#before-using-real-logs) first.

## What it does

* Reads **B3DWEB Gateway** logs (`INF 2026.11.03 09:40:29 SPS - ...`) and **DICOM Router** logs
  (`2026-10-27 00:00:06.059 +00:00 [INF] DicomRouter.Dicom.RouterScp: ...`), including multi-line .NET exception
  traces. No other format (for example a PACS product log) is claimed until representative samples are supplied;
  parsers are modular ([docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#adding-a-log-format)).
* Streams files (never loads them into memory), strictly read-only, and indexes them incrementally: a rescan reads
  only what was appended. Handles rotation by rename, truncation, replaced files, partial last lines, unknown lines
  and unreadable files, and reports each case.
* Every event links back to its source file, line range and byte range; the detail view re-reads the original bytes
  and says whether the file is unchanged.
* Search by accession number, patient ID, Study/SOP Instance UID, AE title, IP address or any text: every term is
  found as a **substring anywhere** in the events (messages and stack traces), ignoring case and accents, so a fragment
  such as `840.1136` or `ACC000` is enough. Filters: level, source, **category** (a list of the categories present),
  time range. Matches are **highlighted in a colour you choose** (five presets or any custom
  colour); the choice and the dark/light background are remembered by the browser.
* The result summary keeps three situations apart: *no matching event found in the indexed logs* (which says nothing
  about success or failure), *errors were logged*, and *events found with no warning or error* (suggests, but does
  not prove, success). Coverage notes explain gaps: missing, disabled, unreachable or never-scanned sources, time
  ranges outside the indexed period, and logs whose time zone is unknown.
* **Exam history** (plain-language interpretation of one exam): enter an accession number, patient ID or Study Instance
  UID (or press *Exam history* on the Search tab). The summary states when the exam **entered the queue** (the first
  time the study was received), whether it was **updated** (a new series arrived after the first receipt), the
  **sends, resends** (repeated sends of the same series) and failures per destination with the last result, and when
  the **final series** arrived, with no further arrivals after it. Key facts, a per-destination table, the series and
  a timeline follow; every statement links to the log entries it is based on. Identifiers must match exactly
  (`SYNEXAM01` does not pull in `SYNEXAM010`), and entries that only name the study's UID (such as router lines) are
  joined automatically. The summary is available in **11 languages** chosen with flag buttons: English (default),
  Spanish (Spain), Portuguese (Portugal), French, Lithuanian, Hungarian, Italian, Greek, Turkish, Croatian and German;
  the choice is remembered by the browser. The interpretation is derived from the wording of the log entries
  (receipt, send, failure, retry and duplicate messages), so it is an aid to reading the evidence, not a verdict; the
  non-English texts were machine-written and should be reviewed by native speakers before clinical teams rely on them.
  Queries (C-FIND) and retrieve requests (C-MOVE) are shown in the timeline but never counted as receipts; activity
  before the first receipt is pointed out, since the study may then predate the indexed days. The recognition follows
  the B3DWEB Gateway wording (`Receiving`, `Adding upload job`, `Beg/End upload`, `C-STORE-RQ … Dest AET`).
  Administrators can export masked **message patterns** (Maintenance) to help tune the recognition.
* **Modality worklist** (Worklist tab), from **VIDAcare Connector** logs (log4net layout, usually in
  `VIDAcare\Connector\logs`; detected automatically). For each piece of equipment (calling AE title) it shows the
  addresses, the software it reports, the called AE, associations, connection tests (C-ECHO), queries, items sent and
  empty answers, the query keys (modality, Scheduled Station AE Title) and the Scheduled Station AE Title of the items
  sent; then the recent associations, each opening its original lines. It points out AE title inconsistencies:
  equipment that never receives an item, items or queries for another station's AE title, a worklist loaded for
  another AE, one AE title used from several addresses, one address using several AE titles, a called AE differing from
  the usual one, different software under one title, invalid or generic default titles, a title suggesting another
  vendor than the reported software, and failed associations. With live indexing it refreshes every 10 seconds. Query
  keys and items need the connector's DEBUG logging; the connector's own outgoing queries are left out.
* Overview: event, error and warning counts, per-source coverage, errors per day, categories/components, most
  frequent error patterns (identifiers masked) and files with read or parse problems.
* Secure import of log bundles (.zip, .gz, .log, .txt) from computers the analyser cannot reach.
* Deletion controls for sources, bundles, the whole index and temporary files; an activity record of who changed what.

## Choosing a mode

| | Portable local mode | Central mode (Docker Compose) |
|---|---|---|
| Runs on | Your Windows or macOS computer | An internal Linux server with Docker |
| Reaches | Folders this computer can open (local, mapped drive, UNC, /Volumes) | Folders mounted read-only into the container, plus uploaded bundles |
| Access | `http://127.0.0.1` only, this computer; no password | HTTPS through an identity-aware proxy using your organisation's SSO |
| Remote use | No | Only over a private, identity-controlled network (VPN, Tailscale/WireGuard, zero-trust gateway) |

Both modes are the same program with the same parser, index and interface.

---

## Portable local mode

### Download

Get the newest package from the **[Releases page](https://github.com/egd-eduardo/DICOM-Log-Analyser/releases/latest)**.
A release is published automatically after every merge to `main`, once CI has passed on Windows, macOS and Linux. Each
release has zips for Windows (x64, ARM64), Mac (Apple silicon, Intel) and Linux and a `SHA256SUMS.txt`. A build
provenance attestation is added when the repository is public (GitHub does not store attestations for private personal
repositories); it is then verified with `gh attestation verify <file> --repo egd-eduardo/DICOM-Log-Analyser`.

### Install and start (Windows)

1. Download `DicomLogAnalyser-win-x64.zip` (or `win-arm64`) and extract it to a folder you own, for example
   `%LOCALAPPDATA%\Programs\DicomLogAnalyser`.
2. Double-click **Start DICOM Log Analyser.cmd** (or `DicomLogAnalyser.exe`). A console window shows the status and
   your default browser opens, already signed in. No administrator rights and no .NET installation are needed.
3. Keep the console window open. Press **O** in it to open the interface again and **Q** to stop; closing the window,
   Ctrl+C or **Stop the analyser** (Maintenance tab) also stop it, within at most 10 seconds even during a scan.
   Starting it a second time simply opens the browser on the copy that is already running. Clicking inside the window
   no longer pauses the program (Windows QuickEdit is switched off for this window).

**Several people on the same computer** (for example a Windows server with Remote Desktop): each Windows user starts
their own copy. Each copy has its own data folder (`%LOCALAPPDATA%\DicomLogAnalyser`), its own random port and its own
sign-in, so copies do not interfere and one user cannot open another user's copy: the loopback port is reachable by
other sessions on the computer, but it accepts only the one-time sign-in link printed in its owner's window and the
session cookie that follows. Each copy builds its own index of the same read-only log files, so disk use and the first
scan are multiplied by the number of users; for a shared team view use the central mode instead.

The packages are not code-signed yet: Windows SmartScreen may show "Windows protected your PC"; choose **More info**,
then **Run anyway** (first time only). Use a build signed with your organisation's certificate for wider distribution.

### Install and start (macOS)

1. Download `DicomLogAnalyser-osx-arm64.zip` (Apple silicon) or `osx-x64` (Intel) and extract it, for example into
   `~/Applications`.
2. The build is not notarised. Remove the download quarantine once:
   ```bash
   xattr -dr com.apple.quarantine ~/Applications/DicomLogAnalyser-osx-arm64
   ```
3. Double-click `start.command` (or run `./DicomLogAnalyser` in Terminal). The browser opens, already signed in.

### Options

```text
DicomLogAnalyser [--port N] [--no-browser] [--data-dir PATH] [--network-read-limit MB] [--live-interval SECONDS]
```

* `--port` fixes the port (default: a random free port). The server always listens on `127.0.0.1` only.
* `--no-browser` prints a one-time sign-in link instead of opening the browser.
* `--data-dir` changes where the index is stored. Default: `%LOCALAPPDATA%\DicomLogAnalyser` (Windows),
  `~/Library/Application Support/DicomLogAnalyser` (macOS), `~/.local/share/DicomLogAnalyser` (Linux). It must be a
  **local disk**: the program refuses network locations, because SQLite must not live on a network share.
* `--network-read-limit` caps how fast logs on a **network share** are read during a scan, in MB per second
  (default 2, `0` = no limit; also `DLA_NETWORK_READ_LIMIT_MB`), so that a scan over Wi-Fi or a VPN leaves bandwidth for
  video calls. Local folders are not limited.
* `--live-interval` sets how often chosen files are checked for new lines (default 5 seconds, `0` = off).

### Sharing the computer with other work (video calls, viewers)

Scanning is the only heavy work: the first scan of a folder (and a scan after a fresh session or a time-zone change)
reads every chosen file and builds the substring index, about one CPU core and roughly 4 times the final index size in
disk writes while it runs. To keep calls and other programs smooth:

* Scans run on their own thread at **background priority** for CPU and disk (Windows background mode, macOS background
  QoS, Linux nice 19). Other programs win whenever they need the processor or the disk; the interface keeps normal
  priority. Measured on 4 cores with 100 MB of logs: a competing program running on every core kept 73 % of its speed
  during a scan before this change and 91 % after it.
* Logs on **network shares** are read at up to 2 MB/s by default (see `--network-read-limit`).
* The browser page stops polling while it is hidden behind other windows and catches up when shown again.
* At rest (folder indexed, live indexing on) the program uses about 0.4 % of one core and under 100 MB of memory.

If a call still suffers during a first scan, start the scan after the call or press **Cancel scan**: the next scan
continues where it stopped.

### How local sign-in works (no password)

The program opens your browser with a one-time link (valid 2 minutes, usable once) that creates a session cookie
for `http://127.0.0.1:<port>`. Other web pages, other local web servers and other programs cannot use the API: the
server checks the exact Host header, Origin and Fetch Metadata headers, and requires a per-session CSRF token for
every change. Details: [docs/SECURITY.md](docs/SECURITY.md).

---

## Adding sources

A **source is one folder with log files**: add one for each folder you want to analyse (for example the Gateway folder
and the DICOM Router folder of each site); all sources are searched together. Open **Sources**, choose **Browse…** (or
type the path), check the label, then **Add folder and scan**; the form then invites you to add the next folder.
In local mode **Browse…** opens the folder dialog of your own computer (Windows Explorer dialog, macOS Finder dialog,
or zenity on Linux desktops; it may open behind the browser window); without a dialog, and in central mode, an in-app
folder browser is shown (in central mode it lists only the read-only mounted folders). The check
reports whether the folder exists, is readable, how many files it contains, whether subfolders were inaccessible,
and whether it is a mapped drive or a read-only mount. Sources can be relabelled, enabled/disabled, rescanned and
deleted; deleting a folder source removes only its index, never the original files.

**At most 5 days of files per folder.** Each folder source analyses only the files you choose, and their
last-modified dates must lie within 5 consecutive calendar days (counted in the source's time zone). When you add a
folder, the files of its most recent 5 days are pre-selected and the **file chooser** opens; reopen it later with
**Files** in the source list. Chosen files are read completely. Unticking a file removes its events from the index.
Files that appear later are listed as *new, not chosen* (and flagged in the overview and search notes) until you tick
them. A chosen file keeps being analysed after log rotation, under its original name. Imported bundles are not limited:
you choose their content when you create the zip.

**Check the time zone** for B3DWEB Gateway sources: their timestamps have no UTC offset. For a new source or bundle
the field is pre-filled with the time zone of the computer running the browser (for example `Europe/Lisbon`) when the
server recognises it, otherwise with the server's own time zone read from its operating system; change it when the
logs were written elsewhere. The list offers every name the server accepts. Both forms work: IANA (`Europe/Lisbon`) and
Windows (`GMT Standard Time`). Windows Server 2016 cannot translate IANA names by itself (it lacks the ICU library);
a built-in table covers the European zones, and Windows names always work there. Editing a source shows its saved time zone.
Without it, their times are shown as written and flagged as "local, zone unknown". DICOM Router timestamps include
their offset and are always placed correctly on the UTC timeline. Changing the time zone re-indexes the source.

| Where the analyser runs | Example paths |
|---|---|
| Windows, local disk | `C:\3dnet\DicomRouter\logs` |
| Windows, mapped drive | `Z:\3dnet\audits\Gateway\0` |
| Windows, share | `\\server\share\logs` |
| macOS | `/Volumes/ClinicalLogs/Gateway/US` |
| Linux | `/var/log/dicomrouter` |
| Docker (central mode) | `/sources/gateway-site-a` (a read-only volume, see below) |

Notes:

* **Mapped drives versus UNC paths.** A drive letter such as `Z:` exists only in the signed-in user's session. If the
  analyser ever runs as a Windows service or under another account, use the UNC path (`\\server\share\...`). The
  path check warns when it sees a mapped network drive.
* **Share credentials** are never stored by the analyser. Access uses the permissions of the account running it
  (Windows domain account, macOS Keychain-backed SMB mount, or a host-level mount for Docker).
* **File name patterns** default to `*`. Archives, binaries, databases and DICOM files are skipped automatically; files
  whose first 200 lines match no supported format are reported as *unrecognised* rather than indexed as noise.
* Symbolic links and junctions inside a source are not followed.

## Live view and automatic updates

* **Live** tab: follows a chosen file as it is written, like BareTail or `tail -f`. Pick a source and file; the last
  200 to 5 000 lines are shown and new lines appear every second. The view follows the end; scroll up to read and it
  stops following, scroll back to the end and it follows again. **Pause** and **Clear** are available. Errors are red
  and warnings amber; **Highlight** marks terms in your highlight colour, optionally showing only matching lines. A
  partly written last line appears once complete; a truncated, rotated or replaced file is picked up again from its end.
  The file is read directly (not from the index) and only read.
* **Automatic index updates:** while the analyser runs, the chosen files of enabled folders are checked every 5 seconds
  (size and date only) and new lines are indexed incrementally, so Search and the Overview stay current without pressing
  Scan. Change the interval with `--live-interval SECONDS` (or `DLA_LIVE_INTERVAL_SECONDS`; 0 turns it off).
* **Encoding:** UTF-8 by default (with or without byte order mark); a line that is not valid UTF-8 is read as
  Windows Latin-1, so accented characters from older tools still display.

### Why there is an index (and nothing to install)

The live view reads files directly. Search, filters, the overview and the links back to original lines use a local
index: a single SQLite file in the data folder, built into the program. There is no database server, service, account
or password to manage. Without an index each search would re-read every chosen file (seconds instead of milliseconds)
and the overview would not be possible.

## Importing a bundle from an unreachable computer

On the computer that has the logs, create a zip (read-only; the original files are not changed):

```powershell
# Windows PowerShell (from the package's scripts folder or the repository)
./scripts/collect-logs.ps1 -Source 'C:\3dnet\DicomRouter\logs' -Destination "$env:USERPROFILE\Desktop\router-site-b.zip" -Days 14
```

```bash
# macOS / Linux
./scripts/collect-logs.sh /Volumes/ClinicalLogs/Gateway/US ~/Desktop/gateway-us.zip 14
```

Transfer the zip only on approved, encrypted media or channels. In the analyser, open **Import bundle**, give it a
label and time zone and upload it. The upload is streamed to disk and checked before anything is kept: unsafe paths
(`..`, absolute paths, drive letters), links, encrypted entries, duplicate names, oversize files and decompression
bombs (actual expanded size and compression ratio) are rejected; non-log files are skipped and listed. Accepted files
are stored read-only under the data folder and indexed. Delete the zip from the transfer medium afterwards.

---

## Central mode (Docker Compose)

### Prerequisites

* A Linux server on the internal network with Docker Engine and the Compose plugin, a local disk for the data volume,
  and a backup system you already trust for PHI.
* **An identity provider** with OpenID Connect (Microsoft Entra ID, Google Workspace, Okta, Keycloak, ...). Register an
  application with redirect URI `https://<your host name>/oauth2/callback`. Users then sign in with the organisation's
  existing SSO; passwordless methods (passkeys, Windows Hello for Business, FIDO2 keys) and MFA are configured at the
  identity provider, not in the analyser.
* A TLS certificate for the host name, issued by your organisation's CA.

There are **no application-managed passwords and no anonymous mode**. The analyser refuses to start in central mode
unless it knows the public HTTPS origin, the trusted proxy address, an allow list of users or e-mail domains and at
least one administrator.

### Roles

| Role | Can do | Who |
|---|---|---|
| Viewer | Overview, search, event details with original evidence. Source folder paths are hidden. | Every allowed user in central mode |
| Administrator | Everything else: add/edit/delete sources, scan, import bundles, CSV export, maintenance, purge, activity record | The person using local mode; in central mode only the addresses in `ADMIN_USERS` (`DLA_ADMIN_USERS`) |

CSV export is administrator-only because it is a bulk extraction of PHI. Refused attempts are recorded in the activity
record. Roles are checked by the analyser before a request is processed, in addition to the proxy's sign-in.

### Architecture

```text
browser ──HTTPS──> access-proxy (oauth2-proxy v7.15.5: TLS, OIDC sign-in, allow list)
                        │  internal Docker network (no internet access), fixed address 172.30.0.10
                        ▼
                   analyser (no published port; trusts identity headers from 172.30.0.10 only)
                        │
          /data (named volume: index, bundles)     /sources/* (read-only bind mounts)
```

### Steps

```bash
cd deploy/docker
cp .env.example .env            # then edit PUBLIC_ORIGIN, OIDC_*, ALLOWED_*, ADMIN_USERS (see comments in the file)
mkdir -m 700 secrets            # put tls.crt, tls.key, oidc_client_secret, cookie_secret, allowed-emails.txt here
```

```bash
openssl rand -base64 32 | tr -- '+/' '-_' > secrets/cookie_secret
```

```bash
docker compose up -d --build
```

```bash
docker compose ps               # the analyser must show "healthy"
```

See [deploy/docker/secrets.README.md](deploy/docker/secrets.README.md) for the secret files. They belong to the access
proxy; the analyser itself holds no secrets.

### Log folders as read-only volumes

A container can only see folders that the Docker host mounts into it. It cannot open `Z:\...`, `\\server\share` or
`/Volumes/...` directly. For each log location:

1. Make it available on the Docker host. For a Windows share, mount it with CIFS on the host, preferably with Kerberos
   (`sec=krb5`) or a root-only credentials file (`chmod 600`), never with a password in `compose.yaml`:
   ```text
   # /etc/fstab
   //fileserver/3dnet-logs  /mnt/logs/gateway-site-a  cifs  ro,credentials=/etc/dicomlogs.cred,uid=1654,gid=1654,file_mode=0440,dir_mode=0550,vers=3.1.1,seal  0  0
   ```
2. Add a read-only bind mount under `/sources` in `compose.yaml`:
   ```yaml
   - /mnt/logs/gateway-site-a:/sources/gateway-site-a:ro
   ```
3. Recreate the container (`docker compose up -d`) and add the source `/sources/gateway-site-a` in the interface.
   Paths outside `/sources`, and paths through symbolic links, are refused. The path check confirms "Mounted read-only".

### Remote access

Remote access is **off by default**: the HTTPS port is published on `127.0.0.1` only. To allow access:

* From the site network: set `PUBLISH_ADDRESS` in `.env` to the server's internal address.
* From outside the site: give the server an address on a private, identity-controlled network (corporate VPN,
  Tailscale or WireGuard with device and user policies, or a zero-trust access gateway) and publish on that address.
  Users must pass both the network's device/user policy and the SSO sign-in.

Never forward the port from the internet, and do not put the site behind a public tunnel without an identity policy.
If your environment cannot provide an identity provider and a private network without falling back to shared or
application passwords, keep remote access disabled: the analyser deliberately has no weaker mode.

Note that a cloud-hosted access gateway (for example a SaaS zero-trust proxy that terminates TLS) sees the traffic,
which contains PHI. Prefer gateways that do not terminate TLS outside your control, or obtain approval for that
processing first.

### Concurrency and database choice

The index is SQLite in WAL mode on the data volume: one writer (scans and imports are queued and run one at a time)
and many concurrent readers. This suits one analyser instance with a few to a few dozen users and up to a few GB of
index. Use a server database (for example PostgreSQL) only if several analyser instances or hosts must share one
index; that is not implemented in this version.

---

## Storage, backups, retention and deletion

| Item | Local mode | Central mode |
|---|---|---|
| Index (`index/index.db*`) | data folder | `analyser-data` volume |
| Imported bundles (`bundles/`) | data folder | `analyser-data` volume |
| Temporary uploads (`tmp/`) | deleted after each import and at start; **Maintenance > Delete temporary files** | same |
| Activity record | inside the index database (kept through a purge) | same |

* **Backups.** The index can be rebuilt from the sources by scanning again; imported bundles cannot. Back up the data
  folder/volume only to encrypted storage approved for PHI. Stop the analyser first (or use `sqlite3 index.db ".backup"`)
  so the database and its WAL file are consistent:
  ```bash
  docker compose stop analyser
  ```
  ```bash
  docker run --rm -v dicom-log-analyser_analyser-data:/data:ro -v "$PWD":/backup alpine tar czf /backup/dla-data.tgz -C /data .
  ```
  ```bash
  docker compose start analyser
  ```
* **Fresh session (on demand).** **Maintenance > Fresh session** clears everything the analyser has collected: all
  indexed events (the database is compacted, so removed content does not remain in its free space or write-ahead log),
  temporary files, imported bundles (optional, on by default) and the analyser's in-memory state, then analyses the
  newest 5 days of each folder again. Folder sources keep their paths and time zones (optional); the activity record is
  kept unless you choose to clear it. Original log files are never touched.
* **Retention.** Nothing expires automatically. Apply your retention policy with the deletion controls: delete a
  bundle or source (**Sources** / **Import bundle**), or delete the whole index (**Maintenance**). Deleting compacts the
  database. Remember that backups and CSV exports keep their own copies.
* **Exports.** CSV exports are generated on demand and not stored by the analyser; they contain PHI once downloaded.
* **Complete removal.** Local: stop the program and delete the data folder. Central: `docker compose down -v`.

## Updates

* Local: download the new package, stop the old one, replace the program folder. The data folder is kept; a newer
  index format is upgraded or the program asks you to rebuild it.
* Central: `git pull`, review [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md), then `docker compose up -d --build`.
  Update the base images and oauth2-proxy when security releases appear (.NET releases monthly on Patch Tuesday).

## Security limitations

* No independent penetration test or clinical validation has been performed.
* Local mode trusts the operating-system account: anything running as the same user can read the data folder and
  the index. Use full-disk encryption (BitLocker, FileVault) on computers that hold indexes or bundles.
* The index is not encrypted by the application; protection relies on file permissions and disk/volume encryption.
* Search terms travel in URLs between the browser and the analyser (never to third parties). Proxy request logging is
  disabled in the provided Compose file; keep it disabled in any other proxy you place in front.
* Error-pattern masking in the overview is heuristic and not de-identification.
* Detailed controls, OWASP ASVS mapping and residual risks: [docs/SECURITY.md](docs/SECURITY.md).

## Before using real logs

1. Have the information-governance/security owner approve the deployment, data location, backups and retention.
2. Confirm full-disk or volume encryption on every computer or server holding indexes, bundles or exports.
3. Central mode: complete the identity-provider registration with MFA/passwordless, restrict the allow list, pin the
   oauth2-proxy image digest, and test sign-in, sign-out and refusal of a non-allowed account.
4. Verify the parsers against a representative, approved sample from each site (formats can differ by version), and
   check time zones for each Gateway source.
5. Run `scripts/check.sh` and the CI pipeline on the exact version you deploy.

## Documentation

| Topic | Document |
|---|---|
| Architecture, data model, adding a format | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Security model, OWASP ASVS mapping, residual risks | [docs/SECURITY.md](docs/SECURITY.md) |
| Dependencies, versions and verification date | [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md) |
| Checks and recorded results | [docs/TESTING.md](docs/TESTING.md) |

## Build from source

```bash
./scripts/check.sh                  # secret/PHI scan, format, build, tests, vulnerability audit, UI type check
```

```bash
./scripts/package.sh win-x64        # also: win-arm64, osx-arm64, osx-x64, linux-x64, linux-arm64
```

```bash
./scripts/release.sh                # all release packages and SHA256SUMS.txt into artifacts/release/
```

Requires the .NET 10 SDK (see `global.json`), Node.js 22 (UI type check only) and Python 3 (secret scan).

## Licence

No open-source licence has been selected. Unless and until the copyright holder adds one, all rights are reserved.
Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
