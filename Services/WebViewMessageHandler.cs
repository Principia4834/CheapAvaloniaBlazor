using Avalonia.Controls;
using CheapAvaloniaBlazor.Models;
using CheapAvaloniaBlazor.Utilities;
using Microsoft.Extensions.Logging;
using System.Text.Json;

using AvaloniaWindowState = Avalonia.Controls.WindowState;

namespace CheapAvaloniaBlazor.Services;

/// <summary>
/// Lightweight message handler for NativeWebView ↔ JavaScript communication.
/// Does NOT manage window creation/lifecycle — only handles messages.
/// </summary>
public class WebViewMessageHandler : IDisposable
{
    private NativeWebView? _webView;
    private Window? _window;
    private readonly Dictionary<string, Func<string, Task<string>>> _messageHandlers = new();
    private readonly ILogger<WebViewMessageHandler>? _logger;

    public WebViewMessageHandler(ILogger<WebViewMessageHandler>? logger = null)
    {
        _logger = logger;
        RegisterDefaultHandlers();
    }

    /// <summary>
    /// Attach to an existing NativeWebView and its host Window for message handling and window control.
    /// </summary>
    public void AttachToWindow(NativeWebView webView, Window window)
    {
        _webView = webView;
        _window = window;
        webView.WebMessageReceived += OnWebMessageReceived;
        _logger?.LogDebug("WebViewMessageHandler attached to NativeWebView");
    }

    /// <summary>
    /// Returns the host window currently attached to this handler, or <c>null</c> if not yet attached.
    /// </summary>
    public Window? GetHostWindow() => _window;

    /// <summary>
    /// Register a custom message handler.
    /// </summary>
    public void RegisterMessageHandler(string messageType, Func<string, Task<string>> handler)
    {
        _messageHandlers[messageType] = handler;
        _logger?.LogDebug("Registered message handler for: {MessageType}", messageType);
    }

