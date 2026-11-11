using System.Globalization;
using System.Reflection;
using System.Text;
using DicomLogAnalyser.Core.Bundles;
using DicomLogAnalyser.Core.Diagnostics;
using DicomLogAnalyser.Core.IO;
using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.Model;
using DicomLogAnalyser.Core.Parsing;
using DicomLogAnalyser.Core.Search;
using DicomLogAnalyser.Core.Sources;
using DicomLogAnalyser.Core.Storage;
using DicomLogAnalyser.Web.Security;
using Microsoft.AspNetCore.Http.Features;

namespace DicomLogAnalyser.Web.Api;

public sealed record SourceInput(string? Label, string? Path, bool? Enabled, bool? Recursive, string? IncludePatterns, string? TimeZone, string? FormatHint, bool? ScanNow);

public sealed record PathInput(string? Path);

public sealed record ScanInput(IReadOnlyList<long>? SourceIds);

public sealed record ConfirmInput(string? Confirm);

public sealed record SelectionInput(IReadOnlyList<string>? Files);

public sealed record FreshSessionInput(string? Confirm, bool? KeepFolderSources, bool? DeleteBundles, bool? ClearActivityRecord, bool? ScanAgain);

public static class Endpoints
{
    public const int ExportMaxRows = 100_000;

