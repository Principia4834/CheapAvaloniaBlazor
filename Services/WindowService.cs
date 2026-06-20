using System.Collections.Concurrent;
using System.Net;
using Avalonia.Controls;
using Avalonia.Threading;
using CheapAvaloniaBlazor.Models;
using Microsoft.Extensions.Logging;

namespace CheapAvaloniaBlazor.Services;

/// <summary>
/// Cross-platform orchestrator for child windows and modal dialogs.
/// Child windows are Avalonia Window instances that each host a NativeWebView
/// connecting to the shared Blazor server as an independent SignalR circuit.
/// </summary>
public sealed class WindowService : IWindowService
{
    private readonly IBlazorHostService _blazorHost;
    private readonly ILogger<WindowService> _logger;
    private readonly ConcurrentDictionary<string, WindowInfo> _windows = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ModalResult>> _modalCompletions = new();

    /// <summary>
    /// Whitelist of component types allowed in WindowHost.razor.
    /// Only types explicitly passed via <see cref="WindowOptions.ComponentType"/> are registered.
    /// Prevents arbitrary type instantiation from URL query parameters.
    /// Capped at <see cref="Constants.Window.MaxRegisteredComponentTypes"/> distinct types.
    /// </summary>
    private readonly ConcurrentDictionary<string, Type> _registeredComponents = new();

    /// <summary>
    /// Signaled by <see cref="RegisterMainWindow"/> when the main window is ready.
    /// Early <see cref="CreateWindowAsync"/> callers await this instead of hitting a null-check exception.
    /// </summary>
    private readonly TaskCompletionSource _mainWindowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Window? _mainWindow;
    private volatile bool _disposed;

    // Modal is always supported via Avalonia ShowDialog
    public bool IsModalSupported => true;

    public event Action<string>? WindowCreated;
    public event Action<string>? WindowClosed;
    public event Action<string, string, object?>? MessageReceived;

    public WindowService(IBlazorHostService blazorHost, ILogger<WindowService> logger)
    {
        _blazorHost = blazorHost;
        _logger = logger;
        _logger.LogDebug("WindowService initialized");
    }

    /// <summary>
    /// Called by BlazorHostWindow once the main window is fully set up.
    /// </summary>
    internal void RegisterMainWindow(Window mainWindow, NativeWebView mainWebView)
    {
        _mainWindow = mainWindow;
        _mainWindowReady.TrySetResult();
        _logger.LogInformation("Main window registered");
    }

    public Type? ResolveWindowComponent(string fullName)
    {
        _registeredComponents.TryGetValue(fullName, out var componentType);
        return componentType;
    }

