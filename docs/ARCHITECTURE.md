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

`events_fts` is an external-content FTS5 table (`unicode61`, diacritics removed) over message, detail, category and
component, maintained by triggers. Punctuation separates tokens, so a UID or IP becomes a token sequence that the query
builder searches as an exact phrase.

## Search semantics

* All terms must match; `"..."` is an exact phrase, a trailing `*` a prefix, a leading `-` excludes.
* Text searches start from the FTS index (identifier look-ups take milliseconds on millions of events); unfiltered
  pages use the time index. Results use keyset pagination on `(sort_ts, id)`. Counts are capped at 200 000.
* Each response includes per-level counts and **coverage notes** computed from source state, so the UI can state
  whether "no result" might be caused by a gap rather than by absence of the event.

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

Plan disk space for the index at roughly three times the indexed log volume.
