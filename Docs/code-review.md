# CheapAvaloniaBlazor — Comprehensive Code Review

**Date:** 2025-07  
**Reviewer:** GitHub Copilot  
**Scope:** Library core (`CheapAvaloniaBlazor`), app template (`TemplateApp`), samples (`MinimalApp`, `DesktopFeatures`), tests (`CheapAvaloniaBlazor.Tests`)  
**Version reviewed:** 3.2.0 (net10.0)

---

## Executive Summary

The library is broadly functional and well-commented. Several real bugs and architectural issues exist across three areas: Avalonia API conformance, Blazor / ASP.NET Core best-practice conformance, and MudBlazor API conformance. Issues are classified **CRITICAL** (correctness bug or major security/reliability risk), **MAJOR** (design flaw, potential production problem), or **MINOR** (code quality, style, maintainability).

---

## 1. Avalonia API & Design Guidelines

### CRITICAL

#### A1 — `AvaloniaXamlLoader.Load(this)` called twice in `Hosting/App.axaml.cs`

`App.axaml.cs` defines two `Initialize` methods:

```csharp
public override void Initialize()          // called by Avalonia framework
{
	AvaloniaXamlLoader.Load(this);         // Load #1
}

public void Initialize(CheapAvaloniaBlazorOptions options, IServiceProvider sp)
{
	// ...
	AvaloniaXamlLoader.Load(this);         // Load #2  ← BUG
}
```

`HostBuilder.BuildAvaloniaApp()` calls the custom overload; the Avalonia framework separately calls the standard overload. Both paths call `AvaloniaXamlLoader.Load(this)`, loading and applying the AXAML resource dictionary twice. This causes duplicate style/resource registrations and may throw `KeyAlreadyExistsException` for resource keys or silently produce incorrect theming.

**Fix:** Remove the `AvaloniaXamlLoader.Load(this)` call from the custom `Initialize(options, sp)` overload. It only needs to be in `override void Initialize()`.

---

#### A2 — `UseWin32()` called unconditionally after `UsePlatformDetect()`

In `Hosting/HostBuilder.cs`:

```csharp
return AppBuilder.Configure(() => { ... })
	.UsePlatformDetect()
	.UseWin32()      // ← overrides PlatformDetect on ALL platforms
	.UseSkia()
	.LogToTrace();
```

`.UsePlatformDetect()` already selects the correct Win32 backend on Windows, and X11/macOS backends on those platforms. `.UseWin32()` after it unconditionally reinstates the Win32 backend, causing a crash on Linux and macOS.

**Fix:** Remove `.UseWin32()`, or guard it: `if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) ...`.

---

#### A3 — Static service locator `CheapAvaloniaBlazorRuntime` is a global-state anti-pattern

`CheapAvaloniaBlazorRuntime` is a static class with a single static `IServiceProvider?` field used as a service locator throughout `BlazorHostWindow`, `SystemTrayService`, `NotificationService`, etc.:

```csharp
var messageHandler = CheapAvaloniaBlazorRuntime.GetRequiredService<WebViewMessageHandler>();
```

Problems:
- Makes unit testing impossible (no way to inject mocks).
- The static state is the root cause of the dual-DI-container problem. Because the Avalonia side resolves services from `CheapAvaloniaBlazorRuntime`, and Blazor resolves from its own container, singletons exist twice unless manually bridged in `EmbeddedBlazorHostService.ConfigureServices()`.
- Not thread-safe: `Initialize()` has no guard against double-initialization.
- Violates the "no static mutable state" principle for a library.

**Fix (long-term):** Inject dependencies through constructors. `BlazorHostWindow` already receives `IBlazorHostService` in its constructor; extend this pattern to resolve `CheapAvaloniaBlazorOptions`, `IDiagnosticLoggerFactory`, `WebViewMessageHandler`, etc. from the constructor rather than from the static locator.

---

### MAJOR

#### A4 — `CheapAvaloniaBlazorApp` is dead code containing `async`/sync pitfalls

`Hosting/CheapAvaloniaBlazorApp.cs` defines a `CheapAvaloniaBlazorApp : Application` class that is never instantiated; `HostBuilder` always creates `AvaloniaApp` instead. Yet it contains:

```csharp
private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
{
	_blazorHost.StopAsync().Wait(TimeSpan.FromSeconds(5));  // sync-over-async
}
```

This code is unreachable today, but the `.Wait()` call would deadlock if this class were ever used. The dead class also duplicates fire-and-forget startup logic present in `AvaloniaApp`.

**Fix:** Delete `CheapAvaloniaBlazorApp.cs` entirely, or convert it to a documented alternative entry point with proper async shutdown.

---

#### A5 — `AvaloniaMainWindow` / `MainWindow.axaml.cs` is orphaned code

`Windows/MainWindow.axaml` defines `AvaloniaMainWindow`, and `MainWindow.axaml.cs` defines a full constructor that calls `StartBlazorHost()` — an `async void` method that hides all exceptions. Neither class is instantiated anywhere in the library or samples; all real work is done by `BlazorHostWindow`.

**Fix:** Either remove both files or document `AvaloniaMainWindow` as an alternative entry point in the API surface.

---

#### A6 — `Dispatcher.UIThread.Post()` with `async` lambda creates unobserved exceptions

In `WebViewMessageHandler.SendMessage()`:

```csharp
Dispatcher.UIThread.Post(async () =>
{
	try { await _webView.InvokeScript(...); }
	catch (Exception ex) { _logger?.LogWarning(...); }
});
```

`Dispatcher.UIThread.Post()` accepts `Action`, not `Func<Task>`. The `async` lambda compiles to `async void`, which means any exception not caught inside the lambda will be unobserved and escalate to the unhandled-exception handler. The `try/catch` inside the lambda mitigates this for the specific call shown, but the pattern is fragile — any future `await` outside the `try/catch` will be unobserved.

**Fix:** Use `Dispatcher.UIThread.InvokeAsync(async () => { ... })` when the lambda is async, which returns an awaitable task that propagates exceptions.

---

#### A7 — Misleading / stale comment in `App.axaml.cs`

```csharp
// (Window is invisible and off-screen, but StorageProvider will be initialized)
window.Show();
```

The `BlazorHostWindow` constructor calls `InitializeWindow()`, which sets `Width`, `Height`, `Opacity = 1`, `ShowInTaskbar = true`, and `WindowStartupLocation.CenterScreen`. The window is fully visible to the user. The "invisible and off-screen" comment refers to an older implementation that moved the window to `OffScreenPosition = -32000`. The comment is wrong and should be updated.

---

#### A8 — `BlazorHostWindow` declared `partial` without matching AXAML file

The class is `public partial class BlazorHostWindow : Window` with the comment "Marked as partial for future splash screen expansion with XAML code-behind." No matching `.axaml` file exists. The `partial` keyword is therefore misleading. If no AXAML is planned in the near term, remove `partial`.

---

### MINOR

#### A9 — Hardcoded x86_64 library paths in `PlatformHelper.CheckLinuxWebViewSupport()`

The webkit2gtk detection enumerates paths like `/usr/lib/x86_64-linux-gnu/libwebkit2gtk-4.0.so`. This will always return `false` on ARM64 Linux (Raspberry Pi, Apple Silicon). Use `ldconfig -p` via `Process.Start()` or check `/usr/lib/` generically for the library name.

---

## 2. Blazor / ASP.NET Core & Microsoft Design Guidelines

### CRITICAL

#### B1 — Sync-over-async in `EmbeddedBlazorHostService.Dispose()`

```csharp
public void Dispose()
{
	if (IsRunning)
	{
		StopAsync().GetAwaiter().GetResult();   // DEADLOCK RISK
	}
	_hostCts?.Dispose();
	_app?.DisposeAsync().GetAwaiter().GetResult(); // DEADLOCK RISK
}
```

Both calls block a thread waiting for an async operation. On the UI thread or inside an ASP.NET Core synchronization context this can deadlock. The `WebApplication` implements `IAsyncDisposable`; the service should too.

**Fix:** Implement `IAsyncDisposable` on `EmbeddedBlazorHostService` with `async ValueTask DisposeAsync()`. If `IDisposable` must be retained for DI container compatibility, implement both and log a warning in the sync `Dispose()` path.

---