    public Task<string> CreateWindowAsync(WindowOptions options, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowService));
        ValidateOptions(options);
        return CreateWindowCoreAsync(options, isModal: false, modalTcs: null, cancellationToken);
    }

    public async Task<ModalResult> CreateModalAsync(WindowOptions options, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowService));
        ValidateOptions(options);

        var tcs = new TaskCompletionSource<ModalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windowId = await CreateWindowCoreAsync(options, isModal: true, modalTcs: tcs, cancellationToken);

        using var ctsRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() =>
            {
                if (_modalCompletions.TryRemove(windowId, out var cancelledTcs))
                {
                    cancelledTcs.TrySetResult(ModalResult.Cancel());
                    if (_windows.TryGetValue(windowId, out var info))
                        CloseChildWindow(info);
                }
            })
            : default;

        return await tcs.Task;
    }

    public Task CloseWindowAsync(string windowId)
    {
        if (_disposed) return Task.CompletedTask;

        if (_windows.TryGetValue(windowId, out var windowInfo))
        {
            CloseChildWindow(windowInfo);
        }
        else
        {
            _logger.LogWarning("CloseWindowAsync: window '{WindowId}' not found", windowId);
        }

        return Task.CompletedTask;
    }

    /// <remarks>
    /// Concurrency contract: three paths compete for modal TCS ownership via TryRemove —
    /// CompleteModal, OnChildWindowClosed, and the CancellationToken callback.
    /// Exactly ONE path wins and executes the cleanup. Losers get false and no-op.
    /// </remarks>
    public void CompleteModal(string windowId, ModalResult result)
    {
        if (_disposed) return;

        if (!_modalCompletions.TryRemove(windowId, out var tcs))
        {
            _logger.LogWarning("CompleteModal: no modal completion found for window '{WindowId}'", windowId);
            return;
        }

        tcs.TrySetResult(result);

        if (_windows.TryGetValue(windowId, out var windowInfo))
            CloseChildWindow(windowInfo);

        _logger.LogDebug("Modal '{WindowId}' completed (Confirmed={Confirmed})", windowId, result.Confirmed);
    }

    public IReadOnlyList<string> GetWindows() => _windows.Keys.ToList().AsReadOnly();

    public void SendMessage(string windowId, string messageType, object? payload = null)
    {
        if (_disposed) return;

        Dispatcher.UIThread.Post(() =>
        {
            InvokeHandlersSafely(MessageReceived, handler =>
            {
                ((Action<string, string, object?>)handler)(windowId, messageType, payload);
            });
        });
    }

    public void BroadcastMessage(string messageType, object? payload = null) =>
        SendMessage("*", messageType, payload);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _mainWindowReady.TrySetResult();

        var windowCount = _windows.Count;
        _logger.LogDebug("WindowService disposing — closing {Count} child window(s)", windowCount);

        foreach (var kvp in _windows)
            CloseChildWindow(kvp.Value);

        foreach (var kvp in _modalCompletions)
            kvp.Value.TrySetResult(ModalResult.Cancel());

        _windows.Clear();
        _modalCompletions.Clear();
        _registeredComponents.Clear();

        _logger.LogDebug("WindowService disposed");
    }

    // ── Internal ────────────────────────────────────────────────────────────

    private async Task<string> CreateWindowCoreAsync(
        WindowOptions options,
        bool isModal,
        TaskCompletionSource<ModalResult>? modalTcs,
        CancellationToken cancellationToken)
    {
        var windowId = Guid.NewGuid().ToString("N")[..12];

        // Register component type whitelist
        if (options.ComponentType is not null)
        {
            var fullName = options.ComponentType.FullName!;
            if (!_registeredComponents.ContainsKey(fullName)
                && _registeredComponents.Count >= Constants.Window.MaxRegisteredComponentTypes)
            {
                _logger.LogWarning(
                    "Component type whitelist is full ({Max} types). Refusing to register '{TypeName}'.",
                    Constants.Window.MaxRegisteredComponentTypes, fullName);
                throw new InvalidOperationException(
                    $"Component type whitelist is full ({Constants.Window.MaxRegisteredComponentTypes} types). " +
                    $"Cannot register '{fullName}'.");
            }
            _registeredComponents.TryAdd(fullName, options.ComponentType);
            _logger.LogDebug("Registered component type '{TypeName}' for window hosting", fullName);
        }

        var windowUrl = BuildWindowUrl(options, windowId);
        var windowInfo = new WindowInfo { WindowId = windowId, IsModal = isModal, ParentWindowId = options.ParentWindowId };
        _windows[windowId] = windowInfo;

        if (modalTcs is not null)
            _modalCompletions[windowId] = modalTcs;

        // Wait for the main window if not yet available
        if (_mainWindow is null)
        {
            _logger.LogDebug("Main window not yet registered — waiting up to {Timeout}ms", Constants.Window.HandleReadyTimeoutMs);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Constants.Window.HandleReadyTimeoutMs);

            try
            {
                await _mainWindowReady.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                CleanupFailedWindow(windowId);
                throw new TimeoutException(
                    $"Main window was not registered within {Constants.Window.HandleReadyTimeoutMs}ms. " +
                    "Cannot create child windows before the main window is ready.");
            }
        }

        if (_mainWindow is null)
        {
            _logger.LogError("Cannot create child window — main window not registered");
            CleanupFailedWindow(windowId);
            throw new InvalidOperationException("Main window has not been registered. Cannot create child windows.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // All window creation and showing must happen on the UI thread.
        // InvokeAsync does not accept a CancellationToken; check before entering.
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var webView = new NativeWebView { Source = new Uri(windowUrl) };
            var childWindow = new Window
            {
                Title = options.Title ?? "Child Window",
                Width = options.Width,
                Height = options.Height,
                MinWidth = Constants.Defaults.MinimumResizableWidth,
                MinHeight = Constants.Defaults.MinimumResizableHeight,
                CanResize = options.Resizable,
                WindowStartupLocation = options.CenterOnParent
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                Content = webView
            };

            windowInfo.ChildWindow = childWindow;

            childWindow.Closed += (_, _) => OnChildWindowClosed(windowId);

            if (isModal)
            {
                // ShowDialog blocks until the window closes; launch it but don't await here
                // — the TCS will be resolved by CompleteModal or OnChildWindowClosed.
                _ = childWindow.ShowDialog(_mainWindow);
            }
            else
            {
                childWindow.Show();
            }
        });

        // Fire event on Avalonia UI thread
        Dispatcher.UIThread.Post(() =>
        {
            InvokeHandlersSafely(WindowCreated, handler => ((Action<string>)handler)(windowId));
        });

        _logger.LogInformation("Child window '{WindowId}' created (modal={IsModal}, url={Url})", windowId, isModal, windowUrl);
        return windowId;
    }

    private void OnChildWindowClosed(string windowId)
    {
        if (_modalCompletions.TryRemove(windowId, out var tcs))
            tcs.TrySetResult(ModalResult.Cancel());

        _windows.TryRemove(windowId, out _);

        if (!_disposed)
        {
            Dispatcher.UIThread.Post(() =>
            {
                InvokeHandlersSafely(WindowClosed, handler => ((Action<string>)handler)(windowId));
            });
        }

        _logger.LogInformation("Child window '{WindowId}' closed", windowId);
    }

    private static void CloseChildWindow(WindowInfo info)
    {
        if (info.ChildWindow is { } window)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { window.Close(); } catch { /* already closed */ }
            });
            info.ChildWindow = null;
        }
    }

    private void CleanupFailedWindow(string windowId)
    {
        _windows.TryRemove(windowId, out _);
        _modalCompletions.TryRemove(windowId, out _);
    }

    private string BuildWindowUrl(WindowOptions options, string windowId)
    {
        var baseUrl = _blazorHost.BaseUrl;

        if (options.ComponentType is not null)
        {
            var typeName = Uri.EscapeDataString(options.ComponentType.FullName!);
            return $"{baseUrl}{Constants.Window.WindowHostRoute}" +
                   $"?{Constants.Window.ComponentTypeQueryParam}={typeName}" +
                   $"&{Constants.Window.WindowIdQueryParam}={Uri.EscapeDataString(windowId)}";
        }

        if (!string.IsNullOrEmpty(options.UrlPath))
        {
            var path = options.UrlPath.StartsWith('/') ? options.UrlPath : "/" + options.UrlPath;
            var separator = path.Contains('?') ? "&" : "?";
            return $"{baseUrl}{path}{separator}{Constants.Window.WindowIdQueryParam}={Uri.EscapeDataString(windowId)}";
        }

        return $"{baseUrl}/?{Constants.Window.WindowIdQueryParam}={Uri.EscapeDataString(windowId)}";
    }

    /// <summary>
    /// Invokes each subscriber of a multicast delegate independently so one bad handler
    /// cannot crash the invocation chain for remaining subscribers.
    /// </summary>
    private void InvokeHandlersSafely(Delegate? multicastDelegate, Action<Delegate> invoker)
    {
        if (multicastDelegate is null) return;

        foreach (var handler in multicastDelegate.GetInvocationList())
        {
            try
            {
                invoker(handler);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Event handler {Handler} threw an exception", handler.Method.Name);
            }
        }
    }

    private static void ValidateOptions(WindowOptions options)
    {
        if (options.UrlPath is null && options.ComponentType is null)
            throw new ArgumentException("Either UrlPath or ComponentType must be set on WindowOptions.", nameof(options));

        if (options.UrlPath is not null && options.ComponentType is not null)
            throw new ArgumentException("UrlPath and ComponentType are mutually exclusive on WindowOptions.", nameof(options));
    }

    // ── Internal types ───────────────────────────────────────────────────────

    internal class WindowInfo
    {
        public required string WindowId { get; init; }
        public Window? ChildWindow { get; set; }
        public bool IsModal { get; set; }
        public string? ParentWindowId { get; set; }
    }
}
