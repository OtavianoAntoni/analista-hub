using System.Runtime.InteropServices;

namespace AnalistaHub.Services;

/// <summary>
/// Atalhos globais (RegisterHotKey) pendurados na janela principal.
///
/// Um id fixo é a busca rápida; as ferramentas usam ids alocados na faixa seguinte. Cada registro é
/// independente e transacional: se o Windows recusar uma combinação, as outras continuam valendo e o
/// erro diz qual combinação falhou (nunca derruba o conjunto).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    /// <summary>Id da busca rápida (paleta).</summary>
    private const int PaletteId = 0xA17;

    /// <summary>Primeiro id livre para atalhos de ferramenta.</summary>
    private const int ToolIdBase = 0xA18;

    private readonly IntPtr _hwnd;
    private readonly Native.SubclassProc _subclassProc; // manter referência: o GC não pode coletar o delegate

    /// <summary>id do RegisterHotKey → combinação registrada.</summary>
    private readonly Dictionary<int, string> _combos = new();

    /// <summary>id do RegisterHotKey → ferramenta.</summary>
    private readonly Dictionary<int, string> _tools = new();

    private bool _paletteRegistered;

    /// <summary>Busca rápida acionada.</summary>
    public event Action? Pressed;

    /// <summary>Atalho de uma ferramenta acionado (recebe o id da ferramenta).</summary>
    public event Action<string>? ToolPressed;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _subclassProc = WndProc;
        Native.SetWindowSubclass(_hwnd, _subclassProc, (UIntPtr)1, IntPtr.Zero);
    }

    public string CurrentCombo { get; private set; } = "";

    /// <summary>Combinações ativas: ferramenta → atalho (só o que o Windows aceitou).</summary>
    public IReadOnlyDictionary<string, string> RegisteredToolCombos =>
        _tools.ToDictionary(p => p.Value, p => _combos[p.Key], StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ busca rápida

    /// <summary>
    /// Troca o atalho da busca rápida. É TRANSACIONAL: se o Windows recusar o novo (outro programa já
    /// usa), o atalho anterior volta a valer na hora — o usuário nunca fica sem busca rápida.
    /// </summary>
    public bool Register(string combo, out string error)
    {
        error = "";

        if (!TryParse(combo, out var modifiers, out var vk))
        {
            error = $"Atalho inválido: \"{combo}\". Use algo como Ctrl+Alt+Space.";
            return false;   // nada registrado é tocado
        }

        var anterior = CurrentCombo;
        var tinhaAnterior = _paletteRegistered;
        Unregister();

        if (!Native.RegisterHotKey(_hwnd, PaletteId, modifiers, vk))
        {
            var code = Marshal.GetLastWin32Error();
            error = code == 1409
                ? $"Outro programa já usa {combo}."
                : $"O Windows não liberou {combo} (código {code}).";
            AppPaths.Log($"RegisterHotKey falhou: {combo} (erro {code})" +
                         (tinhaAnterior ? $" — restaurando {anterior}" : ""));

            if (tinhaAnterior && !string.IsNullOrWhiteSpace(anterior) &&
                TryParse(anterior, out var mods0, out var vk0) &&
                Native.RegisterHotKey(_hwnd, PaletteId, mods0, vk0))
            {
                _paletteRegistered = true;
                _combos[PaletteId] = anterior;
                CurrentCombo = anterior;
                error += $" O atalho {anterior} continua valendo.";
            }

            return false;
        }

        _paletteRegistered = true;
        _combos[PaletteId] = combo;
        CurrentCombo = combo;
        AppPaths.Log("atalho global registrado: " + combo);
        return true;
    }

    public void Unregister()
    {
        if (!_paletteRegistered) return;
        Native.UnregisterHotKey(_hwnd, PaletteId);
        _combos.Remove(PaletteId);
        _paletteRegistered = false;
    }

    // ------------------------------------------------------------------ atalhos por ferramenta

    /// <summary>
    /// Registra o atalho de uma ferramenta. Recusa (com motivo) quando a combinação já é da busca
    /// rápida, de outra ferramenta, ou quando o Windows não libera. Nunca afeta os outros atalhos.
    /// </summary>
    public bool RegisterTool(string toolId, string combo, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(combo)) { UnregisterTool(toolId); return true; }

        if (!TryParse(combo, out var modifiers, out var vk))
        {
            error = $"Atalho inválido: \"{combo}\".";
            return false;
        }

        var conflito = Conflito(combo, toolId);
        if (conflito is not null) { error = conflito; return false; }

        var id = IdForTool(toolId);
        UnregisterTool(toolId);

        if (!Native.RegisterHotKey(_hwnd, id, modifiers, vk))
        {
            var code = Marshal.GetLastWin32Error();
            error = code == 1409
                ? $"Outro programa já usa {combo}."
                : $"O Windows não liberou {combo} (código {code}).";
            AppPaths.Log($"atalho da ferramenta {toolId} recusado: {combo} (erro {code})");
            return false;
        }

        _combos[id] = combo;
        _tools[id] = toolId;
        AppPaths.Log($"atalho da ferramenta {toolId} registrado: {combo}");
        return true;
    }

    public void UnregisterTool(string toolId)
    {
        var id = IdForTool(toolId);
        if (_tools.Remove(id))
        {
            Native.UnregisterHotKey(_hwnd, id);
            _combos.Remove(id);
            AppPaths.Log($"atalho da ferramenta {toolId} liberado");
        }
    }

    /// <summary>
    /// Combinação já em uso? Devolve o motivo pronto para a tela, ou null se está livre. Compara
    /// contra a busca rápida e contra as outras ferramentas — a checagem é normalizada, então
    /// "ctrl+alt+j" e "Ctrl+Alt+J" contam como a mesma combinação.
    /// </summary>
    public string? Conflito(string combo, string? ignorarToolId = null)
    {
        var alvo = Normalizar(combo);
        if (alvo.Length == 0) return null;

        if (!string.IsNullOrWhiteSpace(CurrentCombo) && Normalizar(CurrentCombo) == alvo)
            return $"A combinação {combo} já é da busca rápida.";

        foreach (var par in _tools)
        {
            if (ignorarToolId is not null && par.Value.Equals(ignorarToolId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (_combos.TryGetValue(par.Key, out var outra) && Normalizar(outra) == alvo)
                return $"A combinação {combo} já é da ferramenta «{NomeDe(par.Value)}».";
        }
        return null;
    }

    private static string NomeDe(string toolId) =>
        App.Catalog?.Find(toolId)?.Name ?? toolId;

    private static string Normalizar(string combo) =>
        string.Join("+", combo.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().ToLowerInvariant())
            .OrderBy(p => p));

    /// <summary>Id estável por ferramenta: mesma ferramenta sempre no mesmo slot.</summary>
    private int IdForTool(string toolId)
    {
        foreach (var par in _tools)
            if (par.Value.Equals(toolId, StringComparison.OrdinalIgnoreCase)) return par.Key;

        // slot livre na faixa (1..200 ferramentas)
        for (var i = 0; i < 200; i++)
        {
            var candidato = ToolIdBase + i;
            if (!_tools.ContainsKey(candidato)) return candidato;
        }
        return ToolIdBase + 199;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
    {
        if (msg == Native.WM_HOTKEY)
        {
            var hotkeyId = wParam.ToInt32();

            if (hotkeyId == PaletteId)
            {
                AppPaths.Log("WM_HOTKEY recebido (busca rápida)");
                try { Pressed?.Invoke(); } catch (Exception ex) { AppPaths.Log("hotkey handler: " + ex); }
                return IntPtr.Zero;
            }

            if (_tools.TryGetValue(hotkeyId, out var toolId))
            {
                AppPaths.Log($"WM_HOTKEY recebido (ferramenta {toolId})");
                try { ToolPressed?.Invoke(toolId); } catch (Exception ex) { AppPaths.Log("hotkey da ferramenta: " + ex); }
                return IntPtr.Zero;
            }
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
        foreach (var id in _tools.Keys.ToList()) Native.UnregisterHotKey(_hwnd, id);
        _tools.Clear();
        _combos.Clear();

        try
        {
            Native.RemoveWindowSubclass(_hwnd, _subclassProc, (UIntPtr)1);
        }
        catch { }
    }
}
