using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace DotaReports;

/// <summary>
/// WebView2 со своим постоянным профилем. Обычно скрыт и только читает страницы;
/// окно показывается, когда нужно войти в Steam.
/// </summary>
sealed class SteamBrowser
{
    readonly Window _window;
    readonly IntPtr _hwnd;
    CoreWebView2Controller? _controller;
    bool _exiting;

    public SteamBrowser()
    {
        _window = new Window
        {
            Title = "Dota Reports — вход в Steam",
            Width = 1000,
            Height = 760,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        _hwnd = new WindowInteropHelper(_window).EnsureHandle();
        _window.SizeChanged += (_, _) => UpdateBounds();
        _window.LocationChanged += (_, _) => _controller?.NotifyParentWindowPositionChanged();
        _window.Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            UserClosed = true;
            HideWindow();
        };
    }

    public IntPtr Handle => _hwnd;
    public bool UserClosed { get; private set; }
    public string Source => _controller?.CoreWebView2.Source ?? "";

    CoreWebView2 Web => _controller?.CoreWebView2
        ?? throw new InvalidOperationException("WebView2 ещё не готов");

    public async Task InitAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebView);
        _controller = await env.CreateCoreWebView2ControllerAsync(_hwnd);
        _controller.IsVisible = false;
        UpdateBounds();
    }

    /// <summary>Выполняет действие и ждёт окончания вызванной им навигации. false — навигации не было за timeout.</summary>
    public async Task<bool> RunAndWaitNavigationAsync(Func<Task> action, TimeSpan timeout)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult(e.IsSuccess);
        Web.NavigationCompleted += OnCompleted;
        try
        {
            await action();
            return await Task.WhenAny(done.Task, Task.Delay(timeout)) == done.Task;
        }
        finally
        {
            Web.NavigationCompleted -= OnCompleted;
        }
    }

    public Task<bool> NavigateAsync(string url, TimeSpan timeout) =>
        RunAndWaitNavigationAsync(() => { Web.Navigate(url); return Task.CompletedTask; }, timeout);

    public async Task<JsonElement> EvalAsync(string script)
    {
        var json = await Web.ExecuteScriptAsync(script);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public void ShowWindow()
    {
        UserClosed = false;
        _window.Show();
        _window.Activate();
        if (_controller != null) _controller.IsVisible = true;
        UpdateBounds();
    }

    public void HideWindow()
    {
        if (_controller != null) _controller.IsVisible = false;
        _window.Hide();
    }

    public void Close()
    {
        _exiting = true;
        _controller?.Close();
        _window.Close();
    }

    void UpdateBounds()
    {
        if (_controller == null) return;
        Native.GetClientRect(_hwnd, out var r);
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, Math.Max(r.Right, 1), Math.Max(r.Bottom, 1));
    }
}
