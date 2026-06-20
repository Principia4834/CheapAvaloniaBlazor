using Avalonia.Controls;
using Microsoft.Extensions.Logging;

namespace CheapAvaloniaBlazor.Services;

/// <summary>
/// Extracts and manages cookies via the Avalonia NativeWebView cookie manager.
/// Wraps <see cref="NativeWebViewCookieManager"/> returned by <see cref="NativeWebView.TryGetCookieManager"/>.
/// </summary>
public class CookieService : ICookieService
{
    private readonly ILogger<CookieService>? _logger;
    private NativeWebView? _webView;

    public CookieService(ILogger<CookieService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Called by BlazorHostWindow after the NativeWebView is created.
    /// </summary>
    internal void AttachToWebView(NativeWebView webView)
    {
        _webView = webView;
        _logger?.LogInformation("CookieService attached to NativeWebView");
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, string>> GetCookiesAsync(string uri)
    {
        if (_webView is null)
        {
            _logger?.LogWarning("No NativeWebView attached — cannot extract cookies");
            return [];
        }

        var cookieManager = _webView.TryGetCookieManager();
        if (cookieManager is null)
        {
            _logger?.LogWarning("Cookie manager not available yet. URI: {Uri}", uri);
            return [];
        }

        try
        {
            // GetCookiesAsync returns all cookies; filter by the requested URI's host
            var allCookies = await cookieManager.GetCookiesAsync();

            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri))
            {
                // If URI is invalid, return all cookies
                return allCookies?.ToDictionary(c => c.Name, c => c.Value) ?? [];
            }

            var host = parsedUri.Host;
            return allCookies?
                .Where(c => string.IsNullOrEmpty(c.Domain)
                    || host.EndsWith(c.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
                .ToDictionary(c => c.Name, c => c.Value)
                ?? [];
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get cookies for URI: {Uri}", uri);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetCookieAsync(string uri, string cookieName)
    {
        var cookies = await GetCookiesAsync(uri);
        return cookies.GetValueOrDefault(cookieName);
    }

    /// <inheritdoc />
    public async Task DeleteCookiesAsync(string domain)
    {
        if (_webView is null)
        {
            _logger?.LogWarning("No NativeWebView attached — cannot delete cookies");
            return;
        }

        var cookieManager = _webView.TryGetCookieManager();
        if (cookieManager is null)
        {
            _logger?.LogWarning("Cookie manager not available — cannot delete cookies for domain: {Domain}", domain);
            return;
        }

        try
        {
            // Get all cookies for the domain, then delete them individually
            var allCookies = await cookieManager.GetCookiesAsync();
            if (allCookies is null) return;

            var targetDomain = domain.TrimStart('.');
            int deleted = 0;
            foreach (var cookie in allCookies)
            {
                var cookieDomain = cookie.Domain.TrimStart('.');
                if (cookieDomain.EndsWith(targetDomain, StringComparison.OrdinalIgnoreCase)
                    || targetDomain.EndsWith(cookieDomain, StringComparison.OrdinalIgnoreCase))
                {
                    cookieManager.DeleteCookie(cookie.Name, cookie.Domain, cookie.Path);
                    deleted++;
                }
            }

            _logger?.LogDebug("Deleted {Count} cookies for domain: {Domain}", deleted, domain);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete cookies for domain: {Domain}", domain);
        }
    }
}
