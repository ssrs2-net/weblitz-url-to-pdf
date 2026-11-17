using System.Globalization;
using System.Text.Json.Serialization;

namespace WeblitzPdf.Sample.UrlToPdf;

/// <summary>
/// What a caller asks for: the page's address and how to print it. The same names in the query string
/// (GET /pdf?url=...&amp;format=Letter&amp;landscape=true) and in a JSON body (POST /pdf). The print settings are
/// Puppeteer's page.pdf() options, which Weblitz takes under the same names.
/// </summary>
public sealed class PdfRequest
{
    /// <summary>The page to print: https://example.com/page (example.com is taken as https://example.com).</summary>
    public string? Url { get; set; }

    /// <summary>The paper: A4 (the default), Letter, Legal, Tabloid, Ledger, A0 to A6.</summary>
    public string? Format { get; set; }

    /// <summary>The paper's size as CSS lengths ("210mm", "8.5in"): they win over <see cref="Format"/>.</summary>
    public string? Width { get; set; }
    public string? Height { get; set; }

    public bool? Landscape { get; set; }

    /// <summary>The four margins as one CSS length ("12mm"), or each by its own name; none: the page's own @page
    /// margins, else Chrome's.</summary>
    public string? Margin { get; set; }
    public string? MarginTop { get; set; }
    public string? MarginRight { get; set; }
    public string? MarginBottom { get; set; }
    public string? MarginLeft { get; set; }

    /// <summary>Background colors and pictures (true by default).</summary>
    public bool? Background { get; set; }

    /// <summary>0.1 to 2.</summary>
    public double? Scale { get; set; }

    /// <summary>The pages kept: "1-5, 8".</summary>
    public string? Pages { get; set; }

    /// <summary>Whether the page's scripts run (true by default).</summary>
    public bool? Scripts { get; set; }

    /// <summary>Seconds of the page's own time (timers, animations) before it is printed: 5 by default, 30 at most. They
    /// pass at once: nothing waits.</summary>
    public double? Wait { get; set; }

    /// <summary>Scrolls through the page first, as a reader would, for pages that load their content as it comes into
    /// view.</summary>
    public bool? Scroll { get; set; }

    /// <summary>CSS selectors left out of the print: "nav, .sidebar, #cookie-banner" (or the parameter repeated).</summary>
    public string[]? Hide { get; set; }

    /// <summary>CSS of your own, added to the page before it prints.</summary>
    public string? Css { get; set; }

    /// <summary>A header and footer on each page: the page's title and address, the date and the page numbers.</summary>
    public bool? HeaderFooter { get; set; }

    /// <summary>What the page's scripts see: "fr-CH", "Europe/Zurich".</summary>
    public string? Locale { get; set; }
    public string? TimeZone { get; set; }

    /// <summary>The PDF's title (the page's own by default).</summary>
    public string? Title { get; set; }

    /// <summary>true: the answer is a file to save (Content-Disposition: attachment), not shown in the browser.</summary>
    public bool? Download { get; set; }

    /// <summary>The file's name (the page's host and path by default).</summary>
    public string? FileName { get; set; }

    /// <summary>true: a page the site answered with an error (404, 403...) is refused with 502, not printed.</summary>
    public bool? FailOnHttpError { get; set; }

    [JsonIgnore] internal const double MaxWaitSeconds = 30;

    /// <summary>The papers Weblitz knows by name (Puppeteer's, and CSS's B sizes).</summary>
    private static readonly HashSet<string> Papers = new(StringComparer.OrdinalIgnoreCase)
    {
        "A0", "A1", "A2", "A3", "A4", "A5", "A6", "B4", "B5", "JIS-B4", "JIS-B5", "Letter", "Legal", "Tabloid", "Ledger",
    };

