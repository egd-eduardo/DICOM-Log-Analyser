# Testing and recorded results

All data used in tests is synthetic (`tests/fixtures`, generated logs). Never test with real logs outside an
approved environment.

## Automated checks

`scripts/check.sh` runs, in order:

| Check | Tool |
|---|---|
| Secret and PHI scan | `scripts/check-secrets.py` (keys, tokens, credentials, non-synthetic UIDs/names, public IPs, forbidden file types) |
| Locked restore | `dotnet restore --locked-mode` (lock files must match) |
| Formatting and code style | `dotnet format --verify-no-changes` (`.editorconfig`) |
| Build with analyzers | `dotnet build -c Release`, `AnalysisLevel=latest-recommended`, warnings as errors, NuGet Audit |
| Unit and integration tests | xUnit, 135 tests (parsers, line reader, assembler, ingestion, rotation/truncation, search, query safety, bundle import attacks, path validation, configuration, HTTP security in both modes, end-to-end API, background queue failure isolation, viewer/administrator roles, 5-day file selection and schema upgrade, live view and live indexing) |
| Dependency vulnerabilities | `dotnet list package --vulnerable --include-transitive`; `npm audit` for the UI type-check tool |
| UI type check | TypeScript 7.0.2 `checkJs` over `Ui/app.js` |

CI (`.github/workflows/ci.yml`) runs the same script on Linux, builds and probes the container image, validates the
Compose file, and builds/starts the portable executable on Windows, macOS and Linux.

## Results recorded on 2026-11-04 (development container, Ubuntu 24.04, .NET SDK 10.0.114, 4 CPUs)

These checks were actually run; nothing below is assumed.