#### B2 — `DesktopInteropService.cs` is in the global namespace

`Services/DesktopInteropService.cs` contains no `namespace` declaration:

```csharp
// No namespace!
public class DesktopInteropService : IDesktopInteropService
```

All other types are in `CheapAvaloniaBlazor.*` namespaces. This means the class is in the global namespace, breaking namespace conventions and causing unexpected resolution behavior for consumers who import `using CheapAvaloniaBlazor.Services;`.

**Fix:** Add `namespace CheapAvaloniaBlazor.Services;` at the top of the file.

---

#### B3 — `DetailedErrors = true` hardcoded unconditionally

In `EmbeddedBlazorHostService.ConfigureServices()`:

```csharp
.AddInteractiveServerComponents(circuitOptions =>
{
	circuitOptions.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(10);
	circuitOptions.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(2);
	circuitOptions.DetailedErrors = true;    // ← always true
});
```

`DetailedErrors = true` exposes full .NET exception stack traces in the browser. Even though this is a desktop app running on localhost, the Blazor circuit error format is the same as a web app. Stack traces should not be unconditionally shown.

**Fix:** Respect `_options.EnableDiagnostics`:
```csharp
circuitOptions.DetailedErrors = _options.EnableDiagnostics;
```

---

#### B4 — Always-on HTTP request logging middleware in `ConfigurePipeline`

```csharp
app.Use(async (context, next) =>
{
	_logger.LogInformation($"{Constants.Diagnostics.Prefix} HTTP {Method} {Path} ...");
	// ...
	_logger.LogInformation($"{Constants.Diagnostics.Prefix} Response {StatusCode} ...");
});
```

This middleware logs every HTTP request at `Information` level regardless of `_options.EnableDiagnostics`. In a Blazor Server app, SignalR generates dozens of requests per second. This will flood structured log sinks (Application Insights, Seq, etc.) in all configurations.

**Fix:** Guard with `if (_diagnosticLogger.DiagnosticsEnabled)` or move to `LogDebug`/`LogTrace`.

---

### MAJOR

#### B5 — Reflection-based `MapRazorComponents<TApp>` is fragile

`Utilities/BlazorComponentMapper.TryMapRazorComponents()` uses reflection to invoke `MapRazorComponents<T>()`, `AddAdditionalAssemblies()`, and `AddInteractiveServerRenderMode()`:

```csharp
var mapMethod = typeof(RazorComponentsEndpointRouteBuilderExtensions)
	.GetMethods(BindingFlags.Public | BindingFlags.Static)
	.FirstOrDefault(m => m.Name == MapRazorComponentsMethod && m.IsGenericMethod ...);
```

This will break silently on any ASP.NET Core update that renames, overloads, or moves these extension methods. The failure mode is a runtime error with a cryptic null-reference or method-not-found message, not a compile-time error.

The library cannot statically reference the consumer's `App` type, but it could instead expose a strongly-typed method that the consumer calls explicitly:
```csharp
// In the consumer's Program.cs
app.UseCheapBlazorDesktop<App>();
```
This is a **breaking change** but eliminates the reflection entirely.

---

#### B6 — `HostBuilder.ConfigureDefaultServices()` evaluates options before user configuration

```csharp
public HostBuilder()
{
	_services = new ServiceCollection();
	_options = new CheapAvaloniaBlazorOptions();
	ConfigureDefaultServices();   // ← reads _options.EnableConsoleLogging here
}

private void ConfigureDefaultServices()
{
	_services.AddLogging(logging =>
	{
		if (_options.EnableConsoleLogging)  // always false at construction time
			logging.AddConsole();
	});
	_services.AddCheapAvaloniaBlazor(_options);
}
```

`EnableConsoleLogging` is always `false` in `new CheapAvaloniaBlazorOptions()`. Calling `.EnableConsoleLogging()` on the builder afterward (e.g., in `DesktopFeatures`) updates `_options.EnableConsoleLogging = true`, but the `AddLogging()` lambda has already captured the original `false` value. The console logger is never added.

**Fix:** Defer `AddLogging()` until `RunApp()` / `BuildAvaloniaApp()` is called, by which point all fluent configuration has been applied.

---

