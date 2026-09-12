using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace IndepenDesk;

/// <summary>GitHub Releases üzerinden güncelleme denetimi.</summary>
internal static class UpdateChecker
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim FetchGate = new(1, 1);
    private static readonly object CacheGate = new();
    private static ReleaseInfo? _cachedRelease;
    private static DateTimeOffset _cacheExpiresAtUtc;

    public const string RepoOwner = "prcyangli";
    public const string RepoName = "IndepenDesk";
    public static string RepoUrl => $"https://github.com/{RepoOwner}/{RepoName}";

    public static string CurrentVersion
    {
        get
        {
            var v = typeof(UpdateChecker).Assembly.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Son sürümü denetler ve sonucu kullanıcıya bildirir.</summary>
    public static async Task CheckAndNotifyAsync(IWin32Window owner)
    {
        try
        {
            ReleaseInfo release = await GetLatestReleaseAsync();
            string tag = release.Tag;

            string latest = tag.TrimStart('v', 'V');
            if (!Version.TryParse(latest, out var latestVer) ||
                !Version.TryParse(CurrentVersion, out var currentVer))
            {
                AppLog.Warning(nameof(CheckAndNotifyAsync),
                    $"Could not parse release version '{tag}' or current version '{CurrentVersion}'.");
                MessageBox.Show(owner, L.T("update.invalidVersion"),
                    L.T("update.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (latestVer > currentVer)
            {
                var answer = MessageBox.Show(owner,
                    L.F("update.available", latest, CurrentVersion),
                    L.T("update.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo(release.HtmlUrl) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show(owner, L.F("update.none", CurrentVersion),
                    L.T("update.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            AppLog.Error(nameof(CheckAndNotifyAsync), ex);
            MessageBox.Show(owner, L.T("update.rateLimited"),
                L.T("update.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(CheckAndNotifyAsync), ex);
            MessageBox.Show(owner, L.T("update.error"),
                L.T("update.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static async Task<ReleaseInfo> GetLatestReleaseAsync()
    {
        lock (CacheGate)
        {
            if (_cachedRelease != null && DateTimeOffset.UtcNow < _cacheExpiresAtUtc)
                return _cachedRelease;
        }

        await FetchGate.WaitAsync();
        try
        {
            lock (CacheGate)
            {
                if (_cachedRelease != null && DateTimeOffset.UtcNow < _cacheExpiresAtUtc)
                    return _cachedRelease;
            }

            ReleaseInfo release = await FetchLatestReleaseAsync();
            lock (CacheGate)
            {
                _cachedRelease = release;
                _cacheExpiresAtUtc = DateTimeOffset.UtcNow + CacheDuration;
            }

            return release;
        }
        finally
        {
            FetchGate.Release();
        }
    }

    private static async Task<ReleaseInfo> FetchLatestReleaseAsync()
    {
        var latestUri = new Uri($"{RepoUrl}/releases/latest");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("IndepenDesk-UpdateCheck");

        using var response = await http.GetAsync(
            latestUri, HttpCompletionOption.ResponseHeadersRead);
        return ParseLatestReleaseResponse(latestUri, response);
    }

    private static ReleaseInfo ParseLatestReleaseResponse(
        Uri requestUri, HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException(
                $"GitHub temporarily rejected the update check ({(int)response.StatusCode}).",
                null, response.StatusCode);
        }

        if (!IsRedirect(response.StatusCode) || response.Headers.Location == null)
        {
            response.EnsureSuccessStatusCode();
            throw new InvalidDataException(
                $"GitHub did not redirect the latest release request (HTTP {(int)response.StatusCode}).");
        }

        Uri releaseUri = response.Headers.Location.IsAbsoluteUri
            ? response.Headers.Location
            : new Uri(requestUri, response.Headers.Location);
        if (!TryGetReleaseTag(releaseUri, out string tag))
            throw new InvalidDataException($"Untrusted or invalid release redirect '{releaseUri}'.");

        return new ReleaseInfo(tag, releaseUri.GetLeftPart(UriPartial.Path));
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool TryGetReleaseTag(Uri uri, out string tag)
    {
        tag = "";
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string prefix = $"/{RepoOwner}/{RepoName}/releases/tag/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string encodedTag = uri.AbsolutePath[prefix.Length..].TrimEnd('/');
        if (encodedTag.Length == 0 || encodedTag.Contains('/'))
            return false;

        tag = Uri.UnescapeDataString(encodedTag);
        return tag.Length > 0;
    }

    private sealed record ReleaseInfo(string Tag, string HtmlUrl);
}
