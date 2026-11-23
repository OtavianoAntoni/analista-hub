namespace AnalistaHub.Services;

/// <summary>Atalho global (RegisterHotKey) pendurado na janela principal.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0xA17;

    private readonly IntPtr _hwnd;
    private readonly Native.SubclassProc _subclassProc; // manter referência: o GC não pode coletar o delegate
    private bool _registered;

    public event Action? Pressed;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _subclassProc = WndProc;
        Native.SetWindowSubclass(_hwnd, _subclassProc, (UIntPtr)1, IntPtr.Zero);
    }

    public string CurrentCombo { get; private set; } = "";

    public bool Register(string combo, out string error)
    {
        error = "";
        Unregister();

        if (!TryParse(combo, out var modifiers, out var vk))
        {
            error = $"Atalho inválido: \"{combo}\". Use algo como Ctrl+Alt+Space.";
            return false;
        }

        if (!Native.RegisterHotKey(_hwnd, HotkeyId, modifiers, vk))
        {
            error = $"O Windows não liberou o atalho {combo} (já está em uso por outro programa).";
            AppPaths.Log("RegisterHotKey falhou: " + combo);
            return false;
        }

        _registered = true;
        CurrentCombo = combo;
        return true;
    }

    public void Unregister()
    {
        if (!_registered) return;
        Native.UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            AppPaths.Log("WM_HOTKEY recebido");
            try { Pressed?.Invoke(); } catch (Exception ex) { AppPaths.Log("hotkey handler: " + ex); }
            return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public static bool TryParse(string combo, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;

        foreach (var rawPart in combo.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "win": case "windows": modifiers |= Native.MOD_WIN; break;
                default:
                    if (!TryParseKey(part, out vk)) return false;
                    break;
            }
        }
        return vk != 0;
    }

    private static bool TryParseKey(string key, out uint vk)
    {
        vk = 0;
        key = key.Trim();
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { vk = c; return true; }
            if (c == ' ') { vk = 0x20; return true; }
            return false;
        }

        if (key.StartsWith('F') && int.TryParse(key[1..], out var fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        vk = key.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "esc" or "escape" => 0x1B,
            "insert" => 0x2D,
            "delete" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "`" or "oemtilde" => 0xC0,
            "/" => 0xBF,
            ";" => 0xBA,
            _ => 0
        };
        return vk != 0;
    }

    public void Dispose()
    {
        Unregister();
        try
        {
            Native.RemoveWindowSubclass(_hwnd, _subclassProc, (UIntPtr)1);
        }
        catch { }
    }
}
