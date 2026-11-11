# Architecture

## Components

```text
src/DicomLogAnalyser.Core     Parsing, streaming ingestion, SQLite index, search, overview, bundle import, path checks
src/DicomLogAnalyser.Web      ASP.NET Core host (local or central mode), security middleware, API, scan queue, embedded UI
tests/DicomLogAnalyser.Tests  Unit and integration tests (real Kestrel host on a random loopback port)
tests/fixtures                Synthetic logs only (see tests/fixtures/README.md)
deploy/docker                 Dockerfile, compose.yaml, .env.example
scripts/                      check.sh, package.sh/.ps1, check-secrets.py, collect-logs.ps1/.sh
tools/ui-check                Development-only TypeScript type check of the UI JavaScript
```

One executable serves both modes. `--mode local` (default) binds to `127.0.0.1` and opens the browser; `--mode
server` (set by the container image) expects an identity-aware proxy in front. Everything below the HTTP layer is
shared.

### Why this stack

* **.NET 10 LTS** produces self-contained single-file executables for Windows and macOS, so the portable mode needs no
  runtime installation, and the same code runs in a small, shell-less chiseled Linux container. It is also the stack of
  the sibling project (DICOM CD PACS Transfer), which keeps conventions and review effort aligned.
* **SQLite with FTS5** (via Microsoft.Data.Sqlite, the only runtime dependency) is an embedded, file-based index that
  needs no server, handles the 100 MB to 1 GB workload and supports concurrent readers with one writer (WAL).
* **Vanilla JavaScript UI** embedded in the executable: no npm runtime dependencies, no CDN, strict CSP.

## Ingestion pipeline

```text
FileEnumerator ──> per file: fingerprint check ──> LineReader (byte offsets) ──> ILogFormat.TryParseHeader
                                                                              ──> RecordAssembler (multi-line)
                                                                              ──> batched INSERT (+ FTS5 trigger)
```

* **LineReader** reads 64 KB blocks from a `FileStream` opened with `FileAccess.Read` and
  `FileShare.ReadWrite | Delete` (the logging service keeps writing and rotating). Each line carries its line number
  and exact byte range. CRLF/LF, a UTF-8 BOM, invalid UTF-8 (Latin-1 fallback) and over-long lines are handled.
* **Format detection**: the first 200 lines are tested against every registered format; the best match wins. Files
  with no match, binaries and archives are reported, not indexed.
* **RecordAssembler**: a header line starts a record; following non-header lines (stack traces, `--->` inner
  exceptions, wrapped text) are continuation lines. Lines before the first header or beyond the limits become
  *unparsed* events, so nothing is silently dropped and parse failures are countable.
* **Incremental state per file** (`files` table): size, modification time, SHA-256 of the first 4 KB, committed byte
  offset and line number. Everything before the committed offset is final. The **last record is always provisional**:
  it is indexed (searchable) but re-parsed on the next scan, which handles both partial final lines and stack traces
  still being written. Large files are committed every 20 000 events, so a cancelled scan resumes where it stopped.
* **Rotation and truncation**: a file shorter than its committed offset, or whose first block changed, is re-indexed
  from the start. A known file that disappeared or changed content, and a new path whose first block matches its
  fingerprint, is treated as a rename (`app.log` to `app.log.1`) and keeps its index. Files that vanish keep their
  events and are reported as *missing*.

## File selection (at most 5 days per folder)

Folder sources store the chosen files (`sources.selected_files`) and the files present when the choice was made
(`selection_seen`). `FileSelection` lists candidates with their last-modified date in the source's time zone, proposes
the most recent 5 calendar days, and validates any choice (at most 5 consecutive calendar days, files must exist). At
each scan, only chosen files are processed; files no longer chosen have their events and state removed; unchosen files
that were not present at choice time are counted as *new* (`new_files`) and reported. Log rotation keeps both the
renamed file (its indexed content) and the chosen name. Bundles have no selection and are indexed completely. Schema
version 2 adds these columns; a version 1 index is upgraded in place.

