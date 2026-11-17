using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WeblitzPdf.Sample.UrlToPdf.Tests;

/// <summary>A small site on one of the machine's loopback addresses: the pages the tests print, and a count of the
/// requests each path received (what a refused fetch must leave at nought).</summary>
public sealed class TestSite : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Dictionary<string, int> _hits = new();

    public Uri Address { get; }

    /// <summary>Where "/redirect" and "/picture-elsewhere" point: another site's secret.</summary>
    public string Elsewhere { get; set; } = "http://elsewhere.invalid/secret";

    private TestSite(WebApplication app, Uri address) { _app = app; Address = address; }

    public int Hits(string path) { lock (_hits) return _hits.GetValueOrDefault(path); }

    /// <summary>Whether the machine has the IPv6 loopback address (a second "private" address beside 127.0.0.1).</summary>
    public static bool HasIPv6Loopback
    {
        get
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
                return true;
            }
            catch (SocketException) { return false; }
        }
    }

    public static async Task<TestSite> StartAsync(string host = "127.0.0.1")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{host}:0");
        var app = builder.Build();
        TestSite? site = null;
        app.Use(async (http, next) =>
        {
            lock (site!._hits) site._hits[http.Request.Path] = site._hits.GetValueOrDefault(http.Request.Path) + 1;
            await next();
        });
        IResult Page(string body, int status = 200) => Results.Content("<!doctype html><title>Test page</title>" + body, "text/html; charset=utf-8", statusCode: status);
        app.MapGet("/page", () => Page("<h1>Hello</h1><p id=p>before</p><script>document.getElementById('p').textContent = 'after ' + (6 * 7)</script>"));
        app.MapGet("/two-pages", () => Page("<h1>First</h1><div class=second style='break-before: page'>Second</div>"));
        app.MapGet("/missing", () => Page("<h1>No such page</h1>", 404));
        app.MapGet("/slow", async () => { await Task.Delay(1500); return Page("<h1>Slow</h1>"); });
        app.MapGet("/redirect", () => Results.Redirect(site!.Elsewhere));
        app.MapGet("/picture-elsewhere", () => Page($"<h1>Picture</h1><img src='{site!.Elsewhere}'><script>fetch('{site!.Elsewhere}').catch(() => 0)</script>"));
        app.MapGet("/secret", () => Page("<h1>Secret</h1>"));
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        return site = new TestSite(app, address);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
