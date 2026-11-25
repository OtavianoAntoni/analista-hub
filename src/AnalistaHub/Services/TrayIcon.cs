using System.Runtime.InteropServices;

namespace AnalistaHub.Services;

public enum TrayItemKind { Tool, Separator, Command }

public sealed class TrayMenuItem
{
    public string Text { get; init; } = "";
    public Action? Action { get; init; }
    public TrayItemKind Kind { get; init; } = TrayItemKind.Tool;

    public static TrayMenuItem Separator() => new() { Kind = TrayItemKind.Separator };
    public static TrayMenuItem Command(string text, Action action) => new() { Text = text, Action = action, Kind = TrayItemKind.Command };
    public static TrayMenuItem Tool(string text, Action action) => new() { Text = text, Action = action, Kind = TrayItemKind.Tool };
}

/// <summary>
/// Ícone na bandeja: janela oculta + Shell_NotifyIcon.
/// Clique esquerdo abre o HUD; clique direito abre o menu com as ferramentas, Configurações e Sair.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);
    private static readonly IntPtr IDI_APPLICATION = 32512;

    private readonly Native.SubclassProc _proc;
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _added;
    private int _ultimoClique;

    public event Action? LeftClick;
    public event Action? RightClick;

    /// <summary>Monta o menu do clique direito. Se não definido, o clique direito abre a janela.</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

    public TrayIcon(string tooltip)
    {
        _proc = WndProc;
        _hwnd = Native.CreateWindowEx(0, "STATIC", "AnalistaHubTray", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            AppPaths.Log("tray: CreateWindowEx falhou");
            return;
        }

        Native.SetWindowSubclass(_hwnd, _proc, (UIntPtr)2, IntPtr.Zero);
        _hIcon = LoadAppIcon();
        Add(tooltip);
    }

    private IntPtr LoadAppIcon()
    {
        try
        {
            if (File.Exists(AppPaths.IconFile))
            {
                var icon = Native.LoadImage(IntPtr.Zero, AppPaths.IconFile, Native.IMAGE_ICON, 16, 16, Native.LR_LOADFROMFILE);
                if (icon != IntPtr.Zero) return icon;
            }
        }
        catch { }
        var fallback = LoadIcon(IntPtr.Zero, IDI_APPLICATION);
        return fallback;
    }

    private void Add(string tooltip)
    {
        if (_hwnd == IntPtr.Zero) return;
        var data = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
            uCallbackMessage = Native.WM_TRAYICON,
            hIcon = _hIcon,
            szTip = tooltip.Length > 120 ? tooltip[..120] : tooltip,
            szInfo = "",
            szInfoTitle = ""
        };
        _added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
        if (!_added) AppPaths.Log("tray: NIM_ADD falhou");
    }

    public void SetTooltip(string tooltip)
    {
        if (_hwnd == IntPtr.Zero || !_added) return;
        var data = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Native.NIF_TIP,
            szTip = tooltip.Length > 120 ? tooltip[..120] : tooltip,
            szInfo = "",
            szInfoTitle = ""
        };
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
    {
        if (msg == Native.WM_TRAYICON)
        {
            // No modo clássico o lParam é a mensagem do mouse; em versões novas pode vir
            // empacotado, então ficamos só com o low word.
            var mouse = lParam.ToInt32() & 0xFFFF;
            AppPaths.Log("tray: mensagem 0x" + mouse.ToString("X"));

            switch (mouse)
            {
                case Native.WM_LBUTTONUP:
                case Native.WM_LBUTTONDBLCLK:
                    // CFG-004: a janela "STATIC" não tem CS_DBLCLKS, então um duplo clique chega
                    // como DOIS WM_LBUTTONUP. A guarda de 300 ms transforma os dois em uma ação —
                    // e a ação em si também é idempotente (App.OnTrayLeftClick).
                    var agora = Environment.TickCount;
                    if (agora - _ultimoClique < 300) return IntPtr.Zero;
                    _ultimoClique = agora;
                    try { LeftClick?.Invoke(); } catch (Exception ex) { AppPaths.Log("tray left: " + ex.Message); }
                    return IntPtr.Zero;

                case Native.WM_RBUTTONUP:
                case Native.WM_RBUTTONDOWN:
                case Native.WM_CONTEXTMENU:
                    try
                    {
                        if (MenuProvider is not null) ShowMenu();
                        else RightClick?.Invoke();
                    }
                    catch (Exception ex) { AppPaths.Log("tray right: " + ex); }
                    return IntPtr.Zero;
            }
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Menu nativo do Windows (CreatePopupMenu + TrackPopupMenuEx).
    /// O SetForegroundWindow antes e o PostMessage(WM_NULL) depois são obrigatórios: sem eles o
    /// menu fica na tela quando o usuário clica fora.
    /// </summary>
    private void ShowMenu()
    {
        var items = MenuProvider?.Invoke() ?? Array.Empty<TrayMenuItem>();

        var menu = Native.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        var actions = new Dictionary<int, Action>();
        var id = 1000;
        var firstTool = true;

        try
        {
            foreach (var item in items)
            {
                if (item.Kind == TrayItemKind.Separator)
                {
                    Native.AppendMenu(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }

                var flags = Native.MF_STRING;
                if (item.Kind == TrayItemKind.Tool && firstTool)
                {
                    flags |= Native.MF_DEFAULT;
                    firstTool = false;
                }

                Native.AppendMenu(menu, flags, (UIntPtr)(uint)id, item.Text);
                if (item.Action is not null) actions[id] = item.Action;
                id++;
            }

            Native.SetForegroundWindow(_hwnd);
            Native.GetCursorPos(out var pt);
            var chosen = Native.TrackPopupMenuEx(menu, Native.TPM_LEFTALIGN | Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD,
                pt.X, pt.Y, _hwnd, IntPtr.Zero);
            Native.PostMessage(_hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            AppPaths.Log($"tray: menu aberto ({items.Count} itens), escolhido={chosen}");

            if (chosen > 0 && actions.TryGetValue(chosen, out var action))
                action();
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_added)
        {
            var data = new Native.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1
            };
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
            _added = false;
        }
        Native.RemoveWindowSubclass(_hwnd, _proc, (UIntPtr)2);
        Native.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }
}