## Choosing folders

`IFolderPicker` shows the operating system's folder dialog on the computer running the analyser, in local mode only:
the Windows `IFileOpenDialog` (shared with DICOM CD PACS Transfer), AppleScript `choose folder` on macOS, zenity on
Linux desktops. The path comes from the dialog, never from the request, and is then validated like a typed path.
`FolderBrowser` lists sub-folders for the in-app browser (fallback, and central mode, where it is confined to the
mounted source roots and does not follow links). Both endpoints are administrator-only.

## Live view and live indexing

* `TailReader` serves the Live tab: the last N complete lines of a file (reading at most 2 MB from the end), then the
  complete lines written after a byte offset. A partial final line is withheld until its line break arrives; a file
  shorter than the offset is reported as reset. Files are opened read-only with sharing for each request and never
  held open. Only files chosen for analysis (or a bundle's files) can be opened, resolved inside the source folder,
  never through links.
* `LiveIndexer` (hosted service) checks size and modification time of chosen files every `--live-interval` seconds
  (default 5) and queues a quiet incremental scan for a changed source (deduplicated, no activity-record entry, no
  progress bar). Polling is used instead of file-system notifications because network shares do not deliver those
  reliably.

## Normalised event model (`events` table)

| Field | Meaning |
|---|---|
| `file_id`, `source_id` | Where it came from |
| `line_start`, `line_end`, `byte_start`, `byte_end` | Exact evidence location (byte range includes continuation lines) |
| `ts`, `utc_known`, `ts_text` | Milliseconds since epoch (UTC when the offset or the source time zone is known, otherwise wall-clock), plus the timestamp as written |
| `sort_ts` | `ts`, or the previous record's time for unparsed blocks; used for ordering and time filters |
| `level`, `level_text` | Normalised level (Trace..Fatal, Unknown) and the original token (`INF`, `WARNING`, `ERR`, ...) |
| `category`, `component` | Gateway category (`SPS`, `QRY`, ...) or the router component's last segment (`RouterScp`) and full name |
| `message`, `detail`, `has_exception` | First line, continuation lines, exception trace detected |
| `kind` | `0` record, `1` unparsed block |

`events_fts` is an external-content FTS5 table with the **trigram** tokenizer (diacritics removed) over message,
detail, category and component, maintained by triggers. Every search term of 3 or more characters is matched as a
substring anywhere (inside UIDs, IDs and words); shorter terms use a plain `LIKE` scan. Schema version 3 replaced the
earlier word index and rebuilds it in place on upgrade. The trigram index is larger (about 6 times the indexed log
volume, against about 3 times before) and the first scan slower; see the measurements below.

## Search semantics

* All terms must match, each as a substring; `"..."` keeps spaces in a phrase, a leading `-` excludes; the category
  filter is an exact match on the chosen category.
* Text searches start from the FTS index (identifier look-ups take milliseconds on millions of events); unfiltered
  pages use the time index. Results use keyset pagination on `(sort_ts, id)`. Counts are capped at 200 000.
* Each response includes per-level counts and **coverage notes** computed from source state, so the UI can state
  whether "no result" might be caused by a gap rather than by absence of the event.

## Activity over time

* `GET /api/activity?hours=24|168|720|all&source=` (`ActivityService`) reads `ts`, level and the first 400 characters of
  each message in the period through the `sort_ts` index, inside one read snapshot, and counts per slot. The slot size
  is the smallest of 5 minutes to 30 days giving at most 100 slots; the period ends at the newest event and starts no
  earlier than the first.
* `ActivityClassifier` assigns at most one activity per event by wording, in this order: responses excluded,
  association accepted, query, C-STORE being sent, retrieval, received, sent. Continuation lines (PDU dumps) are not
  classified. Errors and warnings are counted by level, independently of the activity.
* Slots without any event between the first and last event that together last an hour or more are reported as silent.
* Results are cached per period and source until the index changes (`DataVersion`). Measured: 955 000 events in 1.3 s,
  4 ms from the cache.

## Find in files

* `GET /api/find?q=&sources=` streams the chosen files of each enabled source (bundle files too) through `LineReader`
  (UTF-8 with Latin-1 fallback), without the index and without the read pacer: the user is waiting.
* Matching folds every character through a 65 536-entry table (`TextFolding`: lower case, accents removed through
  Unicode decomposition, plus đ, ı, ø, ł, ħ), so it runs at plain-text speed; the browser folds the same way to mark
  the terms. Terms are parsed as in Search (all must match, quotes, `-` exclusion).
* Limits: 500 matching lines (the last ones still get their lines after), 60 seconds, two searches at a time (429
  otherwise), cancelled when the browser disconnects. Lines over 4 000 characters are shortened for display.

## Exam history

`ExamHistoryService` (Core, `Exam/ExamHistory.cs`) builds the history of one exam from the index, with the definitions
agreed for this product:

* **Entered the queue**: the first entry recognised as a receipt of the study.
* **Updated**: a new series received in a later *arrival wave*. Receipts closer than 2 minutes belong to the same wave
  (one transfer); a later wave that brings a series not seen before is one update.
* **Resent**: a further send result for the same series (or for the study, when the log does not name the series) to
  the same destination.
* **Final**: the last series receipt; no further arrivals after it. The last related entry is reported separately.

Entries are classified in this order: duplicate, retry, **query** (C-FIND), **queued** (a send or upload job),
send failed, sent, send attempt (C-STORE, upload), **retrieve request** (C-MOVE, C-GET), received, other. Queries and
retrieve requests never count as receipts, so repeated queries do not create false updates. Zero counters such as
`0 Fail`, `0 Warn` or `Warnings: 0` are ignored when looking for failure words. The series is read from `SeriesInstanceUID`
or `SeUID`, the study from `StudyInstanceUID`, `StudyInstanceUid -` or `StUID`, the destination from `to X`,
`destination X` or `Dest AET X`; uploads are reported under "Cloud upload". A send without a series UID after an update
is counted as a new send, not a resend. Queries, retrievals or sends before the first receipt are reported, because they
show that the study existed before the indexed period.

Collection uses the substring search, then keeps only entries in which the identifier appears as a whole token
(`(?<![A-Za-z0-9.])id(?![A-Za-z0-9])`), and adds entries that name only a Study Instance UID found in those entries (at
most 10 UIDs, 5 000 entries; beyond that the result is marked truncated). Each entry gets a milestone (received,
queued, send, send failed, sent, retry, duplicate, other) from generic English wording and its level, plus the series
UID and destination when written. All regular expressions have a 250 ms timeout. The API (`GET /api/exam?q=`) returns
facts and event ids only; the browser writes the narrative from `exam-i18n.json` (11 languages, identical keys and
placeholders, checked by a test) and shows the flags from `/flags/*.svg`, both embedded and served from the fixed
allow-list. `GET /api/maintenance/patterns.csv` (administrators) exports message patterns with digits, UIDs, GUIDs,
quoted text and `SURNAME^GIVEN MIDDLE` names (up to the modality code) masked, to tune the wording rules against real logs.

## Modality worklist (provider logs)

The interface is provider-neutral: it speaks of "the worklist provider" and shows the provider only as a value.
`WorklistProviders` lists the supported providers (name, log format, usual log folder); today one entry, VIDAcare, whose
format is `VidacareConnectorFormat`. A further provider needs its log format and one entry; `WorklistAnalysis` reads
the events of every provider format and reports the provider of each association.

`VidacareConnectorFormat` reads `yyyy-MM-dd HH:mm:ss,fff [thread] LEVEL Logger - message | memory`: the thread becomes
the component, the logger the category, and the trailing memory counter is removed; dataset dumps and association
details are continuation lines. `WorklistAnalysis` (Core, `Worklist/`) reads the events of files in this format for the
chosen period (one read snapshot, at most 400 000 events) and rebuilds each association: lines carrying `AE(n)` or
`Association(n)` link to association *n* of that file ("accepted" starts a new one, as numbers restart with the
connector); other lines link through the thread serving the association. It takes the calling and called AE from
`A-ASSOCIATE-RQ X >> Y`, the address from "accepted", the software from `implVersionName`, queries and echoes from
`C-FIND-RQ` and `C-ECHO-RQ`, the query keys from the `DicomServiceRegistry` dataset, one item per `C-FIND-RSP` with
status `FF00H`/`FF01H` (its dataset follows on the same thread), the final status, and `Loading data for X`.
Associations the connector initiates itself are excluded. `GET /api/worklist?hours=` returns equipment, findings and
the 300 most recent associations; the UI refreshes it every 10 seconds while the tab is open.

## Resource use

Scan jobs run on a dedicated thread (`TaskCreationOptions.LongRunning`) lowered by `BackgroundPriority`: Windows
`THREAD_MODE_BACKGROUND_BEGIN`, macOS `PRIO_DARWIN_BG` for the thread, Linux nice 19 for the thread. The web interface
and the live-indexing check keep normal priority. `ReadPacer` keeps the average read rate of sources on network shares
(`NetworkVolumes`, via `DriveInfo.DriveType`) at `--network-read-limit` MB/s by pausing between batches of 256 lines,
holding no lock other than the scan's own write transaction (searches read in WAL mode and are not blocked). Measured
on 4 cores, 100 MB of synthetic Gateway logs: first scan about 85 s at one core, 1.9 GB written for a 438 MB index (WAL
plus FTS5 segment merges; a larger SQLite page cache did not change this), peak memory 245 MB; idle 0.4 % of one core.

## Scan queue

`ScanCoordinator` (a hosted background service) runs scans, source deletions and purges one at a time from a bounded
queue, reports progress, supports cancellation and optional periodic scans (`DLA_SCAN_INTERVAL_MINUTES`). This keeps a
single writer for SQLite while searches continue on separate read connections. Each source is scanned in isolation:
an error is recorded on that source as `failed` (with a reference and, for storage errors, the numeric SQLite code)
and the remaining sources are still scanned; a transient lock is retried once. Re-indexing after a time-zone or
format change also runs in this queue, so it never overlaps a scan.

## Adding a log format

1. Collect an approved sample and create a **synthetic** fixture that reproduces its structure (never real data).
2. Implement `ILogFormat` in `src/DicomLogAnalyser.Core/Parsing/`: a stable `Id`, a display name and
   `TryParseHeader(line)` returning level, timestamp (with UTC offset if the format has one), category, component and
   message, or `null` for non-header lines. Use a `GeneratedRegex` with a timeout and anchor it at both ends.
3. Register it in `FormatRegistry.All`.
4. Add parser, detection and ingestion tests with the fixture, then document it in the README. Detection is automatic.

## Measured performance (synthetic data, this build environment)

300 MB B3DWEB-style log, 1.78 million events, Release build, 4-core Linux container:

| Operation | Result |
|---|---|
| First full scan | 105 s (about 3 MB/s), index 824 MB (2.7 x the log size) |
| Rescan after an append | 0.5 s |
| Search for an accession number / patient ID / combined terms | < 10 ms |
| Newest 100 events, no filter | 0.13 s |
| Errors only | 25 ms |
| Term matching every event (for example `2.25`) | 4.4 s |
| Overview | 3.7 s first time, then served from cache until the index changes |

With the substring (trigram) index, schema version 3, same data: first scan 241 s, index 1 767 MB (5.9 x the log size);
full accession number 0.18 s; fragments such as `ACC12345`, `StoreScu.Send` or `0.2.17)` 0.03 to 0.12 s; text plus
category filter 0.6 s; a term matching every event (`2.25`) 2.9 s. With the 5-day limit per folder, a typical
47 MB folder scans in roughly 40 s with about 280 MB of index.

Plan disk space for the index at roughly three times the indexed log volume.
