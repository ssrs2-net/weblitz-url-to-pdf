# Web page to PDF: a Weblitz showcase

An HTTP API that takes a web address and answers its PDF, printed by [Weblitz](https://weblitzpdf.com) (the
[`WeblitzPdf`](https://www.nuget.org/packages/WeblitzPdf) NuGet package for .NET). No browser runs: the page's CSS is
laid out, its scripts run and its print styles applied by the package, inside the API's own process.

```bash
curl -o page.pdf "http://localhost:8080/pdf?url=https://example.com"
```

A sample to read, run and copy from, not a supported product.

## Try it

**Here on GitHub, without installing anything.** [Open an issue with the "Print a page" form](../../issues/new?template=print.yml):
give a public page's address, and in about a minute a comment answers with the PDF. The page is printed on GitHub's
runners by this repository's own image.

**In a codespace.** *Code > Codespaces > Create codespace*: the API builds, starts and opens in a preview, with a form.
It runs in your own codespace, on your GitHub account's free hours.

**As a container.**

```bash
docker run --rm -p 8080:8080 ghcr.io/ssrs2-net/weblitz-url-to-pdf
```

then open http://localhost:8080, or use `curl` as above. The image is built for x64 and ARM64.

**From the source** (the .NET 10 SDK):

```bash
dotnet run --project src -c Release --urls http://localhost:8080
```

Without a licence each page carries a trial watermark: the showcase runs without one. Your SSRS2 licence covers
Weblitz: give it to the container or the process as the `SSRS2_LICENSE` environment variable.

## The API

| Request | Answer |
|---|---|
| `GET /pdf?url=https://example.com` | The PDF (`application/pdf`) |
| `POST /pdf` with `{"url": "https://example.com", ...}` | The same, the settings in a JSON body |
| `GET /info?url=https://example.com` | What the print says of itself as JSON: pages, time, the site's status, the engine's notes (no PDF) |
| `GET /` | A page with a form |
| `GET /health` | 200 once the engine has printed its first page, 503 before |

The settings, in the query string or the JSON body. They are Puppeteer's `page.pdf()` options under the same names,
which Weblitz takes as they are:

| Setting | Meaning | Default |
|---|---|---|
| `url` | The page: `https://example.com/page` (`example.com` is taken as https) | required |
| `format` | A0 to A6, B4, B5, Letter, Legal, Tabloid, Ledger | A4 |
| `width`, `height` | The paper as CSS lengths (`210mm`, `8.5in`); they win over `format` | |
| `landscape` | `true` or `false` | false |
| `margin`, or `marginTop`, `marginRight`, `marginBottom`, `marginLeft` | CSS lengths (`12mm`) | the page's own `@page` margins, else Chrome's |
| `background` | Background colors and pictures | true |
| `scale` | 0.1 to 2 | 1 |
| `pages` | The pages kept: `1-5, 8` | all |
| `scripts` | Whether the page's scripts run | true |
| `wait` | Seconds of the page's own time (timers, animations) before printing, 30 at most; they pass at once | 5 |
| `scroll` | Scrolls through the page first, for pages that load their content as it comes into view | false |
| `hide` | CSS selectors left out: `nav, .sidebar, #cookie-banner` (or the setting repeated; an array in JSON) | |
| `css` | CSS of your own, added before the page prints | |
| `headerFooter` | A header and footer on each page: title, address, date, page numbers | false |
| `locale`, `timeZone` | What the page's scripts see: `fr-CH`, `Europe/Zurich` | en-US, UTC |
| `title` | The PDF's title | the page's |
| `download`, `fileName` | A file to save (`attachment`) and its name | shown in the browser; the page's host and path |
| `failOnHttpError` | `true`: a page the site answers with an error (404, 403...) is refused with 502 | false: it is printed, as a browser prints it |

Beside the PDF, the answer's headers say `X-Weblitz-Pages`, `X-Weblitz-Elapsed-Ms`, `X-Weblitz-Completed` (false when a
limit stopped the print: the pages so far are the PDF), `X-Weblitz-Document-Status` (what the site answered) and
`X-Weblitz-Notes` (how many notes: `GET /info` lists them).

Refusals are JSON (`application/problem+json`, the reason in `detail`): 400 for an address or a setting the API does
not take, 401 without the key when one is set, 429 past the requests a minute, 502 when the page could not be printed
at all, 503 with `Retry-After` when every place is taken.

## The code

The whole of it is in `src/`, about 500 lines:

- `Program.cs`: the endpoints, and the call that does the work, `HtmlToPdf.RenderUrlAsync(address, options)`.
- `PdfRequest.cs`: a request's settings turned into the package's `HtmlPdfOptions`.
- `AddressGuard.cs`: which addresses the API prints (below).
- `ShowcaseOptions.cs`: the host's settings.

In an application of your own, the package alone does it:

```csharp
using WeblitzPdf;

var result = await HtmlToPdf.RenderUrlAsync(new Uri("https://example.com"), new HtmlPdfOptions
{
    Format = "A4",
    Network = HtmlNetwork.Any,
});
File.WriteAllBytes("page.pdf", result.Pdf!);
```

## What a public host must know

**It prints public pages only.** An API that fetches what its callers name must not reach its own network for them (a
cloud's metadata address, an admin page on localhost). Two checks:

- `AddressGuard` answers the caller before any render: http and https only, no user name or password in the address,
  and a host whose addresses are all public.
- Weblitz itself refuses private addresses for every fetch of the render (`HtmlNetwork` with `*`: any public host):
  a redirect, a frame, a picture or a script's `fetch()` leading to one is not followed. The tests hold both
  (`APageLeadingToAnotherPrivateAddress_NeverReachesIt`).

`Weblitz:AllowPrivateHosts` names the exceptions (an intranet's host, `localhost` while developing).

**A render is CPU work.** `Weblitz:MaxConcurrentRenders` bounds how many run at once (half the processors by default);
the others wait `QueueSeconds`, then are told to come back. `RequestsPerMinute` holds one address to its share; behind
a proxy, [configure forwarded headers](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer) so
that the caller's address is the one counted. `MaxPages`, `ScriptSeconds` and `NetworkSeconds` bound what one page may
ask for.

**A key.** With `Weblitz:ApiKey` set, callers send it as the `X-Api-Key` header.

## Settings

The `Weblitz` section of `src/appsettings.json`, or environment variables (`Weblitz__MaxPages=50`):

| Setting | Meaning | Default |
|---|---|---|
| `MaxConcurrentRenders` | Pages printed at once; 0: half the processors | 0 |
| `QueueSeconds` | How long a request waits for a place | 10 |
| `RequestsPerMinute` | From one address; 0: no limit | 20 |
| `MaxPages` | The most pages a PDF holds | 200 |
| `ScriptSeconds` | How long a page's scripts may run | 30 |
| `NetworkSeconds` | How long a page and its resources may take, in all | 45 |
| `AllowPrivateHosts` | Hosts allowed although not public | none |
| `ApiKey` | The key callers send; empty: none asked | empty |
| `WarmUp` | A few renders at start, so the first caller's print is as fast as the hundredth's | true |

Set `DOTNET_TieredPGO_InstrumentOnlyHotCode=0` in the process's environment (the image does): .NET reaches its
optimised code sooner. It is an environment variable only.

On a Linux server, install the fonts it lacks for Arabic, Chinese, Japanese, Korean, Thai, the Indic scripts and emoji
(the image does: `fonts-noto-core fonts-noto-cjk fonts-noto-color-emoji`); without them such text prints as empty
boxes.

## Tests

The API started in memory, printing a small site of the tests' own:

```bash
dotnet test tests -c Release
```

## Licence

The showcase's code is under the MIT licence (`LICENSE`). The `WeblitzPdf` package it references is a commercial
product with a free trial (the watermark): [weblitzpdf.com](https://weblitzpdf.com).