    /// <summary>The request read from a query string.</summary>
    internal static PdfRequest FromQuery(IQueryCollection query)
    {
        string? Text(string name) => query.TryGetValue(name, out var value) && value.Count > 0 && !string.IsNullOrWhiteSpace(value[^1]) ? value[^1]!.Trim() : null;
        bool? Flag(string name) => Text(name) switch
        {
            null => null,
            var t when t is "1" || t.Equals("true", StringComparison.OrdinalIgnoreCase) || t.Equals("yes", StringComparison.OrdinalIgnoreCase) || t.Equals("on", StringComparison.OrdinalIgnoreCase) => true,
            var t when t is "0" || t.Equals("false", StringComparison.OrdinalIgnoreCase) || t.Equals("no", StringComparison.OrdinalIgnoreCase) || t.Equals("off", StringComparison.OrdinalIgnoreCase) => false,
            var t => throw new BadRequestException($"{name}={t}: write true or false."),
        };
        double? Number(string name) => Text(name) is not { } t ? null
            : double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n
            : throw new BadRequestException($"{name}={t}: write a number.");

        return new PdfRequest
        {
            Url = Text("url"),
            Format = Text("format"),
            Width = Text("width"),
            Height = Text("height"),
            Landscape = Flag("landscape"),
            Margin = Text("margin"),
            MarginTop = Text("marginTop"),
            MarginRight = Text("marginRight"),
            MarginBottom = Text("marginBottom"),
            MarginLeft = Text("marginLeft"),
            Background = Flag("background"),
            Scale = Number("scale"),
            Pages = Text("pages"),
            Scripts = Flag("scripts"),
            Wait = Number("wait"),
            Scroll = Flag("scroll"),
            Hide = query.TryGetValue("hide", out var hide) ? hide.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h!).ToArray() : null,
            Css = Text("css"),
            HeaderFooter = Flag("headerFooter"),
            Locale = Text("locale"),
            TimeZone = Text("timeZone"),
            Title = Text("title"),
            Download = Flag("download"),
            FileName = Text("fileName"),
            FailOnHttpError = Flag("failOnHttpError"),
        };
    }

    /// <summary>The engine's options for this request.</summary>
    internal HtmlPdfOptions ToOptions(ShowcaseOptions showcase)
    {
        // (the engine prints an unknown paper as A4 and notes it; a caller of an API is told at once)
        if (Format is not null && !Papers.Contains(Format))
            throw new BadRequestException($"format={Format} is no paper: {string.Join(", ", Papers.Order(StringComparer.OrdinalIgnoreCase))}.");
        if (Scale is { } scale && scale is < 0.1 or > 2) throw new BadRequestException("scale is between 0.1 and 2.");
        if (Wait is { } wait && wait is < 0 or > MaxWaitSeconds) throw new BadRequestException($"wait is between 0 and {MaxWaitSeconds:0} seconds.");
        if (Css is { Length: > 20_000 }) throw new BadRequestException("css holds 20,000 characters at most.");
        // (each selector its own rule: "nav, .sidebar" is two)
        var hide = (Hide ?? []).SelectMany(h => h.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
        if (hide.Length > 50) throw new BadRequestException("hide takes 50 selectors at most.");

        var margin = (Margin ?? MarginTop ?? MarginRight ?? MarginBottom ?? MarginLeft) is null ? null
            : new HtmlMargins(MarginTop ?? Margin, MarginRight ?? Margin, MarginBottom ?? Margin, MarginLeft ?? Margin);

        var allowed = new List<string> { "*" };          // any public host, and the private ones the host's owner named
        allowed.AddRange(showcase.AllowPrivateHosts);

        var options = new HtmlPdfOptions
        {
            Width = Width,
            Height = Height,
            Landscape = Landscape ?? false,
            Margin = margin,
            PrintBackground = Background ?? true,
            Scale = Scale ?? 1,
            PageRanges = Pages,
            Scripts = new HtmlScripts
            {
                Enabled = Scripts ?? true,
                Timeout = TimeSpan.FromSeconds(showcase.ScriptSeconds),
                WaitFor = TimeSpan.FromSeconds(Wait ?? 5),
            },
            Network = new HtmlNetwork
            {
                AllowedHosts = allowed,
                TotalTimeout = TimeSpan.FromSeconds(showcase.NetworkSeconds),
            },
            ScrollThrough = Scroll ?? false,
            Hide = hide,
            Styles = Css is null ? [] : [Css],
            DisplayHeaderFooter = HeaderFooter ?? false,
            Title = Title,
            Limits = HtmlPdfLimits.Default with { MaxPages = showcase.MaxPages },
        };
        if (Format is not null) options = options with { Format = Format };
        if (Locale is not null) options = options with { Locale = Locale };
        if (TimeZone is not null) options = options with { TimeZone = TimeZone };
        return options;
    }

    /// <summary>The PDF's file name: the caller's, else the page's host and path ("example.com-docs-install.pdf").</summary>
    internal string FileNameFor(Uri address)
    {
        var name = FileName ?? (address.Host + address.AbsolutePath).TrimEnd('/');
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray()).Trim('-', '.');
        while (safe.Contains("--", StringComparison.Ordinal)) safe = safe.Replace("--", "-", StringComparison.Ordinal);
        if (safe.Length == 0) safe = "page";
        if (safe.Length > 120) safe = safe[..120];
        return safe.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? safe : safe + ".pdf";
    }
}

/// <summary>A request the API refuses with 400 and this message.</summary>
internal sealed class BadRequestException(string message) : Exception(message);