#### B7 — `MapCheapBlazorTestEndpoints` generates malformed HTML

```csharp
var html = $@"
<p style=""color: green;"">✅ JS Bridge loaded successfully!</p>
<p style=""color: red;"">❌ JS Bridge failed to load</p>
";
```

In a verbatim string (`$@"..."`), `""` represents a single escaped double-quote character. The resulting HTML is:
```html
<p style="color: green;">...</p>
```
This is actually **correct** for a verbatim string. However, the inner JavaScript string uses:
```csharp
'<p style=\"\"color: green;\"\"\">✅ JS Bridge loaded successfully!</p>'
```
which renders as four double-quotes around the attribute value in the browser. This is indeed malformed HTML attribute syntax.

**Fix:** Use single quotes for HTML attribute values in the JavaScript template literals:
```csharp
'<p style="color: green;">✅</p>'
```

---

#### B8 — No null/empty guards on public API methods

Public `HostBuilder` fluent methods accept `string` without validation:

```csharp
public HostBuilder WithTitle(string title)      // no null check
public HostBuilder WithSize(int width, int height) // no range check
public HostBuilder WithSettingsAppName(string name) // no null check
```

Per the project's coding standards: *use `ArgumentNullException.ThrowIfNull(x)` for null checks; for strings use `string.IsNullOrWhiteSpace(x)`*.

**Fix:** Add guards at the top of each public method.

---

#### B9 — Fire-and-forget Blazor host startup in `HostBuilder.Build<T>()`

```csharp
_ = Task.Run(async () =>
{
	await blazorHost.SafeStartAsync<HostBuilder>(serviceProvider);
});
```

The discarded task means startup failures are only logged, never surfaced to the user. The window opens and shows a blank WebView with no indication that Blazor failed to start.

**Fix:** Either await the startup task (using `RunApp` already does this in the `StartWithClassicDesktopLifetime` path), or show a visual error in the window if the host fails to start.

---

#### B10 — Dual DI container architecture requires fragile manual bridging

`EmbeddedBlazorHostService.ConfigureServices()` manually copies every singleton from the Avalonia DI container into the Blazor container:

```csharp
var trayService = CheapAvaloniaBlazorRuntime.GetRequiredService<ISystemTrayService>();
services.AddSingleton(trayService);
// ... repeated for every singleton
```

If a new singleton is added to `ServiceCollectionExtensions.AddCheapAvaloniaBlazor()` but not to this bridge list, it works in Avalonia but is a different instance in Blazor (double tray icons, double settings, etc.). This has happened before (documented in the TODO).

This is a systemic architectural issue rooted in A3 (the static service locator). The proper fix is to share a single `IServiceProvider` between Avalonia and Blazor from the start, which requires redesigning the startup sequence.

---

#### B11 — Redundant server readiness polling creates up to 35-second delay

`EmbeddedBlazorHostService.StartAsync()` calls `WaitForStartupAsync()` (30 second timeout, 100ms intervals). After `StartAsync()` returns, `BlazorHostWindow.InitializeWebView()` calls `WaitForServerReady()` again (10 attempts × 500ms = 5 seconds). This is redundant — by the time `StartAsync()` returns, the server is confirmed ready.

**Fix:** Remove `WaitForServerReady()` from `BlazorHostWindow.InitializeWebView()`, relying on `StartAsync()` having already confirmed readiness.

---

#### B12 — `MudBlazor` is a mandatory hard dependency of the core library

`CheapAvaloniaBlazor.csproj`:

```xml
<PackageReference Include="MudBlazor" Version="9.5.0" />
```

The MudBlazor package (including its JS/CSS assets) is a transitive dependency for all consumers of `CheapAvaloniaBlazor`, even those that want to use a different UI library (Radzen, FluentUI, plain Bootstrap, etc.). At ~3MB of compiled assets, this imposes unnecessary overhead on non-MudBlazor users.

**Fix:** Move the `MudBlazor` package reference to the sample projects and `HostBuilderExtensions.AddMudBlazor()` extension only. Make MudBlazor strictly opt-in. The core library should have no UI component dependencies.

---

### MINOR

#### B13 — `IHttpClientFactory` should be used instead of `new HttpClient()`

