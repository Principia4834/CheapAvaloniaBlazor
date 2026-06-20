using System.Runtime.InteropServices;

namespace CheapAvaloniaBlazor;

/// <summary>
/// Platform detection helpers for Avalonia desktop environments.
/// </summary>
public static class PlatformHelper
{
    /// <summary>
    /// Returns true when the current platform can host an Avalonia NativeWebView.
    /// Windows and macOS are always supported. On Linux, webkit2gtk must be present.
    /// </summary>
    public static bool IsWebViewSupported()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return CheckLinuxWebViewSupport();

            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckLinuxWebViewSupport()
    {
        try
        {
            var display = Environment.GetEnvironmentVariable("DISPLAY");
            var waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");

            if (string.IsNullOrEmpty(display) && string.IsNullOrEmpty(waylandDisplay))
            {
                System.Diagnostics.Debug.WriteLine("No display environment detected");
                return false;
            }

            // Check for webkit2gtk libraries required by Avalonia WebView on Linux
            var webkitLibs = new[]
            {
                "/usr/lib/x86_64-linux-gnu/libwebkit2gtk-4.0.so",
                "/usr/lib/x86_64-linux-gnu/libwebkit2gtk-4.1.so",
                "/usr/lib/libwebkit2gtk-4.0.so",
                "/usr/lib/libwebkit2gtk-4.1.so"
            };

            foreach (var lib in webkitLibs)
            {
                if (File.Exists(lib))
                {
                    System.Diagnostics.Debug.WriteLine($"Found WebKit library: {lib}");
                    return true;
                }
            }

            System.Diagnostics.Debug.WriteLine("WebKit libraries not found");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Linux WebView support check failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Returns a human-readable reason why the web view is not supported on the current platform.
    /// Returns an empty string if the web view is supported.
    /// </summary>
    public static string GetWebViewUnsupportedReason()
    {
        if (IsWebViewSupported()) return string.Empty;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "webkit2gtk library not found. Install libwebkit2gtk-4.1-0 (or libwebkit2gtk-4.0-37).";

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "Unsupported platform";

        return "Unknown compatibility issue";
    }

    /// <summary>
    /// Writes platform diagnostic information to the debug output.
    /// </summary>
    public static void LogPlatformInfo()
    {
        System.Diagnostics.Debug.WriteLine("=== Platform Information ===");
        System.Diagnostics.Debug.WriteLine($"OS: {RuntimeInformation.OSDescription}");
        System.Diagnostics.Debug.WriteLine($"Architecture: {RuntimeInformation.OSArchitecture}");
        System.Diagnostics.Debug.WriteLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        System.Diagnostics.Debug.WriteLine($"NativeWebView Supported: {IsWebViewSupported()}");
        System.Diagnostics.Debug.WriteLine("===========================");
    }
}
