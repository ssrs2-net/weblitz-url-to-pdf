using System.Net;
using System.Net.Sockets;

namespace WeblitzPdf.Sample.UrlToPdf;

/// <summary>
/// Which addresses the API prints: public http and https pages. An API that fetches what a caller names must not reach
/// the machine's own network for them (a cloud's metadata address, an admin page on localhost). Weblitz refuses private
/// addresses itself for every fetch of the render (redirects, frames, scripts' requests) under HtmlNetwork.Any; this
/// check answers the caller before any render starts, with the reason.
/// </summary>
internal sealed class AddressGuard(ShowcaseOptions options)
{
    internal sealed record Verdict(Uri? Address, string? Refusal);

    public async Task<Verdict> CheckAsync(string? text, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new(null, "Give the page's address: url=https://example.com");
        text = text.Trim();
        if (text.Length > 2048)
            return new(null, "The address is longer than 2,048 characters.");
        // (example.com is taken as https://example.com)
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") || address.Host.Length == 0)
            return new(null, "The address must be an http or https address: https://example.com/page");
        if (address.UserInfo.Length > 0)
            return new(null, "An address with a user name or password in it is refused.");

        if (IsAllowedPrivate(address.Host)) return new(address, null);

        IPAddress[] addresses;
        if (IPAddress.TryParse(address.Host.Trim('[', ']'), out var literal)) addresses = [literal];
        else
        {
            try { addresses = await Dns.GetHostAddressesAsync(address.DnsSafeHost, cancellation); }
            catch (SocketException) { return new(null, $"The host {address.Host} was not found."); }
            if (addresses.Length == 0) return new(null, $"The host {address.Host} was not found.");
        }
        if (addresses.Any(a => !IsPublic(a)))
            return new(null, $"{address.Host} is not a public address: this API prints public pages only.");
        return new(address, null);
    }

    private bool IsAllowedPrivate(string host) =>
        options.AllowPrivateHosts.Any(allowed => string.Equals(allowed.Trim('[', ']'), host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether an address is one of the public Internet's: not the machine's own, a private network's, a link's,
    /// a carrier's shared range, multicast or reserved.</summary>
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127
                     || (b[0] == 100 && b[1] is >= 64 and <= 127)       // 100.64.0.0/10, carriers' shared space
                     || (b[0] == 169 && b[1] == 254)                    // link-local, clouds' metadata
                     || (b[0] == 172 && b[1] is >= 16 and <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 192 && b[1] == 0 && b[2] == 0)         // 192.0.0.0/24
                     || (b[0] == 198 && b[1] is 18 or 19)               // benchmarking
                     || b[0] >= 224);                                   // multicast and reserved
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                     || address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)
                     || (b[0] & 0xFE) == 0xFC);                         // fc00::/7, unique local
        }
        return false;
    }
}