`Utilities/HttpClientFactory.cs` creates raw `HttpClient` instances:

```csharp
return new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
```

For localhost health-check polling (short-lived, low frequency) this is acceptable, but the class name `HttpClientFactory` conflicts with the `System.Net.Http.IHttpClientFactory` framework concept and may confuse readers. Rename to `ServerCheckHttpClient` or inline the creation.

---

#### B14 — `DesktopInteropService.GetTopLevel()` returns only `MainWindow`

```csharp
private Window? GetTopLevel()
{
	if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		return desktop.MainWindow;
	return null;
}
```

For multi-window apps, file pickers opened from a child window will always use the main window as the owner, which may show the picker on the wrong monitor. Use `WindowService` to resolve the currently active window.

---

## 3. MudBlazor API & Design Guidelines

### CRITICAL

*(See also B12 above — MudBlazor as mandatory dependency.)*

---

### MAJOR

#### M1 — Google Fonts CDN loaded in desktop app `App.razor`

```html
<link href="https://fonts.googleapis.com/css?family=Roboto:300,400,500,700&display=swap" rel="stylesheet" />
```

This makes all three sample apps require an internet connection to render correctly (Roboto font fallback is system sans-serif). In offline desktop scenarios the app will render with degraded typography. MudBlazor bundles its own font fallbacks; the CDN link is not mandatory.

**Fix:** Remove the Google Fonts CDN link, or bundle the Roboto font as a static web asset in each sample project.

---

#### M2 — `DesktopFeatures/Pages/Index.razor` is 1,257 lines

A single Razor page with 1,257 lines violates the single-responsibility principle and makes navigation, testing, and maintenance difficult. The page covers at minimum: file dialogs, window controls, clipboard, system paths, system tray, notifications, hotkeys, menu bar, multi-window, drag-and-drop, cookies, and settings — each of which deserves its own component.

**Fix:** Extract each demo section into a dedicated `Components/Demos/FileDialogDemo.razor`, `WindowControlDemo.razor`, etc. and compose them in `Index.razor`.

---

#### M3 — `WindowHost.razor` uses raw inline style instead of MudBlazor components

```razor
<p style="color: red; padding: 1rem;">@_errorMessage</p>
```

This is visually inconsistent with the rest of the UI which uses MudBlazor. Since this component is part of the library (not just a sample), consumers will see the raw styled `<p>` tag.

**Fix:**
```razor
<MudAlert Severity="Severity.Error" Class="ma-4">@_errorMessage</MudAlert>
```

---

#### M4 — Dark-mode toggle button not visually disabled when following system theme

In `DesktopFeatures/Shared/MainLayout.razor`:

```razor
<MudIconButton Icon="@(... isDarkMode ? LightMode : DarkMode)"
			   OnClick="ToggleDarkMode" />   <!-- no Disabled attribute -->
```

`ToggleDarkMode()` silently returns when `_followSystem` is `true`, giving the user no visual feedback. MudBlazor's `Disabled` property is the correct mechanism.

**Fix:**
```razor
<MudIconButton ... OnClick="ToggleDarkMode" Disabled="@_followSystem" />
```

---

#### M5 — `MudBlazor.js` script placement may cause initialization race

In `App.razor` of all samples:

```html
<script src="_framework/blazor.web.js"></script>
<script src="_content/MudBlazor/MudBlazor.min.js"></script>
<script src="_content/CheapAvaloniaBlazor/cheap-blazor-interop.js"></script>
```

`blazor.web.js` initializes synchronously and may begin rendering Blazor components before `MudBlazor.min.js` is loaded, causing "MudBlazor not initialized" errors in the browser console on slow loads. Per MudBlazor documentation, `MudBlazor.min.js` should be included **before** `blazor.web.js`.

**Fix:** Reorder to:
```html
<script src="_content/MudBlazor/MudBlazor.min.js"></script>
<script src="_content/CheapAvaloniaBlazor/cheap-blazor-interop.js"></script>
<script src="_framework/blazor.web.js"></script>
```

---

### MINOR

#### M6 — `MinimalApp/Shared/MainLayout.razor` cannot support dark mode

```razor
<MudThemeProvider />
```

