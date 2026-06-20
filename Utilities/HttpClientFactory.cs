namespace CheapAvaloniaBlazor.Utilities;

/// <summary>
/// Creates short-lived <see cref="HttpClient"/> instances used only for server readiness probes.
/// Named <c>BlazorServerProbe</c> (not <c>HttpClientFactory</c>) to avoid confusion with
/// <see cref="System.Net.Http.IHttpClientFactory"/> from the framework (B13).
/// </summary>
public static class BlazorServerProbe
{
    /// <summary>
    /// Create an HttpClient configured for server readiness checks
    /// </summary>
    public static HttpClient CreateForServerCheck()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Constants.Defaults.HttpClientTimeoutSeconds)
        };
    }
}
