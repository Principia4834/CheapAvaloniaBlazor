using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Runtime context for CheapAvaloniaBlazor.
/// </summary>
/// <remarks>
/// This static holder bridges the Avalonia-side DI container (built by <see cref="CheapAvaloniaBlazor.Hosting.HostBuilder"/>)
/// with Avalonia controls that cannot participate in constructor injection (e.g. <c>BlazorHostWindow</c>).
/// It must be initialized exactly once via <c>HostBuilder.BuildAvaloniaApp()</c> before any services
/// are resolved. All other code should use constructor injection where possible.
/// </remarks>
public static class CheapAvaloniaBlazorRuntime
{
    private static IServiceProvider? _serviceProvider;
    private static readonly Lock _initLock = new();

    internal static void Initialize(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        lock (_initLock)
        {
            // Allow re-initialization with the same provider (e.g. Build<T>() + RunApp() sharing one build).
            // Reject attempts to swap in a different provider — that indicates a programming error.
            if (_serviceProvider is not null && !ReferenceEquals(_serviceProvider, serviceProvider))
                throw new InvalidOperationException(
                    "CheapAvaloniaBlazorRuntime has already been initialized with a different service provider. " +
                    "Ensure RunApp() or BuildAvaloniaApp() is called only once per process.");
            _serviceProvider = serviceProvider;
        }
    }

    public static T GetRequiredService<T>() where T : notnull
    {
        if (_serviceProvider == null)
        {
            throw new InvalidOperationException(
                "CheapAvaloniaBlazor has not been initialized. " +
                "Make sure to call UseCheapAvaloniaBlazor() in your AppBuilder configuration.");
        }

        return _serviceProvider.GetRequiredService<T>();
    }

    public static T? GetService<T>() where T : class
    {
        return _serviceProvider?.GetService<T>();
    }
}