    /// <summary>
    /// Send a message to JavaScript.
    /// </summary>
    public void SendMessage(string messageType, object? payload = null)
    {
        if (_webView == null) return;

        var message = JsonSerializer.Serialize(new { type = messageType, payload });

        // InvokeScript must run on the UI thread; InvokeAsync returns an awaitable task so
        // exceptions are propagated rather than becoming unobserved (A6).
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                await _webView.InvokeScript($"window.cheapBlazor?.onHostMessage({message})");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "SendMessage InvokeScript failed for type {MessageType}", messageType);
            }
        });
    }

    // Direct window control methods for C# callers (e.g., DesktopInteropService)

    /// <summary>
    /// Minimize the host window.
    /// </summary>
    public void MinimizeWindow()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_window != null) _window.WindowState = AvaloniaWindowState.Minimized;
        });
    }

    /// <summary>
    /// Maximize the host window.
    /// </summary>
    public void MaximizeWindow()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_window != null) _window.WindowState = AvaloniaWindowState.Maximized;
        });
    }

    /// <summary>
    /// Restore the host window to normal state.
    /// </summary>
    public void RestoreWindow()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_window != null) _window.WindowState = AvaloniaWindowState.Normal;
        });
    }

    /// <summary>
    /// Hide the host window completely (used for minimize-to-tray functionality).
    /// </summary>
    /// <returns>True if the window reference is available.</returns>
    public bool HideWindow()
    {
        if (_window == null) return false;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => _window.Hide());
        return true;
    }

    /// <summary>
    /// Show a previously hidden window and bring it to the foreground.
    /// </summary>
    /// <returns>True if the window reference is available.</returns>
    public bool ShowWindowFromHidden()
    {
        if (_window == null) return false;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _window.Show();
            _window.Activate();
            if (_window.WindowState == AvaloniaWindowState.Minimized)
                _window.WindowState = AvaloniaWindowState.Normal;
        });
        return true;
    }

    /// <summary>
    /// Set the host window title.
    /// </summary>
    public void SetWindowTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_window != null) _window.Title = title;
        });
    }

    /// <summary>
    /// Get the current window state as a string constant.
    /// </summary>
    public string GetWindowState()
    {
        var window = _window;
        if (window == null) return Constants.WindowStates.Normal;
        return window.WindowState switch
        {
            AvaloniaWindowState.Maximized => Constants.WindowStates.Maximized,
            AvaloniaWindowState.Minimized => Constants.WindowStates.Minimized,
            _ => Constants.WindowStates.Normal
        };
    }

    /// <summary>
    /// Execute JavaScript in the web view and return the result.
    /// </summary>
    public async Task<string> ExecuteScriptAsync(string script)
    {
        if (_webView == null)
            throw new InvalidOperationException("No web view attached");

        if (string.IsNullOrWhiteSpace(script))
            throw new ArgumentException("Script cannot be null or empty", nameof(script));

        // Basic security checks — reject dangerous patterns
        var lowerScript = script.ToLowerInvariant();
        foreach (var pattern in Constants.Security.DangerousScriptPatterns)
        {
            if (lowerScript.Contains(pattern.ToLowerInvariant()))
                throw new ArgumentException($"Script contains potentially dangerous pattern: {pattern}", nameof(script));
        }

        if (script.Length > Constants.Defaults.MaxScriptLength)
            throw new ArgumentException($"Script is too long (max {Constants.Defaults.MaxScriptLength} characters)", nameof(script));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Constants.Defaults.ScriptExecutionTimeoutSeconds));
        try
        {
            // Schedule InvokeScript on the UI thread; apply timeout to the resulting task.
            var scriptTask = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => _webView.InvokeScript(script));
            return await ((Task<string?>)scriptTask).WaitAsync(cts.Token) ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Script execution timed out after {Constants.Defaults.ScriptExecutionTimeoutSeconds} seconds");
        }
    }

    private void RegisterDefaultHandlers()
    {
        RegisterMessageHandler(Constants.MessageTypes.Minimize, async (_) =>
        {
            MinimizeWindow();
            return "ok";
        });

        RegisterMessageHandler(Constants.MessageTypes.Maximize, async (_) =>
        {
            MaximizeWindow();
            return "ok";
        });

        RegisterMessageHandler(Constants.MessageTypes.Restore, async (_) =>
        {
            RestoreWindow();
            return "ok";
        });

        RegisterMessageHandler(Constants.MessageTypes.ToggleMaximize, async (_) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_window != null)
                {
                    _window.WindowState = _window.WindowState == AvaloniaWindowState.Maximized
                        ? AvaloniaWindowState.Normal
                        : AvaloniaWindowState.Maximized;
                }
            });
            return "ok";
        });

        RegisterMessageHandler(Constants.MessageTypes.Close, async (_) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _window?.Close());
            return "ok";
        });

        RegisterMessageHandler(Constants.MessageTypes.SetTitle, async (payload) =>
        {
            if (!string.IsNullOrEmpty(payload))
                SetWindowTitle(payload);
            return "ok";
        });

        RegisterMessageHandler(Constants.MessageTypes.GetWindowState, async (_) =>
            GetWindowState());
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        var message = e.Body;
        try
        {
            _logger?.LogDebug("Received web message: {Message}", message);

            var messageData = JsonSerializer.Deserialize<MessageData>(message, MessageJsonOptions);
            if (messageData?.Type == null) return;

            // Handle one-time result handlers
            var resultKey = $"{Constants.MessageTypes.ResultPrefix}{messageData.Type}";
            if (_messageHandlers.ContainsKey(resultKey))
            {
                Task.Run(async () =>
                {
                    if (_messageHandlers.TryGetValue(resultKey, out var handler))
                    {
                        await handler(messageData.Payload ?? "");
                        _messageHandlers.Remove(resultKey);
                    }
                });
                return;
            }

            // Handle regular message handlers
            if (_messageHandlers.TryGetValue(messageData.Type, out var messageHandler))
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var result = await messageHandler(messageData.Payload ?? "");
                        if (!string.IsNullOrEmpty(result))
                        {
                            SendMessage($"{Constants.MessageTypes.ResponsePrefix}{messageData.Type}", result);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error handling message: {MessageType}", messageData.Type);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing web message: {Message}", message);
        }
    }

    public void Dispose()
    {
        if (_webView != null)
        {
            _webView.WebMessageReceived -= OnWebMessageReceived;
            _webView = null;
        }
        _window = null;
        _messageHandlers.Clear();
    }

    private static readonly JsonSerializerOptions MessageJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private class MessageData
    {
        public string? Type { get; set; }
        public string? Payload { get; set; }
    }
}
