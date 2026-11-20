namespace WeblitzPdf.Sample.UrlToPdf;

/// <summary>The showcase's settings (the "Weblitz" section, or environment variables: Weblitz__MaxPages=50).</summary>
public sealed class ShowcaseOptions
{
    /// <summary>Pages printed at once; 0: half the machine's processors. A render is CPU work: more at once than
    /// processors only makes each slower.</summary>
    public int MaxConcurrentRenders { get; set; }

    /// <summary>How long a request waits for a free place before it is answered 503 with Retry-After.</summary>
    public int QueueSeconds { get; set; } = 10;

    /// <summary>Requests a minute from one address; 0: no limit.</summary>
    public int RequestsPerMinute { get; set; } = 20;

    /// <summary>The most pages a PDF holds (the document is cut there).</summary>
    public int MaxPages { get; set; } = 200;

    /// <summary>The largest HTML a caller may post (POST /pdf {"html": ...}), in kilobytes of UTF-8.</summary>
    public int MaxHtmlKilobytes { get; set; } = 512;

    /// <summary>The most elements and other nodes a page may have, the parser's and its scripts' (0: the engine's own
    /// limit, a million): what bounds a page's memory, with <see cref="MaxResourceMegabytes"/>.</summary>
    public int MaxNodes { get; set; }

    /// <summary>The most a page's resources (pictures, fonts, scripts, styles) may weigh in all, in megabytes (0: the
    /// engine's own limit, 200).</summary>
    public int MaxResourceMegabytes { get; set; }

    /// <summary>Whether a page printed from its address leaves out its consent boxes (HtmlConsent.Hide: nothing is
    /// clicked or answered) unless the caller asks for them (consent=keep).</summary>
    public bool HideConsent { get; set; }

    /// <summary>Whether a page printed from its address leaves out its advertisements and trackers (HtmlAds.Block, by
    /// the filter lists the package carries) unless the caller asks for them (ads=keep).</summary>
    public bool BlockAds { get; set; }

    /// <summary>How long a page's scripts may run.</summary>
    public int ScriptSeconds { get; set; } = 30;

    /// <summary>How long a page and its resources may take to arrive, in all.</summary>
    public int NetworkSeconds { get; set; } = 45;

    /// <summary>Hosts allowed although their address is not public ("localhost", "intranet.example", "10.0.0.5"): none by
    /// default, so the API never reaches the machine's own network for a caller.</summary>
    public string[] AllowPrivateHosts { get; set; } = [];

    /// <summary>When set, callers send it as the X-Api-Key header (the home page asks for it).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Whether the engine is warmed up at start (a few renders of a small page, discarded).</summary>
    public bool WarmUp { get; set; } = true;

    internal int Places => MaxConcurrentRenders > 0 ? MaxConcurrentRenders : Math.Max(1, Environment.ProcessorCount / 2);
}
