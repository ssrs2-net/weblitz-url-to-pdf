// A showcase of Weblitz (the WeblitzPdf package): send a web address, get its PDF.
//
//   GET  /pdf?url=https://example.com            the PDF (application/pdf)
//   POST /pdf   {"url": "https://example.com"}   the same, the settings in a JSON body
//   POST /pdf   {"html": "<h1>Hello</h1>"}        the PDF of the HTML itself (its resources from public hosts)
//   GET  /info?url=https://example.com           what the print says of itself, as JSON: pages, time, notes (no PDF)
//   GET  /                                       a page with a form
//   GET  /health                                 200 once the engine has printed a page
//
// Run as weblitzpdf.com/try's render service (its ops/weblitz-try), WEBLITZ_TRY_KEY set: every request but /health
// carries that key in the X-Weblitz-Try-Key header, or is refused.
//
// The print settings are Puppeteer's page.pdf() options under the same names (PdfRequest.cs).

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using WeblitzPdf;
using WeblitzPdf.Sample.UrlToPdf;

var builder = WebApplication.CreateBuilder(args);

var showcase = builder.Configuration.GetSection("Weblitz").Get<ShowcaseOptions>() ?? new ShowcaseOptions();
builder.Services.AddSingleton(showcase);
builder.Services.AddSingleton<AddressGuard>();
builder.Services.AddSingleton<RenderGate>();
builder.Services.AddProblemDetails();

// One address may not keep the engine to itself (behind a proxy, configure forwarded headers so that the caller's
// address is the one counted)
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("renders", http => showcase.RequestsPerMinute <= 0
        ? RateLimitPartition.GetNoLimiter("all")
        : RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = showcase.RequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    limiter.OnRejected = async (context, cancellation) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await Results.Problem("Too many requests from this address: try again in a minute.", statusCode: StatusCodes.Status429TooManyRequests)
            .ExecuteAsync(context.HttpContext);
    };
});

var app = builder.Build();

// Your SSRS2 licence covers Weblitz: the SSRS2_LICENSE environment variable (the engine reads it itself), or
// Weblitz:LicenseKey in the host's own configuration. Without one, each page carries the trial watermark.
if (builder.Configuration["Weblitz:LicenseKey"] is { Length: > 0 } licenceKey)
    License.Activate(licenceKey);

// Behind weblitzpdf.com/try only the site may call (WEBLITZ_TRY_KEY, an environment variable of the container): every
// request but /health carries the key, compared in constant time, or is refused before anything else runs
if (builder.Configuration["WEBLITZ_TRY_KEY"] is { Length: > 0 } tryKey)
    app.Use(async (http, next) =>
    {
        if (http.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase) || SameKey(http.Request.Headers["X-Weblitz-Try-Key"].ToString(), tryKey))
        {
            await next();
            return;
        }
        await Results.Problem("This service answers weblitzpdf.com/try only.", statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(http);
    });

app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

var ready = false;
// .NET compiles the engine as it runs: a few renders of a small page at start make the first caller's print as fast
// as the hundredth's (HtmlToPdf.WarmUp; optional, changes no PDF)
_ = Task.Run(async () =>
{
    try
    {
        if (showcase.WarmUp)
        {
            var warm = await HtmlToPdf.WarmUpAsync(
                "<!doctype html><title>Warm-up</title><style>body{font:14px sans-serif;display:grid;gap:8px}</style><h1>Warm-up</h1>" +
                "<table><tr><td>1<td>2</table><p id=p></p><script>document.getElementById('p').textContent='ready '+[1,2,3].map(x=>x*2).join()</script>",
                renders: 6);
            app.Logger.LogInformation("Warm-up: {Renders} renders, the first {First:0} ms, the last {Last:0} ms", warm.Renders, warm.First.TotalMilliseconds, warm.Last.TotalMilliseconds);
        }
        else
            await HtmlToPdf.RenderAsync("<p>ready</p>");
    }
    catch (Exception problem)
    {
        app.Logger.LogError(problem, "The engine's first render failed");
    }
    ready = true;
});

app.MapGet("/health", () => ready ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "starting" }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapGet("/pdf", (HttpContext http, AddressGuard guard, RenderGate gate) =>
    Print(http, () => PdfRequest.FromQuery(http.Request.Query), guard, gate, showcase, app.Logger, pdf: true)).RequireRateLimiting("renders");

app.MapPost("/pdf", (HttpContext http, [FromBody] PdfRequest request, AddressGuard guard, RenderGate gate) =>
    Print(http, () => request, guard, gate, showcase, app.Logger, pdf: true)).RequireRateLimiting("renders");

app.MapGet("/info", (HttpContext http, AddressGuard guard, RenderGate gate) =>
    Print(http, () => PdfRequest.FromQuery(http.Request.Query), guard, gate, showcase, app.Logger, pdf: false)).RequireRateLimiting("renders");

app.Run();