    public static string Version { get; } =
        typeof(Endpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/healthz", () => Results.Text("ok", "text/plain"));

        var api = app.MapGroup("/api");
        // Everything that changes data, reveals storage locations or extracts PHI in bulk needs the administrator role.
        // Enforced by SecurityMiddleware before the request body is read.
        var admin = app.MapGroup("/api").WithMetadata(new AdminOnlyAttribute());
        api.MapGet("/session", (HttpContext ctx, AppRuntime rt, PathValidator validator) =>
        {
            var s = SecurityMiddleware.CurrentSession(ctx);
            return Results.Json(new
            {
                mode = rt.Options.Mode == AppMode.Server ? "server" : "local",
                identity = s.Identity,
                role = s.IsAdmin ? "admin" : "viewer",
                csrfToken = s.CsrfToken,
                version = Version,
                platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
                sourceRoots = validator.AllowedRoots,
                maxUploadMb = rt.Options.MaxUploadBytes / (1024 * 1024),
                scanIntervalMinutes = rt.Options.ScanIntervalMinutes,
                liveIntervalSeconds = rt.Options.LiveIntervalSeconds,
                folderDialog = ctx.RequestServices.GetRequiredService<Platform.IFolderPicker>().IsSupported,
                formats = FormatRegistry.All.Select(f => new { id = f.Id, name = f.DisplayName }),
            });
        });

        api.MapGet("/overview", (OverviewService overview) => Results.Json(overview.Build()));

        // Activity over time (Overview chart): events, errors, warnings and DICOM activities per time slot, ending at the
        // newest indexed event. hours = 24, 168, 720 or "all"; source = one source id or empty for all.
        api.MapGet("/activity", (HttpContext ctx, ActivityService activity) =>
        {
            var q = ctx.Request.Query;
            int? hours = 24;
            var hoursText = q["hours"].ToString();
            if (hoursText == "all") hours = null;
            else if (hoursText.Length > 0)
            {
                if (!int.TryParse(hoursText, NumberStyles.None, CultureInfo.InvariantCulture, out var h) || h is < 1 or > 24 * 366) return Bad("Invalid period.");
                hours = h;
            }

            long? source = null;
            var sourceText = q["source"].ToString();
            if (sourceText.Length > 0)
            {
                if (!long.TryParse(sourceText, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return Bad("Invalid source.");
                source = id;
            }

            var aets = new List<string>();
            foreach (var part in q["aet"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!EquipmentService.IsValidAet(part) || aets.Count >= ActivityService.MaxAets) return Bad("Invalid AE title list.");
                aets.Add(part);
            }

            return Results.Json(activity.Build(hours, source, aets));
        });

        // Exam history: first receipt, updates (new series), resends per destination, final series, with evidence.
        api.MapGet("/exam", (HttpContext ctx, Core.Exam.ExamHistoryService exams) =>
        {
            var q = ctx.Request.Query["q"].ToString().Trim();
            if (q.Length < 3 || q.Length > 200) return Bad("Enter an accession number, patient ID or Study Instance UID (3 to 200 characters).");
            var ids = new List<long>();
            foreach (var part in ctx.Request.Query["sources"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || ids.Count >= 100) return Bad("Invalid source list.");
                ids.Add(id);
            }

            return Results.Json(exams.Build(q, ids.Count > 0 ? ids : null));
        });

        // Modality worklist (logs of a worklist provider): equipment, queries, items sent and AE title inconsistencies.
        api.MapGet("/worklist", (HttpContext ctx, Core.Worklist.WorklistAnalysis worklist) =>
        {
            var hoursText = ctx.Request.Query["hours"].ToString();
            int? hours = 24;
            if (hoursText == "all") hours = null;
            else if (hoursText.Length > 0)
            {
                if (!int.TryParse(hoursText, NumberStyles.None, CultureInfo.InvariantCulture, out var h) || h is < 1 or > 24 * 31) return Bad("Invalid period.");
                hours = h;
            }

            var ids = new List<long>();
            foreach (var part in ctx.Request.Query["sources"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || ids.Count >= 100) return Bad("Invalid source list.");
                ids.Add(id);
            }

            return Results.Json(worklist.Analyse(ids.Count > 0 ? ids : null, hours));
        });

        // Equipment named in the logs with its modality ("Mod US, PHILLIS4"), for the equipment filters.
        api.MapGet("/equipment", (EquipmentService equipment) => Results.Json(equipment.List()));

        api.MapGet("/categories", (OverviewService overview) => Results.Json(overview.Categories()));

        // Time-zone names this server accepts, its own time zone, and whether the browser's zone is usable here.
        api.MapGet("/timezones", (HttpContext ctx) =>
        {
            var prefer = ctx.Request.Query["prefer"].ToString().Trim();
            var server = Core.Sources.TimeZones.Server;
            return Results.Json(new
            {
                server = new { id = server.Id, name = server.DisplayName },
                preferred = prefer.Length is > 0 and <= 64 && Core.Sources.TimeZones.Resolve(prefer) is not null ? prefer : null,
                names = Core.Sources.TimeZones.KnownNames(),
            });
        });

        api.MapGet("/search", (HttpContext ctx, SearchService search) =>
        {
            var (request, error) = ParseSearch(ctx.Request.Query);
            return error is not null ? Bad(error) : Results.Json(search.Search(request!));
        });

        // Find in files: reads the chosen files directly, line by line, without the index (like "Find in Files" in a text
        // editor), so results do not wait for indexing. Case- and accent-insensitive. The search text is not logged.
        api.MapGet("/find", (HttpContext ctx, SourceRepository repo) =>
        {
            var q = ctx.Request.Query;
            var text = q["q"].ToString();
            if (text.Length > 500) return Bad("The search text is too long.");
            if (FileFinder.Parse(text).Include.Count == 0) return Bad("Enter the text to find.");
            var ids = new HashSet<long>();
            foreach (var part in q["sources"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || ids.Count >= 100) return Bad("Invalid source list.");
                ids.Add(id);
            }

            if (!FindGate.Wait(0))
                return Results.Json(new { error = "busy", message = "Another file search is running. Try again in a moment." }, statusCode: StatusCodes.Status429TooManyRequests);
            try
            {
                var (targets, unreachable) = FindTargets(repo.List().Where(s => s.Enabled && (ids.Count == 0 || ids.Contains(s.Id))));
                var result = FileFinder.Find(targets, text, FileFinder.DefaultMaxHits, FindTimeLimit, ctx.RequestAborted);
                return Results.Json(new
                {
                    hits = result.Hits.Select(h => new { sourceId = h.SourceId, source = h.SourceLabel, file = h.File, line = h.Line, text = h.Text, before = h.Before, after = h.After }),
                    filesSearched = result.FilesSearched,
                    bytesRead = result.BytesRead,
                    truncated = result.Truncated,
                    timedOut = result.TimedOut,
                    seconds = Math.Round(result.Seconds, 2),
                    unreadable = result.Unreadable.Concat(unreachable),
                    terms = result.Terms,
                    maxHits = FileFinder.DefaultMaxHits,
                });
            }
            catch (OperationCanceledException)
            {
                return Results.Empty;
            }
            finally
            {
                FindGate.Release();
            }
        });

        api.MapGet("/events/{id:long}", (long id, SearchService search) => search.Get(id) is { } e ? Results.Json(e) : Results.NotFound(new { error = "not_found" }));

        admin.MapGet("/export.csv", async (HttpContext ctx, SearchService search, AuditLog audit) =>
        {
            var (request, error) = ParseSearch(ctx.Request.Query);
            if (error is not null)
            {
                await Bad(error).ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            ctx.Response.ContentType = "text/csv; charset=utf-8";
            var name = "dicom-log-events-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv";
            ctx.Response.Headers.ContentDisposition = "attachment; filename=\"" + name + "\"";
            var writer = new StreamWriter(ctx.Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 65536);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteLineAsync("time,time_basis,level,source,file,line,category,component,message").ConfigureAwait(false);
                var rows = 0;
                foreach (var e in search.Export(request!, ExportMaxRows))
                {
                    var time = e.Timestamp is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) : "";
                    await writer.WriteLineAsync(string.Join(',', Csv(time), Csv(e.Timestamp is null ? "unknown" : e.UtcKnown ? "UTC" : "source local time (zone unknown)"),
                        Csv(e.LevelText.Length > 0 ? e.LevelText : e.Level.ToString()), Csv(e.SourceLabel), Csv(e.File), e.LineStart.ToString(CultureInfo.InvariantCulture),
                        Csv(e.Category), Csv(e.Component ?? ""), Csv(e.Message))).ConfigureAwait(false);
                    rows++;
                }

                audit.Write(SecurityMiddleware.CurrentSession(ctx).Identity, "export", new Dictionary<string, object?> { ["rows"] = rows });
            }
        });

        // Viewers need the source list for filters, but not the folder paths behind it.
        api.MapGet("/sources", (HttpContext ctx, SourceRepository repo) => SecurityMiddleware.CurrentSession(ctx).IsAdmin
            ? Results.Json(repo.List())
            : Results.Json(repo.List().Select(x => x with { Path = "", LastScanMessage = null, SelectedFiles = null })));

        // File chooser for folder sources: at most FileSelection.MaxDays calendar days of files per folder.
        admin.MapGet("/sources/{id:long}/files", (long id, SourceRepository repo) =>
        {
            if (repo.Get(id) is not { Kind: SourceKinds.Folder } source) return Results.NotFound(new { error = "not_found" });
            var tz = Ingestor.ResolveTimeZone(source.TimeZone);
            IReadOnlyList<CandidateFile> files;
            try
            {
                files = FileSelection.List(source, tz);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.Json(new { error = "unreachable", message = "The folder could not be listed. Check that it is reachable, then try again." }, statusCode: StatusCodes.Status409Conflict);
            }

            var saved = source.SelectedFiles is not null;
            var chosen = new HashSet<string>(source.SelectedFiles ?? FileSelection.Default(files), StringComparer.Ordinal);
            var seen = new HashSet<string>(source.SeenFiles ?? [], StringComparer.Ordinal);
            return Results.Json(new
            {
                maxDays = FileSelection.MaxDays,
                timeZone = (tz ?? TimeZoneInfo.Local).Id,
                selectionSaved = saved,
                truncated = files.Count > FileSelection.MaxListed,
                files = files.Take(FileSelection.MaxListed).Select(f => new
                {
                    path = f.RelPath,
                    size = f.Size,
                    modifiedUtc = f.ModifiedUtc,
                    modifiedDate = f.ModifiedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    selected = chosen.Contains(f.RelPath),
                    isNew = saved && source.SeenFiles is not null && !seen.Contains(f.RelPath) && !chosen.Contains(f.RelPath),
                }),
            });
        });

        admin.MapPut("/sources/{id:long}/files", (HttpContext ctx, long id, SelectionInput input, SourceRepository repo, AuditLog audit, ScanCoordinator coordinator) =>
        {
            if (repo.Get(id) is not { Kind: SourceKinds.Folder } source) return Results.NotFound(new { error = "not_found" });
            IReadOnlyList<CandidateFile> files;
            try
            {
                files = FileSelection.List(source, Ingestor.ResolveTimeZone(source.TimeZone));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.Json(new { error = "unreachable", message = "The folder could not be listed, so the choice cannot be checked." }, statusCode: StatusCodes.Status409Conflict);
            }

            var problem = FileSelection.Validate(input.Files, files);
            if (problem is not null) return Bad(problem);
            repo.SetSelection(id, input.Files!, files.Select(f => f.RelPath).ToList());
            var actor = SecurityMiddleware.CurrentSession(ctx).Identity;
            audit.Write(actor, "selection_changed", new Dictionary<string, object?> { ["sourceId"] = id, ["files"] = input.Files!.Count });
            if (source.Enabled) coordinator.EnqueueScan(actor, [id]);
            return Results.Json(new { id, files = input.Files!.Count });
        });

        // Folder choice without typing: the native dialog of this computer (local mode) or the in-app browser.
        admin.MapPost("/folders/pick", async (Platform.IFolderPicker picker, CancellationToken ct) =>
        {
            if (!picker.IsSupported) return Results.Json(new { error = "unsupported", message = "No folder dialog is available here; use the folder browser." }, statusCode: StatusCodes.Status409Conflict);
            try
            {
                var dir = await picker.PickAsync(ct).ConfigureAwait(false);
                return Results.Json(new { path = dir?.FullName, cancelled = dir is null });
            }
            catch (InvalidOperationException)
            {
                return Results.Json(new { error = "busy", message = "A folder dialog is already open on this computer." }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        admin.MapGet("/folders", (HttpContext ctx, Platform.FolderBrowser browser) =>
        {
            try
            {
                return Results.Json(browser.List(ctx.Request.Query["path"].ToString()));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Bad("This is not a valid folder path.");
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { error = "not_allowed", message = "This folder cannot be opened (no permission, or outside the mounted source folders)." }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (IOException)
            {
                return Results.Json(new { error = "unreachable", message = "This folder does not exist or is not reachable." }, statusCode: StatusCodes.Status404NotFound);
            }
        });

        admin.MapPost("/sources/validate", (PathInput input, PathValidator validator) => Results.Json(validator.ValidateFolder(input.Path)));

        admin.MapPost("/sources", (HttpContext ctx, SourceInput input, SourceRepository repo, PathValidator validator, AuditLog audit, ScanCoordinator coordinator) =>
        {
            var errors = CheckSettings(input);
            if (errors.Count > 0) return Results.BadRequest(new { error = "invalid", messages = errors });
            var validation = validator.ValidateFolder(input.Path);
            if (!validation.Ok) return Results.BadRequest(new { error = "invalid", messages = validation.Errors, validation });
            var id = repo.Add(new SourceDefinition
            {
                Label = input.Label!.Trim(),
                Kind = SourceKinds.Folder,
                Path = validation.NormalisedPath!,
                Enabled = input.Enabled ?? true,
                Recursive = input.Recursive ?? true,
                IncludePatterns = string.IsNullOrWhiteSpace(input.IncludePatterns) ? "*" : input.IncludePatterns.Trim(),
                TimeZone = string.IsNullOrWhiteSpace(input.TimeZone) ? null : input.TimeZone.Trim(),
                FormatHint = string.IsNullOrWhiteSpace(input.FormatHint) ? FormatRegistry.Auto : input.FormatHint,
            });
            var actor = SecurityMiddleware.CurrentSession(ctx).Identity;
            audit.Write(actor, "source_added", new Dictionary<string, object?> { ["sourceId"] = id });
            if (input.ScanNow == true) coordinator.EnqueueScan(actor, [id]);
            return Results.Json(new { id, validation });
        });

        admin.MapPut("/sources/{id:long}", (HttpContext ctx, long id, SourceInput input, SourceRepository repo, AuditLog audit, ScanCoordinator coordinator) =>
        {
            var existing = repo.Get(id);
            if (existing is null) return Results.NotFound(new { error = "not_found" });
            var errors = CheckSettings(input with { Path = existing.Path });
            if (errors.Count > 0) return Results.BadRequest(new { error = "invalid", messages = errors });
            var updated = existing with
            {
                Label = input.Label!.Trim(),
                Enabled = input.Enabled ?? existing.Enabled,
                Recursive = input.Recursive ?? existing.Recursive,
                IncludePatterns = string.IsNullOrWhiteSpace(input.IncludePatterns) ? existing.IncludePatterns : input.IncludePatterns.Trim(),
                TimeZone = string.IsNullOrWhiteSpace(input.TimeZone) ? null : input.TimeZone.Trim(),
                FormatHint = string.IsNullOrWhiteSpace(input.FormatHint) ? existing.FormatHint : input.FormatHint,
            };
            var needsReindex = updated.TimeZone != existing.TimeZone || updated.FormatHint != existing.FormatHint;
            repo.Update(updated);
            var actor = SecurityMiddleware.CurrentSession(ctx).Identity;
            audit.Write(actor, "source_updated", new Dictionary<string, object?> { ["sourceId"] = id, ["reindex"] = needsReindex });
            // The index is cleared by the background queue, never while a scan of the same source is writing to it.
            if (needsReindex && !coordinator.EnqueueReindex(actor, id))
                return Results.Json(new { error = "busy", message = "The work queue is full. Save again in a moment." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            return Results.Json(new { id, reindexed = needsReindex });
        });

        admin.MapDelete("/sources/{id:long}", (HttpContext ctx, long id, SourceRepository repo, ScanCoordinator coordinator) =>
        {
            if (repo.Get(id) is null) return Results.NotFound(new { error = "not_found" });
            coordinator.CancelCurrent();
            return coordinator.EnqueueDelete(SecurityMiddleware.CurrentSession(ctx).Identity, id)
                ? Results.Accepted(value: new { queued = true })
                : Results.Json(new { error = "busy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        admin.MapPost("/scan", (HttpContext ctx, ScanInput input, ScanCoordinator coordinator) =>
            coordinator.EnqueueScan(SecurityMiddleware.CurrentSession(ctx).Identity, input.SourceIds is { Count: > 0 } ids ? ids : null)
                ? Results.Accepted(value: new { queued = true })
                : Results.Json(new { error = "busy" }, statusCode: StatusCodes.Status503ServiceUnavailable));

        admin.MapPost("/scan/cancel", (ScanCoordinator coordinator) =>
        {
            coordinator.CancelCurrent();
            return Results.Json(new { cancelled = true });
        });

        // Live view (BareTail-style): reads a chosen file directly, without the index. Viewers may use it: it shows the
        // same files their searches already cover. Only files chosen for analysis (or bundle files) can be opened.
        api.MapGet("/tail/files", (HttpContext ctx, SourceRepository repo) =>
        {
            if (!long.TryParse(ctx.Request.Query["source"], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || repo.Get(id) is not { } source)
                return Results.NotFound(new { error = "not_found" });
            try
            {
                return Results.Json(TailFiles(source).Select(f => new { path = f.RelPath, size = f.Size, modifiedUtc = f.ModifiedUtc }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.Json(new { error = "unreachable", message = "The folder is not reachable at the moment." }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        api.MapGet("/tail", (HttpContext ctx, SourceRepository repo) =>
        {
            var q = ctx.Request.Query;
            if (!long.TryParse(q["source"], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || repo.Get(id) is not { } source)
                return Results.NotFound(new { error = "not_found" });
            var rel = q["file"].ToString();
            var lines = int.TryParse(q["lines"], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 500;
            try
            {
                var allowed = TailFiles(source).Select(f => f.RelPath).ToHashSet(StringComparer.Ordinal);
                var full = Path.GetFullPath(Path.Combine(source.Path, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!allowed.Contains(rel) || !PathValidator.IsSameOrInside(full, source.Path) || new FileInfo(full).LinkTarget is not null)
                    return Results.Json(new { error = "not_allowed", message = "Only files chosen for analysis can be viewed live." }, statusCode: StatusCodes.Status403Forbidden);
                var chunk = q["from"].ToString() is { Length: > 0 } f && long.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                    ? TailReader.ReadFrom(full, from, lines)
                    : TailReader.ReadLast(full, lines);
                return Results.Json(new
                {
                    lines = chunk.Lines.Select(l => new { t = l.Text, l = l.Level?.ToString().ToLowerInvariant() }),
                    offset = chunk.Offset,
                    size = chunk.Size,
                    reset = chunk.Reset,
                    headTruncated = chunk.HeadTruncated,
                    modifiedUtc = chunk.ModifiedUtc,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.Json(new { error = "unreachable", message = "The file is not reachable at the moment (share offline, file removed or locked)." }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        api.MapGet("/scan/status", (ScanCoordinator coordinator) => Results.Json(coordinator.Status));

        admin.MapPost("/bundles", async (HttpContext ctx, BundleImporter importer, SourceRepository repo, AuditLog audit, ScanCoordinator coordinator, ImportTracker tracker) =>
        {
            var q = ctx.Request.Query;
            var label = q["label"].ToString().Trim();
            var tz = q["timeZone"].ToString().Trim();
            var name = q["name"].ToString();
            var labelError = PathValidator.CheckLabel(label) ?? PathValidator.CheckTimeZone(tz.Length == 0 ? null : tz);
            if (labelError is not null) return Bad(labelError);
            var limit = importer.Limits.MaxUploadBytes;
            if (ctx.Request.ContentLength is { } len && len > limit)
                return Results.Json(new { error = "too_large", message = $"The upload exceeds the limit of {limit / (1024 * 1024)} MB." }, statusCode: StatusCodes.Status413PayloadTooLarge);
            var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = limit;

            BundleResult result;
            using (tracker.Begin())
            {
                result = await importer.ImportAsync(ctx.Request.Body, name, ctx.RequestAborted).ConfigureAwait(false);
            }

            var id = repo.Add(new SourceDefinition
            {
                Label = label,
                Kind = SourceKinds.Bundle,
                Path = result.FilesDirectory,
                Recursive = true,
                IncludePatterns = "*",
                TimeZone = tz.Length == 0 ? null : tz,
                BundleSha256 = result.Sha256,
                BundleBytes = result.UploadBytes,
            });
            var actor = SecurityMiddleware.CurrentSession(ctx).Identity;
            audit.Write(actor, "bundle_imported", new Dictionary<string, object?>
            {
                ["sourceId"] = id,
                ["bytes"] = result.UploadBytes,
                ["files"] = result.FilesExtracted,
                ["sha256"] = result.Sha256,
                ["skipped"] = result.Skipped.Count,
            });
            coordinator.EnqueueScan(actor, [id]);
            return Results.Json(new { id, result.Sha256, result.UploadBytes, result.FilesExtracted, result.BytesExtracted, result.Skipped });
        });

        admin.MapGet("/maintenance", (AppPaths paths, AuditLog audit, AppRuntime rt) => Results.Json(new
        {
            dataDirectory = rt.Options.Mode == AppMode.Local ? paths.Root : "(container volume)",
            databaseBytes = SizeOf(paths.DatabaseFile) + SizeOf(paths.DatabaseFile + "-wal") + SizeOf(paths.DatabaseFile + "-shm"),
            bundlesBytes = DirectorySize(paths.BundlesDirectory),
            tempEntries = Directory.Exists(paths.TempDirectory) ? Directory.EnumerateFileSystemEntries(paths.TempDirectory).Count() : 0,
            tempBytes = DirectorySize(paths.TempDirectory),
            audit = audit.Recent(200),
        }));

        // Masked message patterns (no identifiers) to share for tuning the exam-history rules.
        admin.MapGet("/maintenance/patterns.csv", async (HttpContext ctx, OverviewService overview, AuditLog audit) =>
        {
            var rows = overview.MessagePatterns();
            ctx.Response.ContentType = "text/csv; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = "attachment; filename=\"dicom-log-message-patterns.csv\"";
            var writer = new StreamWriter(ctx.Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 65536);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteLineAsync("format,category,level,count,pattern").ConfigureAwait(false);
                foreach (var (format, category, level, pattern, count) in rows)
                    await writer.WriteLineAsync(string.Join(',', Csv(format), Csv(category), Csv(level), count.ToString(CultureInfo.InvariantCulture), Csv(pattern))).ConfigureAwait(false);
            }

            audit.Write(SecurityMiddleware.CurrentSession(ctx).Identity, "patterns_exported", new Dictionary<string, object?> { ["rows"] = rows.Count });
        });

        // Local mode: stop the analyser from the browser (same as Q or closing its window).
        admin.MapPost("/maintenance/stop", (HttpContext ctx, AppRuntime rt, IHostApplicationLifetime lifetime, AuditLog audit) =>
        {
            if (rt.Options.Mode != AppMode.Local) return Results.NotFound();
            audit.Write(SecurityMiddleware.CurrentSession(ctx).Identity, "stopped");
            _ = Task.Run(async () =>
            {
                await Task.Delay(300).ConfigureAwait(false);
                lifetime.StopApplication();
            });
            return Results.Json(new { stopping = true });
        });

        admin.MapPost("/maintenance/clean-temp", (HttpContext ctx, AppPaths paths, AuditLog audit, ImportTracker tracker) =>
        {
            if (tracker.Active > 0) return Results.Conflict(new { error = "busy", message = "An import is in progress. Try again when it has finished." });
            var remaining = paths.CleanTemp();
            audit.Write(SecurityMiddleware.CurrentSession(ctx).Identity, "temp_cleaned", new Dictionary<string, object?> { ["remaining"] = remaining });
            return Results.Json(new { remaining });
        });

        admin.MapPost("/maintenance/fresh-session", (HttpContext ctx, FreshSessionInput input, ScanCoordinator coordinator, ImportTracker tracker) =>
        {
            if (input.Confirm != "FRESH") return Bad("Confirm the fresh session.");
            if (tracker.Active > 0) return Results.Conflict(new { error = "busy", message = "An import is in progress. Start the fresh session when it has finished." });
            coordinator.CancelCurrent();
            var options = new FreshSessionOptions(input.KeepFolderSources ?? true, input.DeleteBundles ?? true, input.ClearActivityRecord ?? false, input.ScanAgain ?? true);
            return coordinator.EnqueueFreshSession(SecurityMiddleware.CurrentSession(ctx).Identity, options)
                ? Results.Accepted(value: new { queued = true })
                : Results.Json(new { error = "busy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        admin.MapPost("/maintenance/purge-index", (HttpContext ctx, ConfirmInput input, ScanCoordinator coordinator) =>
        {
            if (input.Confirm != "PURGE") return Bad("Type PURGE to confirm.");
            coordinator.CancelCurrent();
            return coordinator.EnqueuePurge(SecurityMiddleware.CurrentSession(ctx).Identity)
                ? Results.Accepted(value: new { queued = true })
                : Results.Json(new { error = "busy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
    }

    private static readonly SemaphoreSlim FindGate = new(2, 2);
    private static readonly TimeSpan FindTimeLimit = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The files "Find in files" may read: the same files as the live view (chosen files of folders, bundle files), each
    /// checked to lie inside its source folder and not to be a link.
    /// </summary>
    internal static (List<FindTarget> Targets, List<string> Unreachable) FindTargets(IEnumerable<SourceDefinition> sources)
    {
        var targets = new List<FindTarget>();
        var unreachable = new List<string>();
        foreach (var source in sources)
        {
            List<CandidateFile> files;
            try
            {
                files = TailFiles(source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreachable.Add(source.Label + ": (folder not reachable)");
                continue;
            }

            foreach (var f in files.OrderBy(f => f.RelPath, StringComparer.Ordinal))
            {
                var full = Path.GetFullPath(Path.Combine(source.Path, f.RelPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!PathValidator.IsSameOrInside(full, source.Path)) continue;
                try
                {
                    if (new FileInfo(full).LinkTarget is not null) continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreachable.Add(source.Label + ": " + f.RelPath);
                    continue;
                }

                targets.Add(new FindTarget(source.Id, source.Label, f.RelPath, full));
            }
        }

        return (targets, unreachable);
    }

    /// <summary>Files that may be viewed live: the chosen files of a folder (default choice if none saved), or a bundle's files.</summary>
    internal static List<CandidateFile> TailFiles(SourceDefinition source)
    {
        var tz = Ingestor.ResolveTimeZone(source.TimeZone);
        var all = FileSelection.List(source, tz);
        if (source.Kind == SourceKinds.Bundle) return all.ToList();
        var chosen = new HashSet<string>(source.SelectedFiles ?? FileSelection.Default(all), StringComparer.Ordinal);
        return all.Where(f => chosen.Contains(f.RelPath)).ToList();
    }

    internal static List<string> CheckSettings(SourceInput input)
    {
        var errors = new List<string>();
        void Add(string? e)
        {
            if (e is not null) errors.Add(e);
        }

        Add(PathValidator.CheckLabel(input.Label));
        Add(string.IsNullOrWhiteSpace(input.IncludePatterns) ? null : PathValidator.CheckPatterns(input.IncludePatterns));
        Add(PathValidator.CheckTimeZone(string.IsNullOrWhiteSpace(input.TimeZone) ? null : input.TimeZone.Trim()));
        Add(PathValidator.CheckFormat(string.IsNullOrWhiteSpace(input.FormatHint) ? null : input.FormatHint));
        return errors;
    }

    internal static (SearchRequest? Request, string? Error) ParseSearch(IQueryCollection q)
    {
        EventLevel? level = null;
        var levelText = q["minLevel"].ToString();
        if (levelText.Length > 0)
        {
            if (!Enum.TryParse<EventLevel>(levelText, ignoreCase: true, out var l) || !Enum.IsDefined(l) || int.TryParse(levelText, out _)) return (null, "Unknown level.");
            level = l;
        }

        var ids = new List<long>();
        foreach (var part in q["sources"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || ids.Count >= 100) return (null, "Invalid source list.");
            ids.Add(id);
        }

        long? from = null, to = null;
        if (q["from"].ToString() is { Length: > 0 } f)
        {
            if (!DateTimeOffset.TryParse(f, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return (null, "Invalid start time.");
            from = d.ToUnixTimeMilliseconds();
        }

        if (q["to"].ToString() is { Length: > 0 } t)
        {
            if (!DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return (null, "Invalid end time.");
            to = d.ToUnixTimeMilliseconds();
        }

        var limit = 100;
        if (q["limit"].ToString() is { Length: > 0 } lt && (!int.TryParse(lt, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1)) return (null, "Invalid limit.");
        var text = q["q"].ToString();
        var category = q["category"].ToString();
        if (text.Length > FtsQuery.MaxInputLength || category.Length > 200) return (null, "The search text is too long.");
        // Equipment filter: the AE title must appear in the event, as an extra required (quoted) term.
        var aet = q["aet"].ToString();
        if (aet.Length > 0)
        {
            if (!EquipmentService.IsValidAet(aet)) return (null, "Invalid AE title.");
            text = (text + " \"" + aet + "\"").Trim();
        }

        return (new SearchRequest
        {
            Text = text,
            MinLevel = level,
            SourceIds = ids,
            Category = category.Length == 0 ? null : category,
            FromMs = from,
            ToMs = to,
            ExceptionsOnly = q["exceptions"] == "1",
            IncludeUnparsed = q["unparsed"] != "0",
            OldestFirst = q["order"] == "oldest",
            Limit = Math.Min(limit, SearchService.MaxLimit),
            Cursor = q["cursor"].ToString() is { Length: > 0 } c ? c : null,
        }, null);
    }

    /// <summary>CSV cell with quoting and protection against spreadsheet formula injection.</summary>
    internal static string Csv(string value)
    {
        var v = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t') v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static IResult Bad(string message) => Results.BadRequest(new { error = "invalid", message });

    private static long SizeOf(string file) => File.Exists(file) ? new FileInfo(file).Length : 0;

    private static long DirectorySize(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
}

/// <summary>Marks endpoints that need the administrator role.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AdminOnlyAttribute : Attribute;

/// <summary>Counts imports in progress so temporary files are not cleaned while in use.</summary>
public sealed class ImportTracker
{
    private int _active;

    public int Active => Volatile.Read(ref _active);

    public IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        return new Release(this);
    }

    private sealed class Release(ImportTracker owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref owner._active);
        }
    }
}