The `MudThemeProvider` is created without `@ref` or `@bind-IsDarkMode`, so dark mode cannot be toggled or detected. If the MinimalApp is intended as a "native app feel" template, document this limitation. If dark mode support is desired (even minimally), add the binding pattern from `DesktopFeatures`.

---

#### M7 — `ChildWindow.razor` inline style for scrollable message log

```razor
<MudPaper Style="max-height: 150px; overflow-y: auto;">
```

Inline styles should be avoided. Extract to a CSS class in a `ChildWindow.razor.css` CSS isolation file:
```css
.message-log { max-height: 150px; overflow-y: auto; }
```

---

#### M8 — `MudChip` used for informational labels in `ChildWindow.razor`

```razor
<MudChip T="string" Size="Size.Small">@(WindowId ?? "N/A")</MudChip>
```

`MudChip` is an interactive / removable component; for read-only labels `MudText` with `Typo.caption` or a `MudBadge` is semantically more appropriate.

---

## 4. Test Coverage Gaps

The existing xUnit tests cover:
- `BlazorFrameworkExtractor` version selection and staleness logic ✅
- NuGet packaging wiring (props imports, static web asset guards) ✅

**Not covered:**
- `EmbeddedBlazorHostService` startup/shutdown lifecycle
- `WindowService` multi-window orchestration (create, close, modal)
- `HotkeyService` registration / unregistration / deduplication
- `DragDropService` event dispatch
- `ThemeService` initial state and change propagation
- `GuardClauses` overloads
- `HostBuilder` fluent methods and option propagation
- `BlazorComponentMapper.DiscoverAppType()` with missing / ambiguous `App` type

---

## 5. Summary Table

| ID | Area | Severity | File(s) | Issue |
|----|------|----------|---------|-------|
| A1 | Avalonia | **Critical** | `Hosting/App.axaml.cs` | `AvaloniaXamlLoader.Load()` called twice |
| A2 | Avalonia | **Critical** | `Hosting/HostBuilder.cs` | `UseWin32()` after `UsePlatformDetect()` crashes non-Windows |
| A3 | Avalonia | **Critical** | `CheapBlazorAvaloniaRuntime.cs`, all services | Static service locator anti-pattern |
| A4 | Avalonia | **Major** | `Hosting/CheapAvaloniaBlazorApp.cs` | Dead code with `.Wait()` sync-over-async |
| A5 | Avalonia | **Major** | `Windows/MainWindow.axaml.cs` | Orphaned `AvaloniaMainWindow`, `async void` host start |
| A6 | Avalonia | **Major** | `Services/WebViewMessageHandler.cs` | `Post()` with `async` lambda — unobserved exceptions |
| A7 | Avalonia | **Major** | `Hosting/App.axaml.cs` | Stale "invisible and off-screen" comment |
| A8 | Avalonia | **Minor** | `Windows/BlazorHostWindow.cs` | `partial` with no AXAML counterpart |
| A9 | Avalonia | **Minor** | `PlatformHelper.cs` | Hardcoded x86_64 webkit paths, breaks ARM64 |
| B1 | Blazor | **Critical** | `Services/EmbeddedBlazorHostService.cs` | `Dispose()` uses sync-over-async, deadlock risk |
| B2 | Blazor | **Critical** | `Services/DesktopInteropService.cs` | Missing `namespace` — class in global namespace |
| B3 | Blazor | **Critical** | `Services/EmbeddedBlazorHostService.cs` | `DetailedErrors = true` hardcoded |
| B4 | Blazor | **Critical** | `Services/EmbeddedBlazorHostService.cs` | Always-on HTTP request logging floods log sinks |
| B5 | Blazor | **Major** | `Utilities/BlazorComponentMapper.cs` | Reflection-based `MapRazorComponents<TApp>` is fragile |
| B6 | Blazor | **Major** | `Hosting/HostBuilder.cs` | Console logger option read before user configures it |
| B7 | Blazor | **Major** | `Extensions/WebApplicationExtensions.cs` | Test endpoint generates malformed HTML |
| B8 | Blazor | **Major** | `Hosting/HostBuilder.cs`, all public methods | Missing null/empty guards on public API |
| B9 | Blazor | **Major** | `Hosting/HostBuilder.cs` | Fire-and-forget startup, failures not surfaced |
| B10 | Blazor | **Major** | `Services/EmbeddedBlazorHostService.cs` | Dual-DI bridge requires manual update for every new singleton |
| B11 | Blazor | **Major** | `Services/EmbeddedBlazorHostService.cs`, `Windows/BlazorHostWindow.cs` | Double server-readiness polling |
| B12 | Blazor | **Major** | `CheapAvaloniaBlazor.csproj` | MudBlazor mandatory hard dependency in core library |
| B13 | Blazor | **Minor** | `Utilities/HttpClientFactory.cs` | Misleading name vs `IHttpClientFactory` |
| B14 | Blazor | **Minor** | `Services/DesktopInteropService.cs` | File picker always uses main window as owner |
| M1 | MudBlazor | **Major** | `samples/*/App.razor` | Google Fonts CDN — breaks offline/no-internet scenarios |
| M2 | MudBlazor | **Major** | `samples/DesktopFeatures/Pages/Index.razor` | 1,257-line page — needs splitting |
| M3 | MudBlazor | **Major** | `Components/WindowHost.razor` | Inline style `<p>` instead of `<MudAlert>` |
| M4 | MudBlazor | **Major** | `samples/DesktopFeatures/Shared/MainLayout.razor` | Toggle button missing `Disabled="@_followSystem"` |
| M5 | MudBlazor | **Major** | `samples/*/App.razor` | `MudBlazor.min.js` loaded after `blazor.web.js` |
| M6 | MudBlazor | **Minor** | `samples/MinimalApp/Shared/MainLayout.razor` | `MudThemeProvider` without binding — no dark mode |
| M7 | MudBlazor | **Minor** | `samples/DesktopFeatures/Pages/ChildWindow.razor` | Inline scroll style should use CSS isolation |
| M8 | MudBlazor | **Minor** | `samples/DesktopFeatures/Pages/ChildWindow.razor` | `MudChip` used for read-only label |

