# Security and privacy design

This document describes what is implemented and tested. It is not a certification. No independent penetration test
has been performed. Baseline: **OWASP Application Security Verification Standard (ASVS) 5.0**, used as a review
checklist for the chapters relevant to this application (see [ASVS mapping](#owasp-asvs-50-mapping)).

## Assets and trust boundaries

| Asset | Where | Protection |
|---|---|---|
| Original logs (PHI) | Source folders, never modified | Opened read-only with sharing; Docker mounts `:ro`; links not followed |
| Index database (PHI) | `<data>/index/index.db` | Local disk only (network locations refused), owner-only directory (macOS/Linux), container volume owned by uid 1654 |
| Imported bundles (PHI) | `<data>/bundles/<id>/files` | Validated before keeping, stored read-only, deletable |
| Temporary uploads (PHI) | `<data>/tmp` | Deleted after each import, at start and on request |
| Sessions | Process memory only | 256-bit random ids, HttpOnly, SameSite=Strict, idle (30 min) and absolute (12 h) expiry |
| Activity record | `audit` table | Ids and counts only, no search text, log content, identifiers or file names |

Untrusted inputs: log file content, uploaded bundles (may be crafted), every HTTP request, and in central mode
anything that does not arrive through the access proxy.

## Local mode

* **Binding:** Kestrel listens on `127.0.0.1` only. No external configuration source is loaded
  (`WebApplication.CreateEmptyBuilder`), so `ASPNETCORE_URLS`, `appsettings.json` or `--urls` cannot add a binding.
  After start, every bound address is verified and the program stops if one is not loopback.
* **Sign-in without a password:** a one-time launch token (256-bit, 2-minute lifetime, single use, at most 5
  outstanding) is passed to the default browser, redeemed for a server-side session and removed from the address bar
  by a redirect. Press O in the console for a new link.
* **DNS rebinding:** the Host header must be exactly `127.0.0.1:<port>`; otherwise `421`.
* **Cross-site requests:** requests with `Sec-Fetch-Site` other than `same-origin`/`none` are refused (except a plain
  top-level navigation to `/`); an `Origin` header, when present, must equal the application's origin; `OPTIONS`
  pre-flights are always refused and no CORS headers are ever sent. This also blocks other web servers on
  `127.0.0.1:<other port>`, which browsers treat as same-site.
* **CSRF:** every `POST`/`PUT`/`DELETE` requires the per-session token in `X-CSRF-Token` (held in JavaScript memory,
  never in a cookie) **and** an exact `Origin`.
* **One instance per data folder** (exclusive lock file) so two writers never compete for the index.
* Plain HTTP on loopback: traffic does not leave the computer; a locally trusted TLS root would add more risk than it
  removes (same reasoning as the sibling DICOM CD PACS Transfer project).

## Central mode

* **No application passwords, no anonymous mode.** The analyser trusts an identity header (default
  `X-Forwarded-Email`) **only** when the TCP peer is in `DLA_TRUSTED_PROXIES`. The provided Compose file gives the proxy
  a fixed address (`172.30.0.10/32`) on an `internal: true` network, and the analyser publishes no port.
* **Defence in depth on authorisation:** oauth2-proxy enforces the identity provider sign-in and its allow list; the
  analyser independently checks `DLA_ALLOWED_USERS` / `DLA_ALLOWED_EMAIL_DOMAINS` (exact match, no wildcards).
* **Roles:** *viewer* (overview, search, event detail; source paths hidden) for every allowed user, *administrator*
  (sources, scans, imports, CSV export, maintenance, purge, activity record) only for the exact addresses in
  `DLA_ADMIN_USERS`, which must also be allowed. Local mode's single user is the administrator. Administrator-only
  endpoints are marked in routing metadata and enforced by the security middleware **before** any request body is read;
  refusals return 403 and are written to the activity record.
* **Fail closed:** the program refuses to start in central mode without an `https://` public origin, a non-`/0`
  trusted proxy range, an allow list and source roots.
* **Header spoofing:** oauth2-proxy strips client-supplied `X-Forwarded-*` authentication headers
  (`skip_auth_strip_headers`, default true since v7.0 and set explicitly); `--trusted-ip`, `--skip-auth-route` and
  `--reverse-proxy` are not used (relevant to GHSA-wr5q-7wxw-x568 and GHSA-63jm-59jj-478j, fixed in v7.15.5). A
  request with several identity headers is refused.
* **Sessions:** after the proxy authenticates the user, the analyser issues its own `__Host-dla_session` cookie
  (Secure, HttpOnly, SameSite=Strict, Path=/) bound to the identity; a different identity invalidates it. CSRF token
  and exact `Origin` (= `DLA_PUBLIC_ORIGIN`) are required for changes, as in local mode. HSTS is sent.
* **TLS** terminates at oauth2-proxy with the organisation's certificate (`TLS1.2` minimum). The hop to the analyser
  is plain HTTP on the internal Docker network of the same host.
* **Container hardening:** chiseled .NET runtime image (no shell, no package manager), non-root uid 1654, read-only
  root filesystem, `cap_drop: ALL`, `no-new-privileges`, PID and memory limits, health check, base images pinned by
  digest. The analyser container has no route to the internet (`internal: true`).
* **Logging:** oauth2-proxy request logging is disabled (URLs carry search text); authentication logging stays on.

## Browser-side protections

`Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src
'self'; form-action 'none'; frame-ancestors 'none'; base-uri 'none'`, plus `X-Frame-Options: DENY`,
`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, COOP/CORP, `Permissions-Policy` and
`Cache-Control: no-store` on every response. The UI uses no third-party scripts, fonts or CDNs. All log content is
inserted with `textContent`; search highlighting builds `<mark>` elements through the DOM, never HTML strings. The
highlight colour and theme are stored in `localStorage` (preferences only, no data).

## Input handling

* **Search:** user text is never passed to FTS5 as query syntax. It is reduced to quoted phrases of letter/digit
  tokens (`FtsQuery`), so operators, column filters and quotes cannot be injected; SQL always uses parameters.
* **Parsers:** regular expressions are compiled with timeouts; lines are capped at 64 KB, records at 2000
  continuation lines / 256 KB; invalid UTF-8 falls back to Latin-1 per line without losing byte offsets.
* **Paths:** must be absolute; normalised; refused when overlapping the data folder; in central mode confined to
  `DLA_SOURCE_ROOTS` with no symbolic link below the root. Enumeration never follows links or junctions.
* **Uploads:** streamed to disk with a size limit (default 1 GB, `DLA_MAX_UPLOAD_MB`); type detected from content
  (zip, gzip, text; UTF-16 and binary refused). Zip entries: rejected for absolute paths, drive letters, `..`,
  control or reserved characters, symbolic links or special files, encryption, duplicates (case-insensitive), more
  than 20 000 entries, per-file and total expanded size limits and a compression ratio above 200:1 measured on bytes
  actually written. Non-log files and nested archives are skipped and listed. Any failure removes everything.
* **Folder choice:** `POST /api/folders/pick` opens the native dialog only in local mode (the process runs on the
  user's own desktop); `GET /api/folders` lists sub-folders, in central mode only inside `DLA_SOURCE_ROOTS` and without
  following links. Both are administrator-only; chosen paths are validated exactly like typed ones.
* **Search syntax:** search text is never passed to FTS5 as syntax; each term becomes a quoted literal (quotes doubled),
  short terms use parameterised `LIKE` with escaped wildcards.
* **Live view:** `/api/tail` opens only files chosen for analysis in that source (or a bundle's files), after resolving
  the path inside the source folder and refusing links; any other name returns 403. Files are opened read-only with
  sharing. Viewers may use it, as it shows the same files their searches cover.
* **Fresh session:** administrator-only, explicitly confirmed; runs in the background queue (never during a scan),
  deletes events, file state, optional bundles and sources, temporary files and in-memory caches, then compacts the
  database (VACUUM, WAL truncated). A test confirms that an indexed identifier no longer occurs in the raw bytes of the
  database or its WAL afterwards. Deleted file blocks on disk are protected by the operating system's disk encryption.
* **CSV export:** quoted cells, newlines flattened, formula prefixes (`= + - @ tab`) neutralised.
* **JSON:** unknown properties rejected, depth limited, enums as strings; request bodies limited to 1 MB except bundle
  upload.

## Privacy in diagnostics

Routine logs contain source ids, counts and exception **type names** with a correlation reference only, never
messages, paths, search terms, identities of refused users, or log content. ASP.NET Core framework logging is limited
to warnings. Error responses are generic with the same reference. The activity record holds actor, action and ids.

## OWASP ASVS 5.0 mapping

| ASVS chapter | Relevance | Addressed by |
|---|---|---|
| V1 Encoding and Sanitization | High | Parameterised SQL; FTS query rebuilding; DOM `textContent` only; CSV formula neutralisation |
| V2 Validation and Business Logic | High | Server-side validation of all inputs (labels, patterns, time zones, formats, levels, ids, dates); scan queue serialises index changes |
| V3 Web Frontend Security | High | Strict CSP without inline script/style, frame denial, nosniff, no-referrer, COOP/CORP, Fetch Metadata, no CORS, cookie attributes (`__Host-`, Secure, HttpOnly, SameSite=Strict) |
| V4 API and Web Service | Medium | Same-origin JSON API; method-specific endpoints; Content-Type controlled; body size limits; generic errors |
| V5 File Handling | High | Upload limits, content-type detection, archive traversal/link/bomb defences, storage outside any served path, read-only storage, no execution of uploaded content |
| V6 Authentication | High | No application passwords; local: single-use launch tokens bound to the OS session; central: delegated to the organisation's IdP via oauth2-proxy (MFA/passwordless at the IdP) |
| V7 Session Management | High | Random 256-bit server-side sessions, idle and absolute timeouts, identity binding, termination at shutdown |
| V8 Authorization | High | Allow list checked in the application as well as the proxy; trusted-proxy check before any identity is used; viewer/administrator roles enforced server-side before request processing, deny by default for marked endpoints, refusals audited |
| V9 Self-contained Tokens | Low | The analyser does not consume JWTs; oauth2-proxy validates the IdP tokens |
| V10 OAuth and OIDC | Medium | oauth2-proxy with PKCE (S256), per-request CSRF cookies, no token pass-through to the analyser |
| V11 Cryptography | Low | CSPRNG for tokens; SHA-256 for file fingerprints and bundle checksums; constant-time token comparison |
| V12 Secure Communication | Medium | TLS 1.2+ at the proxy with the organisation's certificate, HSTS; loopback HTTP only in local mode |
| V13 Configuration | High | No external config sources in local mode; fail-closed central configuration; pinned dependencies with lock files; hardened container; no secrets in the analyser |
| V14 Data Protection | High | Local processing only, no telemetry/AI/CDN; `no-store` caching; read-only sources; deletion controls; temp cleanup; PHI-free diagnostics; retention guidance |
| V15 Secure Coding and Architecture | Medium | Minimal dependency set (one runtime package); analyzers with warnings as errors; dependency audit in CI |
| V16 Security Logging and Error Handling | Medium | Activity record of security-relevant actions; refused requests logged without identities or content; generic error responses with references |
| V17 WebRTC | Not applicable | |

## Residual risks and limitations

* **Two roles only.** Viewers can still read every indexed event (PHI) through search; there is no per-source or
  per-site access restriction. Administrators are listed by e-mail address; identity-provider groups are not used yet.
* **Same-account access (local mode).** Malware or another process running as the same user can read the data folder
  or obtain a session. The application cannot defend against a compromised account.
* **No at-rest encryption by the application.** Rely on BitLocker/FileVault/LUKS and encrypted backups.
* **Launch link in process arguments.** The one-time link is passed to the browser on its command line, briefly
  visible to other local users on the same machine; it expires after 2 minutes and works once.
* **Search text in URLs.** GET requests carry search text to the analyser; browsers may keep CSV download URLs in
  download history. Nothing is sent to third parties.
* **oauth2-proxy image digest** could not be pulled from this build environment (registry blocked); pin it on first
  deployment. The proxy configuration was validated with `docker compose config` but not exercised against a real IdP.
* **Unsigned binaries** (Windows Authenticode, macOS notarisation) until an organisational signing certificate is used.
* **SQLite single-writer** design: suitable for one instance; not a clustered service.
* **Heuristic masking** of identifiers in the overview's error patterns is not de-identification.
