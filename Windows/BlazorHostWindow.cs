using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CheapAvaloniaBlazor.Configuration;
using CheapAvaloniaBlazor.Services;
using CheapAvaloniaBlazor.Utilities;

using AvaloniaWindowState = Avalonia.Controls.WindowState;

namespace CheapAvaloniaBlazor.Windows;

/// <summary>
/// Main window that hosts the Blazor application inside an embedded NativeWebView.
/// </summary>
/// <remarks>
/// Marked as partial for future splash screen expansion with XAML code-behind.
/// </remarks>
public partial class BlazorHostWindow : Window, IBlazorWindow
{
    private readonly IBlazorHostService? _blazorHost;
    private readonly CheapAvaloniaBlazorOptions? _options;
    private readonly DiagnosticLogger? _logger;

    private NativeWebView? _nativeWebView;

    public BlazorHostWindow()
    {
    }

    public BlazorHostWindow(IBlazorHostService? blazorHost = null)
    {
        _blazorHost = blazorHost ?? CheapAvaloniaBlazorRuntime.GetRequiredService<IBlazorHostService>();
        _options = CheapAvaloniaBlazorRuntime.GetRequiredService<CheapAvaloniaBlazorOptions>();
        var loggerFactory = CheapAvaloniaBlazorRuntime.GetRequiredService<IDiagnosticLoggerFactory>();
        _logger = loggerFactory.CreateLogger<BlazorHostWindow>();

        _logger.LogVerbose("BlazorHostWindow constructor called");
        _logger.LogVerbose("Services initialized, calling InitializeWindow");
        InitializeWindow();
        _logger.LogVerbose("BlazorHostWindow constructor completed");
    }

    public new string? Title // Match IBlazorWindow interface
    {
        get => base.Title;
        set => base.Title = value;
    }