---

## 6. Prioritised Action Plan

### Immediate (correctness / crash bugs)

1. **Fix A2** — Remove `.UseWin32()` after `.UsePlatformDetect()` (one-line fix, prevents Linux/macOS crash).
2. **Fix A1** — Remove duplicate `AvaloniaXamlLoader.Load(this)` from custom `Initialize()` overload.
3. **Fix B2** — Add `namespace CheapAvaloniaBlazor.Services;` to `DesktopInteropService.cs`.
4. **Fix B1** — Implement `IAsyncDisposable` on `EmbeddedBlazorHostService`; remove `.GetAwaiter().GetResult()`.
5. **Fix B3** — Gate `DetailedErrors` on `_options.EnableDiagnostics`.
6. **Fix B4** — Gate the HTTP request logging middleware on `_diagnosticLogger.DiagnosticsEnabled`.

### Short-term (design / reliability)

7. **Fix B6** — Defer `AddLogging()` in `HostBuilder` until `RunApp()`/`BuildAvaloniaApp()`.
8. **Fix B8** — Add `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrWhiteSpace` guards to public `HostBuilder` methods.
9. **Fix M5** — Reorder scripts in `App.razor` to load `MudBlazor.min.js` before `blazor.web.js`.
10. **Fix M4** — Add `Disabled="@_followSystem"` to dark-mode toggle button.
11. **Fix A4 / A5** — Delete or document `CheapAvaloniaBlazorApp` and `AvaloniaMainWindow`.
12. **Fix B11** — Remove redundant `WaitForServerReady()` from `BlazorHostWindow`.

### Medium-term (architecture)

13. **Address B12** — Move `MudBlazor` package reference out of core library into opt-in extension.
14. **Address A3 / B10** — Design a shared `IServiceProvider` startup to eliminate the static service locator and dual-DI bridge.
15. **Address B5** — Replace reflection-based `MapRazorComponents` with a consumer-supplied type parameter.
16. **Address M2** — Split `DesktopFeatures/Pages/Index.razor` into per-feature sub-components.
17. **Expand test coverage** — Add tests for `HostBuilder` option propagation, `WindowService`, `HotkeyService`, `ThemeService`.
