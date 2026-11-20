using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace WeblitzPdf.Sample.UrlToPdf.Tests;

/// <summary>
/// The showcase API, started in memory, printing a site of the tests' own on 127.0.0.1 (named in AllowPrivateHosts:
/// by default the API prints public pages only, which is what the refusal tests hold).
/// </summary>
public sealed class UrlToPdfApiTests : IAsyncLifetime
{
    private TestSite _site = null!;

    public async Task InitializeAsync() => _site = await TestSite.StartAsync();

    public async Task DisposeAsync() => await _site.DisposeAsync();

    /// <summary>The API with the test site's address allowed (unless told otherwise) and no rate limit or warm-up.</summary>
    private WebApplicationFactory<Program> Api(bool allowTestSite = true, params (string Name, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("Weblitz:WarmUp", "false");
            host.UseSetting("Weblitz:RequestsPerMinute", "0");
            if (allowTestSite) host.UseSetting("Weblitz:AllowPrivateHosts:0", "127.0.0.1");
            foreach (var (name, value) in settings) host.UseSetting(name, value);
        });

    private string Page(string path) => Uri.EscapeDataString(new Uri(_site.Address, path).AbsoluteUri);

    private static async Task<string> Detail(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString()!;

    [Fact]
    public async Task AnAddress_IsAnsweredWithItsPdf()
    {
        using var api = Api();
        var response = await api.CreateClient().GetAsync($"/pdf?url={Page("/page")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        var pdf = await response.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 8));
        Assert.Equal("1", response.Headers.GetValues("X-Weblitz-Pages").Single());
        Assert.Equal("true", response.Headers.GetValues("X-Weblitz-Completed").Single());
        Assert.Equal("200", response.Headers.GetValues("X-Weblitz-Document-Status").Single());
        Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("127.0.0.1-page.pdf", response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.Equal(1, _site.Hits("/page"));
    }

    [Fact]
    public async Task ThePaperAndItsOrientation_AreThePdfs()
    {
        using var api = Api();
        var client = api.CreateClient();

        var a4 = Encoding.Latin1.GetString(await client.GetByteArrayAsync($"/pdf?url={Page("/page")}"));
        var letter = Encoding.Latin1.GetString(await client.GetByteArrayAsync($"/pdf?url={Page("/page")}&format=Letter&landscape=true"));

        Assert.Contains("/MediaBox [0 0 594.95996 841.91998]", a4);
        Assert.Contains("/MediaBox [0 0 792 612]", letter);
    }

    [Fact]
    public async Task APostedRequest_IsPrintedAsTheQuerys()
    {
        using var api = Api();
        var response = await api.CreateClient().PostAsJsonAsync("/pdf", new { url = new Uri(_site.Address, "/two-pages").AbsoluteUri, download = true, fileName = "report" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2", response.Headers.GetValues("X-Weblitz-Pages").Single());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("report.pdf", response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
    }

    [Fact]
    public async Task WhatHideNames_IsLeftOut_AndPagesKeepsSome()
    {
        using var api = Api();
        var client = api.CreateClient();

        var whole = await client.GetAsync($"/pdf?url={Page("/two-pages")}");
        var hidden = await client.GetAsync($"/pdf?url={Page("/two-pages")}&hide=.second");
        var first = await client.GetAsync($"/pdf?url={Page("/two-pages")}&pages=1");

        Assert.Equal("2", whole.Headers.GetValues("X-Weblitz-Pages").Single());
        Assert.Equal("1", hidden.Headers.GetValues("X-Weblitz-Pages").Single());
        Assert.Equal("1", first.Headers.GetValues("X-Weblitz-Pages").Single());
    }

    /// <summary>POST /pdf {"html": ...} prints the HTML itself (weblitzpdf.com/try forwards a visitor's HTML here).</summary>
    [Fact]
    public async Task PostedHtml_IsPrinted()
    {
        using var api = Api();
        var response = await api.CreateClient().PostAsJsonAsync("/pdf", new { html = "<!doctype html><h1>Posted</h1><p id=p>before</p><script>document.getElementById('p').textContent = 'after ' + (6 * 7)</script>" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("1", response.Headers.GetValues("X-Weblitz-Pages").Single());
        Assert.Equal("document.pdf", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
    }

    [Fact]
    public async Task PostedHtml_IsRefused_WithAnAddressBesideIt_OrPastItsSize()
    {
        using var api = Api(settings: ("Weblitz:MaxHtmlKilobytes", "1"));
        var client = api.CreateClient();

        var both = await client.PostAsJsonAsync("/pdf", new { html = "<h1>Hi</h1>", url = new Uri(_site.Address, "/page").AbsoluteUri });
        var large = await client.PostAsJsonAsync("/pdf", new { html = "<p>" + new string('x', 2000) + "</p>" });
        var empty = await client.PostAsJsonAsync("/pdf", new { html = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Contains("not both", await Detail(both));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Contains("larger than 1 KB", await Detail(large));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(0, _site.Hits("/page"));
    }

    /// <summary>The public settings: an address's consent boxes and ads left out when the host says so (the caller may
    /// keep them), posted HTML printed as written, and the limits on a page's nodes and resources passed to the engine.</summary>
    [Fact]
    public void ConsentAdsAndLimits_AreTheHostsUnlessTheCallerSays()
    {
        var host = new ShowcaseOptions { HideConsent = true, BlockAds = true, MaxNodes = 200_000, MaxResourceMegabytes = 50, MaxPages = 20 };

        var page = new PdfRequest { Url = "https://example.com" }.ToOptions(host);
        Assert.Equal(HtmlConsent.Hide, page.Consent);
        Assert.Equal(HtmlAds.Block, page.Ads);
        Assert.Equal(200_000, page.Limits.MaxNodes);
        Assert.Equal(50_000_000L, page.Limits.MaxTotalResourceBytes);
        Assert.Equal(20, page.Limits.MaxPages);

        var kept = new PdfRequest { Url = "https://example.com", Consent = "keep", Ads = "keep" }.ToOptions(host);
        Assert.Equal(HtmlConsent.Keep, kept.Consent);
        Assert.Equal(HtmlAds.Keep, kept.Ads);

        var html = new PdfRequest { Html = "<h1>Hi</h1>" }.ToOptions(host);
        Assert.Equal(HtmlConsent.Keep, html.Consent);
        Assert.Equal(HtmlAds.Keep, html.Ads);

        var plain = new PdfRequest { Url = "https://example.com" }.ToOptions(new ShowcaseOptions());
        Assert.Equal(HtmlConsent.Keep, plain.Consent);
        Assert.Equal(HtmlAds.Keep, plain.Ads);
        Assert.Equal(HtmlPdfLimits.Default.MaxNodes, plain.Limits.MaxNodes);

        Assert.Throws<BadRequestException>(() => new PdfRequest { Url = "https://example.com", Ads = "maybe" }.ToOptions(host));
    }

    [Fact]
    public async Task Info_SaysWhatThePrintSaysOfItself()
    {
        using var api = Api();
        var info = await api.CreateClient().GetFromJsonAsync<JsonElement>($"/info?url={Page("/page")}");

        Assert.Equal(1, info.GetProperty("pages").GetInt32());
        Assert.True(info.GetProperty("completed").GetBoolean());
        Assert.Equal(200, info.GetProperty("documentStatus").GetInt32());
        // (the page's script ran: the engine notes it)
        Assert.Contains(info.GetProperty("notes").EnumerateArray(), note => note.GetProperty("code").GetString() == "scriptsRan");
    }

    [Theory]
    [InlineData("", "Give the page's address")]
    [InlineData("ftp://example.com/file", "http or https")]
    [InlineData("https://user:secret@example.com/", "user name or password")]
    [InlineData("http://127.0.0.1/", "not a public address")]
    [InlineData("http://localhost/", "not a public address")]
    [InlineData("http://169.254.169.254/latest/meta-data/", "not a public address")]
    [InlineData("http://10.1.2.3/", "not a public address")]
    [InlineData("http://192.168.1.1/", "not a public address")]
    [InlineData("http://[::1]/", "not a public address")]
    [InlineData("http://[fd00::1]/", "not a public address")]
    public async Task AnAddressThatIsNoPublicPage_IsRefused(string address, string reason)
    {
        using var api = Api(allowTestSite: false);
        var response = await api.CreateClient().GetAsync($"/pdf?url={Uri.EscapeDataString(address)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(reason, await Detail(response));
    }

    [Fact]
    public async Task TheTestSite_IsRefused_UnlessTheHostsOwnerNamedIt()
    {
        using var api = Api(allowTestSite: false);
        var response = await api.CreateClient().GetAsync($"/pdf?url={Page("/page")}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _site.Hits("/page"));
    }

    /// <summary>
    /// The address a caller gives is checked before the render; what the page then leads to (a redirect, a picture, a
    /// script's fetch) is the engine's to refuse: another private address, not named, is never reached.
    /// </summary>
    [Theory]
    [InlineData("/redirect")]
    [InlineData("/picture-elsewhere")]
    public async Task APageLeadingToAnotherPrivateAddress_NeverReachesIt(string path)
    {
        if (!TestSite.HasIPv6Loopback) return;       // (a machine without ::1: nothing to lead to)
        await using var other = await TestSite.StartAsync("[::1]");
        _site.Elsewhere = new Uri(other.Address, "/secret").AbsoluteUri;
        using var api = Api();

        var response = await api.CreateClient().GetAsync($"/pdf?url={Page(path)}");

        Assert.Equal(1, _site.Hits(path));
        Assert.Equal(0, other.Hits("/secret"));
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadGateway, response.StatusCode.ToString());
    }

    /// <summary>The control of the test above: named by the host's owner, the other address is reached (so its nought
    /// there is the engine's refusal, not an address the machine cannot reach).</summary>
    [Fact]
    public async Task APageLeadingToAnotherPrivateAddress_ReachesIt_WhenThatAddressIsNamedToo()
    {
        if (!TestSite.HasIPv6Loopback) return;
        await using var other = await TestSite.StartAsync("[::1]");
        _site.Elsewhere = new Uri(other.Address, "/secret").AbsoluteUri;
        using var api = Api(true, ("Weblitz:AllowPrivateHosts:1", "::1"));

        var response = await api.CreateClient().GetAsync($"/pdf?url={Page("/redirect")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, other.Hits("/secret"));
    }

    [Theory]
    [InlineData("scale=5", "scale")]
    [InlineData("wait=600", "wait")]
    [InlineData("landscape=sideways", "landscape")]
    [InlineData("format=A9", "A9")]
    public async Task ASettingOutOfRange_IsRefusedWithItsName(string setting, string named)
    {
        using var api = Api();
        var response = await api.CreateClient().GetAsync($"/pdf?url={Page("/page")}&{setting}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(named, await Detail(response));
    }

    [Fact]
    public async Task APageTheSiteAnswersWithAnError_IsPrintedAndSaid_OrRefusedWhenAsked()
    {
        using var api = Api();
        var client = api.CreateClient();

        var printed = await client.GetAsync($"/pdf?url={Page("/missing")}");
        var refused = await client.GetAsync($"/pdf?url={Page("/missing")}&failOnHttpError=true");

        Assert.Equal(HttpStatusCode.OK, printed.StatusCode);
        Assert.Equal("404", printed.Headers.GetValues("X-Weblitz-Document-Status").Single());
        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        Assert.Contains("404", await Detail(refused));
    }

    [Fact]
    public async Task WithAKeySet_CallersSendIt()
    {
        using var api = Api(true, ("Weblitz:ApiKey", "the-showcase-test-key"));
        var client = api.CreateClient();

        var without = await client.GetAsync($"/pdf?url={Page("/page")}");
        var wrong = new HttpRequestMessage(HttpMethod.Get, $"/pdf?url={Page("/page")}") { Headers = { { "X-Api-Key", "another" } } };
        var right = new HttpRequestMessage(HttpMethod.Get, $"/pdf?url={Page("/page")}") { Headers = { { "X-Api-Key", "the-showcase-test-key" } } };

        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(right)).StatusCode);
        Assert.Equal(1, _site.Hits("/page"));
    }

    /// <summary>As weblitzpdf.com/try's render service (WEBLITZ_TRY_KEY set), every request but /health carries the key
    /// in X-Weblitz-Try-Key; the home page and the PDF alike are refused without it.</summary>
    [Fact]
    public async Task AsTrysRenderService_EveryRequestButHealth_CarriesTheKey()
    {
        using var api = Api(settings: ("WEBLITZ_TRY_KEY", "the-try-key"));
        var client = api.CreateClient();

        Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/pdf?url={Page("/page")}")).StatusCode);
        var wrong = new HttpRequestMessage(HttpMethod.Get, $"/pdf?url={Page("/page")}");
        wrong.Headers.Add("X-Weblitz-Try-Key", "not-the-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);
        Assert.Equal(0, _site.Hits("/page"));

        var right = new HttpRequestMessage(HttpMethod.Post, "/pdf") { Content = JsonContent.Create(new { html = "<h1>From the site</h1>" }) };
        right.Headers.Add("X-Weblitz-Try-Key", "the-try-key");
        var response = await client.SendAsync(right);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task MoreRendersThanPlaces_AreToldToComeBack()
    {
        using var api = Api(true, ("Weblitz:MaxConcurrentRenders", "1"), ("Weblitz:QueueSeconds", "0"));
        var client = api.CreateClient();

        var slow = client.GetAsync($"/pdf?url={Page("/slow")}");
        while (_site.Hits("/slow") == 0) await Task.Delay(20);       // the first render holds the one place
        var second = await client.GetAsync($"/pdf?url={Page("/page")}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal("5", second.Headers.GetValues("Retry-After").Single());
        Assert.Equal(HttpStatusCode.OK, (await slow).StatusCode);
    }

    [Fact]
    public async Task OneAddress_IsHeldToItsRequestsAMinute()
    {
        using var api = Api(true, ("Weblitz:RequestsPerMinute", "2"));
        var client = api.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++) statuses.Add((await client.GetAsync($"/info?url={Page("/page")}")).StatusCode);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task TheHomePageAndHealth_Answer()
    {
        using var api = Api();
        var client = api.CreateClient();

        var home = await client.GetStringAsync("/");
        Assert.Contains("Web page to PDF", home);

        HttpResponseMessage health;
        var tries = 0;
        do health = await client.GetAsync("/health");
        while (health.StatusCode != HttpStatusCode.OK && tries++ < 100 && await Task.Delay(100).ContinueWith(_ => true));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("fe80::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    public void AnAddress_IsPublicOrNot(string address, bool isPublic) =>
        Assert.Equal(isPublic, AddressGuard.IsPublic(IPAddress.Parse(address)));
}
