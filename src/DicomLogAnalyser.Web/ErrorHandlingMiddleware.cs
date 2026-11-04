using DicomLogAnalyser.Core.Bundles;
using DicomLogAnalyser.Core.Diagnostics;

namespace DicomLogAnalyser.Web;

/// <summary>Turns exceptions into generic JSON errors. Diagnostics record the exception type and a reference only.</summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (BundleRejectedException ex)
        {
            await Write(context, StatusCodes.Status400BadRequest, "bundle_rejected", ex.Message).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await Write(context, StatusCodes.Status413PayloadTooLarge, "too_large", "The upload is larger than allowed.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away.
        }
        catch (Exception ex)
        {
            var reference = SafeError.NewReference();
            logger.LogError("Unhandled {Type} (ref {Reference}).", SafeError.Describe(ex), reference);
            await Write(context, StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred (reference " + reference + ").").ConfigureAwait(false);
        }
    }

    private static async Task Write(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        SecurityHeaders(context);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = code, message }).ConfigureAwait(false);
    }

    private static void SecurityHeaders(HttpContext context) => Security.SecurityMiddleware.AddSecurityHeaders(context.Response, context.Request.IsHttps);
}