static async Task<IResult> Print(HttpContext http, Func<PdfRequest> read, AddressGuard guard, RenderGate gate, ShowcaseOptions showcase, ILogger log, bool pdf)
{
    if (showcase.ApiKey.Length > 0 && !SameKey(http.Request.Headers["X-Api-Key"].ToString(), showcase.ApiKey))
        return Results.Problem("This API asks for its key in the X-Api-Key header.", statusCode: StatusCodes.Status401Unauthorized);

    PdfRequest request;
    HtmlPdfOptions options;
    try
    {
        request = read();
        options = request.ToOptions(showcase);
    }
    catch (BadRequestException refused)
    {
        return Results.Problem(refused.Message, statusCode: StatusCodes.Status400BadRequest);
    }

    // Posted HTML, or a page's address checked before any render starts
    Uri? address = null;
    if (request.Html is { } html)
    {
        if (request.Url is { Length: > 0 })
            return Results.Problem("Give the page's address or its HTML, not both.", statusCode: StatusCodes.Status400BadRequest);
        if (Encoding.UTF8.GetByteCount(html) > showcase.MaxHtmlKilobytes * 1024)
            return Results.Problem($"The HTML is larger than {showcase.MaxHtmlKilobytes:N0} KB.", statusCode: StatusCodes.Status413PayloadTooLarge);
        if (string.IsNullOrWhiteSpace(html))
            return Results.Problem("The HTML is empty.", statusCode: StatusCodes.Status400BadRequest);
    }
    else
    {
        var verdict = await guard.CheckAsync(request.Url, http.RequestAborted);
        if (verdict.Address is not { } checkedAddress)
            return Results.Problem(verdict.Refusal, statusCode: StatusCodes.Status400BadRequest);
        address = checkedAddress;
    }
    var what = address?.AbsoluteUri ?? "The HTML";

    // A render is CPU work: a bounded number at once, the others wait their turn a few seconds
    using var place = await gate.EnterAsync(http.RequestAborted);
    if (place is null)
    {
        http.Response.Headers.RetryAfter = "5";
        return Results.Problem("The server is printing as many pages as it can at once: try again in a few seconds.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    HtmlPdfResult result;
    var watch = Stopwatch.StartNew();
    try
    {
        result = address is null
            ? await HtmlToPdf.RenderAsync(request.Html!, options, http.RequestAborted)
            : await HtmlToPdf.RenderUrlAsync(address, options, http.RequestAborted);
    }
    catch (ArgumentException refused)           // a setting the engine does not take ("format=A9", a margin that is no length)
    {
        return Results.Problem(refused.Message, statusCode: StatusCodes.Status400BadRequest);
    }
    catch (HtmlRenderException failed)          // no PDF at all: the page did not answer, or is past the limits
    {
        log.LogInformation("{Address}: no PDF: {Reason}", what, failed.Message);
        return Results.Problem($"{what} could not be printed: {failed.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);           // the caller left
    }

    log.LogInformation("{Address}: {Pages} pages in {Elapsed:0} ms, {Notes} notes, document status {Status}",
        what, result.PageCount, watch.Elapsed.TotalMilliseconds, result.Notes.Count, result.HttpStatus);

    if (request.FailOnHttpError == true && result.HttpStatus is >= 400)
        return Results.Problem($"{what} answered {result.HttpStatus}.", statusCode: StatusCodes.Status502BadGateway);

    if (!pdf)
        return Results.Json(new
        {
            url = address?.AbsoluteUri,
            pages = result.PageCount,
            elapsedMs = Math.Round(result.Elapsed.TotalMilliseconds),
            completed = result.Completed,
            documentStatus = result.HttpStatus,
            notes = result.Notes.Select(note => new { code = note.Code, severity = note.Severity.ToString(), message = note.Message, element = note.Element }),
        });

    if (result.Pdf is not { Length: > 0 } bytes)
        return Results.Problem($"{what} printed no page.", statusCode: StatusCodes.Status502BadGateway);

    // What the print says of itself, beside the PDF (GET /info gives the notes themselves)
    var headers = http.Response.Headers;
    headers["X-Weblitz-Pages"] = result.PageCount.ToString();
    headers["X-Weblitz-Elapsed-Ms"] = Math.Round(result.Elapsed.TotalMilliseconds).ToString();
    headers["X-Weblitz-Completed"] = result.Completed ? "true" : "false";
    headers["X-Weblitz-Notes"] = result.Notes.Count.ToString();
    if (result.HttpStatus is { } status) headers["X-Weblitz-Document-Status"] = status.ToString();
    headers.CacheControl = "no-store";
    headers.ContentDisposition = $"{(request.Download == true ? "attachment" : "inline")}; filename=\"{request.FileNameFor(address)}\"";
    return Results.Bytes(bytes, "application/pdf");
}

static bool SameKey(string given, string expected) =>
    CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

/// <summary>The places a render takes: as many at once as the settings say, the others waiting a few seconds.</summary>
internal sealed class RenderGate(ShowcaseOptions options)
{
    private readonly SemaphoreSlim _places = new(options.Places, options.Places);

    /// <summary>A place, to dispose when the render is done; null when none came free in time.</summary>
    public async Task<IDisposable?> EnterAsync(CancellationToken cancellation)
    {
        try
        {
            return await _places.WaitAsync(TimeSpan.FromSeconds(Math.Max(0, options.QueueSeconds)), cancellation) ? new Place(_places) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private sealed class Place(SemaphoreSlim places) : IDisposable
    {
        private int _left;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0) places.Release();
        }
    }
}

/// <summary>For the tests' host.</summary>
public partial class Program;