    protected virtual void InitializeWindow()
    {
        _logger?.LogVerbose("InitializeWindow called - setting up Avalonia window");

        var splashConfig = _options?.SplashScreen;
        var showSplash = splashConfig?.Enabled ?? false;

        if (showSplash)
        {
            _logger?.LogVerbose("Splash screen enabled - showing splash during startup");

            Title = splashConfig!.Title;
            Width = splashConfig.Width;
            Height = splashConfig.Height;
            MinWidth = splashConfig.Width;
            MinHeight = splashConfig.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            CanResize = false;
            ShowInTaskbar = true;
            Opacity = 1;
            WindowDecorations = Avalonia.Controls.WindowDecorations.None;

            Content = splashConfig.CustomContentFactory?.Invoke() ?? splashConfig.CreateDefaultContent();

            _logger?.LogVerbose($"Splash screen configured: {splashConfig.Width}x{splashConfig.Height}");
        }
        else
        {
            _logger?.LogVerbose("Splash screen disabled - window will show with NativeWebView directly");

            Title = _options?.DefaultWindowTitle ?? Constants.Framework.Name;
            Width = _options?.DefaultWindowWidth ?? Constants.Defaults.MinimumWindowSize;
            Height = _options?.DefaultWindowHeight ?? Constants.Defaults.MinimumWindowSize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
        }

        _logger?.LogVerbose("Subscribing to Loaded event");
        Loaded += OnWindowLoaded;

        _logger?.LogVerbose("InitializeWindow completed");
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        var lifecycleService = CheapAvaloniaBlazorRuntime.GetService<IAppLifecycleService>() as AppLifecycleService;
        if (lifecycleService?.OnClosing() == true)
        {
            _logger?.LogVerbose("Window close cancelled by lifecycle subscriber");
            e.Cancel = true;
            return;
        }

        if (_options?.CloseToTray == true)
        {
            var trayService = CheapAvaloniaBlazorRuntime.GetService<ISystemTrayService>();
            if (trayService != null)
            {
                _logger?.LogVerbose("Window closing - minimizing to tray instead");
                e.Cancel = true;
                trayService.MinimizeToTray();
                return;
            }
        }

        _logger?.LogVerbose("Window closing - shutting down application");
        base.OnClosing(e);

        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                lifetime.Shutdown();
        });
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        _logger?.LogVerbose("OnWindowLoaded called - Avalonia window loaded");

        try
        {
            await InitializeWebView();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error initializing web view: {ErrorMessage}", ex.Message);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
    }

    private async Task InitializeWebView()
    {
        if (!GuardClauses.RequireServices(_logger, _blazorHost, _options))
            return;

        _logger?.LogVerbose("InitializeWebView - starting Blazor host");

        if (!_blazorHost.IsRunning)
        {
            _logger?.LogVerbose("Starting Blazor host...");
            await _blazorHost.StartAsync();
        }

        var baseUrl = _blazorHost.BaseUrl;
        _logger?.LogVerbose("Blazor server URL: {BaseUrl}", baseUrl);

        await WaitForServerReady(baseUrl);

        // Transition splash → real window, or apply sizing when no splash was used
        var splashConfig = _options?.SplashScreen;
        if (splashConfig?.Enabled == true)
        {
            _logger?.LogVerbose("Server ready - transitioning from splash to main window");
            Title = _options.DefaultWindowTitle;
            Width = _options.DefaultWindowWidth;
            Height = _options.DefaultWindowHeight;
            MinWidth = Constants.Defaults.MinimumResizableWidth;
            MinHeight = Constants.Defaults.MinimumResizableHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            CanResize = _options.Resizable;
            ShowInTaskbar = true;
            Opacity = 1;
            // Restore full window chrome after the splash was shown borderless
            WindowDecorations = Avalonia.Controls.WindowDecorations.Full;
        }
        else
        {
            MinWidth = Constants.Defaults.MinimumResizableWidth;
            MinHeight = Constants.Defaults.MinimumResizableHeight;
            CanResize = _options.Resizable;
        }

        // Create and embed the NativeWebView
        _logger?.LogVerbose("Creating NativeWebView...");
        _nativeWebView = new NativeWebView
        {
            Source = new Uri(baseUrl)
        };

        Content = _nativeWebView;

        // Attach message handler for JavaScript <-> C# communication
        var messageHandler = CheapAvaloniaBlazorRuntime.GetRequiredService<WebViewMessageHandler>();
        messageHandler.AttachToWindow(_nativeWebView, this);

        // Attach cookie service
        if (CheapAvaloniaBlazorRuntime.GetRequiredService<ICookieService>() is CookieService cookieService)
            cookieService.AttachToWebView(_nativeWebView);

        // Wire lifecycle service to window property changes (Avalonia uses PropertyChanged, not a dedicated event)
        var lifecycleService = CheapAvaloniaBlazorRuntime.GetRequiredService<IAppLifecycleService>() as AppLifecycleService;
        if (lifecycleService is null)
        {
            _logger?.LogWarning("IAppLifecycleService is not AppLifecycleService - lifecycle events will not fire");
        }
        else
        {
            PropertyChanged += (_, args) =>
            {
                if (args.Property == WindowStateProperty)
                {
                    switch (WindowState)
                    {
                        case AvaloniaWindowState.Minimized:
                            lifecycleService.OnMinimized();
                            break;
                        case AvaloniaWindowState.Maximized:
                            lifecycleService.OnMaximized();
                            break;
                        case AvaloniaWindowState.Normal:
                            lifecycleService.OnRestored();
                            break;
                    }
                }
            };
            Activated += (_, _) => lifecycleService.OnActivated();
            Deactivated += (_, _) => lifecycleService.OnDeactivated();
        }

        // Initialize native menu bar with the platform handle once the visual tree is attached
        var menuBarService = CheapAvaloniaBlazorRuntime.GetRequiredService<IMenuBarService>() as MenuBarService;
        if (menuBarService is not null)
        {
            var platformHandle = TryGetPlatformHandle();
            if (platformHandle is not null)
                menuBarService.Initialize(platformHandle.Handle, _options?.MenuBarItems);
        }

        // Register this window with WindowService for multi-window support
        var windowService = CheapAvaloniaBlazorRuntime.GetRequiredService<IWindowService>() as WindowService;
        windowService?.RegisterMainWindow(this, _nativeWebView);

        _logger?.LogVerbose("NativeWebView created and all services wired up");
        Activate();
    }

    private async Task WaitForServerReady(string baseUrl)
    {
        using var httpClient = HttpClientFactory.CreateForServerCheck();

        for (int i = 0; i < Constants.Defaults.ServerReadinessMaxAttempts; i++)
        {
            try
            {
                _logger?.LogVerbose($"Checking server readiness... attempt {i + 1}");
                var response = await httpClient.GetAsync(baseUrl);
                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogVerbose("Server is ready!");
                    await Task.Delay(Constants.Defaults.ServerStabilizationDelayMilliseconds);
                    _logger?.LogVerbose("Server stabilization delay completed");
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogVerbose($"Server not ready yet: {ex.Message}");
            }

            await Task.Delay(Constants.Defaults.ServerReadinessCheckDelayMilliseconds);
        }

        _logger?.LogWarning("Warning: Server readiness check failed, proceeding anyway...");
    }

    /// <summary>
    /// Show the window as a dialog (explicit interface implementation to match nullable signature).
    /// </summary>
    /// <param name="owner">The owner window (nullable to match interface contract).</param>
    async Task IBlazorWindow.ShowDialog(Window? owner)
    {
        await base.ShowDialog(owner!);
    }

    /// <summary>
    /// Run the window and start the application (interface implementation).
    /// </summary>
    public void Run()
    {
        // The Avalonia window initialization triggers OnWindowLoaded,
        // which creates the NativeWebView and wires up all services.
        // This method satisfies the interface requirement.
    }
}