| Check | Result |
|---|---|
| Secret/PHI scan | Passed (83 files). A planted file with a real-looking UID, a non-synthetic name, a public IP, a private-key header and a password was correctly rejected (5 findings). |
| Locked restore, format, build | Passed, no warnings |
| Tests | 135 passed, 0 failed |
| Vulnerable packages (NuGet, transitive) | None reported |
| npm audit (TypeScript) | 0 vulnerabilities |
| UI type check | Passed, after fixing seven type errors it found |
| Portable publish | win-x64 (PE32+ x86-64), osx-arm64 (Mach-O arm64) and linux-x64 single-file packages built; lock files unchanged by cross-publishing |
| Portable smoke test (linux-x64 package) | Started from the extracted zip; listening socket only on 127.0.0.1; API without session returned 401; a second instance on the same data folder was refused |
| Browser test (Playwright, Chromium) | Added two sources, scanned, searched an accession number: 4 matches, 4 highlighted terms; switching the highlight colour applied immediately; event detail showed lines, bytes and verified original text; a search with no match showed the "No matching event found" explanation; dark and light themes rendered; no browser console errors |
| Container image | Built from the pinned digests (291 MB); refused to start without identity configuration; healthy under `--read-only --cap-drop ALL --security-opt no-new-privileges` as uid 1654; no identity 401, disallowed identity 403, wrong Host 421, allowed identity 200; read-only mount detected; paths outside `/sources` and Windows/UNC paths refused with explanations; Europe/Lisbon conversion worked; bundle upload indexed; container logs contained only ids and counts |
| Compose file | `docker compose config` valid; proxy published on 127.0.0.1 only; analyser on an internal network without a published port |
| Volume test | See [ARCHITECTURE.md](ARCHITECTURE.md#measured-performance-synthetic-data-this-build-environment): 300 MB, 1.78 million events in 105 s; identifier searches under 10 ms |
| Roles (central mode, Chromium over HTTPS through a test TLS proxy that injects the identity) | Viewer: only Overview and Search tabs, search with highlighting works, no Export button; administrator: all tabs and Export. All 11 administrator-only endpoints return 403 `admin_required` for a viewer before any request body is read, and the refusal is recorded. Container refuses to start without `DLA_ADMIN_USERS`; in the container a viewer gets 403 and an administrator 200 on maintenance |
| Time-zone pre-fill (Chromium with the browser in Europe/Lisbon and in America/New_York) | New-source and bundle fields pre-filled with the browser's zone and saved with it; a changed value (Asia/Tokyo) was kept; editing showed the saved value; the add form was pre-filled again after saving |
| File chooser (Chromium, folder with 8 daily files) | Latest 5 days pre-selected; ticking an older file showed "7 day(s) of 5 maximum" and disabled saving; a 4-day custom choice was saved and only its 4 files were searchable; a file added afterwards appeared as "new, not chosen" and in the overview gaps, while a deliberately unticked file stayed "not chosen" |
| Live view (Chromium, a writer appending 3 to 4 lines per second with CRLF, accents and exception traces) | New lines appeared within about a second; errors red with traces; highlight marked the term; accented text correct; scrolling up stopped following; "only matching lines" filtered; after truncation the view restarted from the new end. A line written seconds earlier was found by Search without a manual scan (live indexing, 5 s) |
| Concurrency stress | Three rounds of scans (30 MB plus fixtures) while six threads searched, read the overview and sources, and new sources were added: 0 errors |

### Intermittent scan failure (investigated 2026-11-04)

**Symptom.** In one of nine browser runs, the scan of the second source stopped with a `SqliteException`. Because
routine diagnostics omit exception messages, the cause was not captured, and the source was then shown as "never
scanned".

**Investigation.** The failure could not be reproduced in 27 further runs of the same browser flow in Chromium,
40 timed HTTP replays, or a concurrency stress test. Transactions were confirmed empirically to start as
`BEGIN IMMEDIATE`, so lock waits are honoured. Disk space was ample. The exact trigger therefore remains unknown.

**Defects found and fixed during the review**, any of which could produce the same symptom:

1. An error in one source aborted the whole scan, and the failing source recorded no status. Each source is now scanned
   in isolation; a failure is recorded on that source as `failed` with a reference, shown in the overview and in
   search coverage notes, and the other sources are still scanned.
2. A read error *after* a file was opened (truncated or rotated mid-read) aborted the whole source. It is now caught
   per file; the file is marked unreadable and retried at the next scan from its last committed offset.
3. Changing a source's time zone or format cleared its index from the web request, which could race a scan starting at
   that moment (constraint errors). The reset now runs in the background queue, after any running work.
4. Prepared insert statements were not disposed, and the optional `PRAGMA optimize` step could fail a completed scan.
   Both are fixed.

Diagnostics now include the numeric SQLite result code, and a transient lock (SQLITE_BUSY/LOCKED) is retried once.

**Verification after the fixes.** `scripts/check.sh` passed (101 tests, including new tests for failure isolation,
the transient-lock retry and the queued re-index behind a running scan); 10 browser runs and 20 HTTP replays showed
no failures; the rebuilt container re-indexed a source after a time-zone change saved while scans were queued.

**Remaining.** If the original trigger recurs, it now affects only the one source, is visible in the interface, and
the logged SQLite code identifies the cause.

## Validation with real logs (2026-11-04)

A real sample supplied by the owner (16 log files, 47 MB: one DICOM Router folder and two B3DWEB Gateway folders) was
processed in an isolated working folder outside the repository and deleted afterwards. Only aggregate counts and
masked line shapes were examined; nothing from the sample was committed. Results with the current build:

| Check | Result |
|---|---|
| Recognition | 16 of 16 files recognised, 0 unreadable, 0 unrecognised, 0 unparsed lines |
| Volume | 206 868 events (Router 57 746; Gateway 131 159 and 17 963), scanned in about 11 s |
| Levels | Gateway `INF`, `WARNING`; Router `INF`, `WRN`, `ERR`, `FTL` (fatal): all mapped, no unknown level tokens |
| Multi-line records | 35 461 continuation lines; none looks like a record header, so no format variant is hidden inside other records; largest record 25 lines; 6 499 records carry exception traces (11 in Gateway logs, grouped correctly) |
| Time stamps | All Router entries carry `+00:00` (UTC). Gateway entries have no offset: **each Gateway source needs its time zone configured**; the coverage notes flag this until it is set |
| Search | Five identifiers taken at random from the sample: 9 to 71 matches each, 4 to 67 ms; errors-only filter 48 ms |
| Finding fixed | One Router line has an empty component (`[FTL] : message`); its message kept a leading colon. Fixed and covered by a synthetic test |

The sample's Gateway and Router periods do not overlap, so the Gateway time zones could not be inferred from shared
DICOM UIDs and must be confirmed by the owner.

## GitHub Actions (CI)

| Run | Commit | Result |
|---|---|---|
| [1](https://github.com/egd-eduardo/DICOM-Log-Analyser/actions/runs/41914713868) | `0675431` | Linux, macOS (Apple silicon) and Linux package jobs passed. Windows: 2 of 101 tests failed because they used Linux-style paths (`/sources`, `/proc/self/mountinfo`); the product behaviour was correct. |
| [2](https://github.com/egd-eduardo/DICOM-Log-Analyser/actions/runs/41916306816) | `7525124` | All four jobs passed. On Windows, macOS and Linux the self-contained package was built, started, answered 401 without a session and was stopped; on Windows `Get-NetTCPConnection` confirmed a loopback-only listener. Linux: full check script, container build, refusal without identity settings, hardened container healthy with 401/403/200, Compose file valid. |
| [3](https://github.com/egd-eduardo/DICOM-Log-Analyser/actions/runs/41918125944) | `173a7af` | All four jobs passed (118 tests on Windows, macOS and Linux), including the container probe with `DLA_ADMIN_USERS` and the Compose check with the updated `.env.example`. |
| [4](https://github.com/egd-eduardo/DICOM-Log-Analyser/actions/runs/41921125491) | `ef826f4` | Record of run 3: all four jobs passed (Linux with container and Compose checks; packages built, started and probed on Windows, macOS and Linux). |
| [5](https://github.com/egd-eduardo/DICOM-Log-Analyser/actions/runs/41931817718) | `fec4fbb` | Real-log validation and empty-component fix (119 tests): all four jobs passed (Linux with container and Compose checks; packages built, started and probed on Windows, macOS and Linux). |
| [6](https://github.com/egd-eduardo/DICOM-Log-Analyser/actions/runs/41932728824) | `f03592f` | Time-zone pre-fill (119 tests): all four jobs passed (Linux with container and Compose checks; packages built, started and probed on Windows, macOS and Linux). |
| [7](https://github.com/egd-eduardo/DICOM-Log-Analyser/actions/runs/41934095365) | `51365ac` | 5-day file limit per folder with file chooser, schema v2 (130 tests): all four jobs passed (Linux with container and Compose checks; packages built, started and probed on Windows, macOS and Linux). |
| [8](https://github.com/egd-eduardo/DICOM-Log-Analyser/actions/runs/41934872734) | `7f2dedf` | Live view and automatic index updates (135 tests): all four jobs passed (Linux with container and Compose checks; packages built, started and probed on Windows, macOS and Linux). |

## Not run here (needs the target environments)

* Interactive use of the Windows and macOS packages by a person, and Windows SmartScreen / macOS Gatekeeper behaviour
  with unsigned binaries (CI starts the executables headless).
* oauth2-proxy with a real identity provider (the image registry was not reachable from this environment).
* Real logs from every site and software version in use (one real sample has been validated, see above).
