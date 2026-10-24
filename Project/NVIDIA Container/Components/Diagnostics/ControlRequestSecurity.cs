using System;
using System.Collections.Generic;

// Protect loopback HTTP endpoints against cross-site browser requests and DNS rebinding.
// This is NOT authentication against another process running under the same OS user.
internal static class ControlRequestSecurity
{
    internal static (int Status, string Error) Validate(
        string method, IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Host", out var host) || !IsLoopbackAuthority(host))
            return (403, "Control API only accepts loopback Host headers.");

        if (headers.TryGetValue("Origin", out var origin) && !IsLoopbackOrigin(origin))
            return (403, "Cross-origin browser requests are not permitted.");

        if (headers.TryGetValue("Sec-Fetch-Site", out var fetchSite) &&
            (string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(fetchSite, "same-site", StringComparison.OrdinalIgnoreCase) &&
             !headers.ContainsKey("Origin")))
            return (403, "Cross-site browser requests are not permitted.");

        // No browser form POSTs: all existing native/NodeAPI clients use JSON.
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
            (!headers.TryGetValue("Content-Type", out var contentType) ||
             !(contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
               contentType.StartsWith("application/json;", StringComparison.OrdinalIgnoreCase))))
            return (415, "Control API POST requests must use application/json.");

        return (200, "");
    }

    private static bool IsLoopbackAuthority(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Contains('/') || host.Contains('\\') ||
            host.Contains('@') || host.Contains('?') || host.Contains('#'))
            return false;
        if (!Uri.TryCreate("http://" + host, UriKind.Absolute, out var uri) ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length != 0)
            return false;
        return IsLocalHostname(uri.Host);
    }

    private static bool IsLoopbackOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        return IsLocalHostname(uri.Host);
    }

    private static bool IsLocalHostname(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::1", StringComparison.OrdinalIgnoreCase);
}
