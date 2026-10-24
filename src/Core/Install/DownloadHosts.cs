using System;

namespace OverTheHillPlus.Core.Install;

/// <summary>
/// Where the installer's downloads may go: HTTPS on the default port to github.com or GitHub's download hosts
/// (objects.githubusercontent.com, release-assets.githubusercontent.com, any *.githubusercontent.com), with no
/// credentials in the URL. <see cref="HttpFetch"/> follows redirects by hand (at most <see cref="MaxRedirects"/>) and
/// checks every hop with <see cref="IsAllowed"/>. Pure logic so it can be unit tested.
/// </summary>
internal static class DownloadHosts
{
    public const int MaxRedirects = 5;

    private const string GitHub = "github.com";
    private const string UserContent = ".githubusercontent.com";

    /// <summary>True for an absolute HTTPS URL on github.com or a *.githubusercontent.com host (default port, no user info).</summary>
    public static bool IsAllowed(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        string host = uri.Host.ToLowerInvariant();
        // objects.githubusercontent.com and release-assets.githubusercontent.com are *.githubusercontent.com hosts.
        return host == GitHub || host.Length > UserContent.Length && host.EndsWith(UserContent, StringComparison.Ordinal);
    }

    /// <summary>The HTTP statuses that are followed as redirects.</summary>
    public static bool IsRedirect(int status) => status == 301 || status == 302 || status == 303 || status == 307 || status == 308;

    /// <summary>
    /// The URL a redirect from <paramref name="from"/> leads to (a relative Location resolves against it). Throws when
    /// there is no Location, <paramref name="hopsSoFar"/> already reached <see cref="MaxRedirects"/>, or the target
    /// isn't <see cref="IsAllowed"/>.
    /// </summary>
    public static Uri NextHop(Uri from, Uri location, int hopsSoFar)
    {
        if (hopsSoFar >= MaxRedirects) throw new InvalidOperationException($"more than {MaxRedirects} redirects");
        if (location == null) throw new InvalidOperationException("a redirect without a target");
        var next = location.IsAbsoluteUri ? location : new Uri(from, location);
        if (!IsAllowed(next)) throw new InvalidOperationException("redirected away from GitHub (" + next.Scheme + "://" + next.Host + ")");
        return next;
    }
}
