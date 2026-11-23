using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace AnalistaHub.Services;

/// <summary>Aparência e posicionamento das janelas flutuantes (HUD e aviso).</summary>
public static class UiKit
{
    public static IntPtr Hwnd(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

    public static double Scale(Window window)
    {
        var dpi = Native.GetDpiForWindow(Hwnd(window));
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>Sem moldura, sempre no topo, fora do Alt+Tab, cantos arredondados e tema escuro.</summary>
    public static void MakeHudWindow(Window window, bool alwaysOnTop = true, bool hideFromAltTab = true)
    {
        try
        {
            var appWindow = window.AppWindow;
            var presenter = OverlappedPresenter.Create();
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = alwaysOnTop;
            appWindow.SetPresenter(presenter);
            if (hideFromAltTab) appWindow.IsShownInSwitchers = false;
            ApplyDarkChrome(window, round: true);
        }
        catch (Exception ex) { AppPaths.Log("MakeHudWindow: " + ex.Message); }
    }

    public static void ApplyDarkChrome(Window window, bool round)
    {
        try
        {
            var hwnd = Hwnd(window);
            var dark = 1;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            if (round)
            {
                var pref = Native.DWMWCP_ROUND;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
        }
        catch { }
    }

    public static void TryMica(Window window)
    {
        try { window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt }; }
        catch (Exception ex) { AppPaths.Log("mica indisponível: " + ex.Message); }
    }

    /// <summary>
    /// Força a janela a receber o foco. Sem isso o Windows mantém o foco no app anterior
    /// (trava de foreground) e o que o usuário digita cai no lugar errado.
    /// </summary>
    public static void ForceForeground(Window window)
    {
        try
        {
            var hwnd = Hwnd(window);
            var foreground = Native.GetForegroundWindow();
            if (foreground == hwnd) return;

            var foregroundThread = Native.GetWindowThreadProcessId(foreground, out _);
            var currentThread = Native.GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                           Native.AttachThreadInput(currentThread, foregroundThread, true);
            bool setOk;
            try
            {
                Native.BringWindowToTop(hwnd);
                setOk = Native.SetForegroundWindow(hwnd);
                Native.SetFocus(hwnd);
            }
            finally
            {
                if (attached) Native.AttachThreadInput(currentThread, foregroundThread, false);
            }

            var after = Native.GetForegroundWindow();
            if (after != hwnd)
            {
                // último recurso: força a janela para frente e para o topo
                Native.SwitchToThisWindow(hwnd, true);
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                after = Native.GetForegroundWindow();
            }

            AppPaths.Log($"ForceForeground: hud={hwnd} antes={foreground} setfg={setOk} attach={attached} depois={after} ok={after == hwnd}");
        }
        catch (Exception ex) { AppPaths.Log("ForceForeground: " + ex.Message); }
    }

    public static (int Left, int Top, int Right, int Bottom) WorkAreaAtCursor()
    {
        Native.GetCursorPos(out var pt);
        var monitor = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (Native.GetMonitorInfo(monitor, ref info))
            return (info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        return (0, 0, 1920, 1080);
    }

    /// <summary>HUD: centrada na horizontal, ~12 % do topo do monitor onde está o cursor.</summary>
    public static void PlaceHud(Window window, double widthDip, double heightDip)
    {
        var scale = Scale(window);
        var (left, top, right, bottom) = WorkAreaAtCursor();
        var w = (int)Math.Round(widthDip * scale);
        var h = (int)Math.Round(heightDip * scale);
        var x = left + Math.Max(0, (right - left - w) / 2);
        var y = top + (int)Math.Round((bottom - top) * 0.12);
        window.AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    public static void PlaceHud(Window window, double widthDip, double heightDip, int x, int y)
    {
        var scale = Scale(window);
        window.AppWindow.MoveAndResize(new RectInt32(x, y,
            (int)Math.Round(widthDip * scale), (int)Math.Round(heightDip * scale)));
    }

    /// <summary>Aviso: perto do cursor, preso à área útil do monitor.</summary>
    public static void PlaceNearCursor(Window window, double widthDip, double heightDip)
    {
        var scale = Scale(window);
        var (left, top, right, bottom) = WorkAreaAtCursor();
        Native.GetCursorPos(out var pt);

        var w = (int)Math.Round(widthDip * scale);
        var h = (int)Math.Round(heightDip * scale);
        var x = Math.Min(pt.X + 18, right - w - 8);
        var y = Math.Min(pt.Y + 18, bottom - h - 8);
        x = Math.Max(x, left + 8);
        y = Math.Max(y, top + 8);
        window.AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    public static void PlaceBottomRight(Window window, double widthDip, double heightDip)
    {
        var scale = Scale(window);
        var (left, top, right, bottom) = WorkAreaAtCursor();
        var w = (int)Math.Round(widthDip * scale);
        var h = (int)Math.Round(heightDip * scale);
        window.AppWindow.MoveAndResize(new RectInt32(right - w - 16, bottom - h - 16, w, h));
    }
}
