using System.Runtime.InteropServices;
using System.Text;

namespace AnalistaHub.Services;

/// <summary>Clipboard via Win32: funciona sem STA e sem depender do dispatcher.</summary>
public static class ClipboardService
{
    public static string GetText()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (!Native.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(40);
                continue;
            }
            try
            {
                var handle = Native.GetClipboardData(Native.CF_UNICODETEXT);
                if (handle == IntPtr.Zero) return "";
                var ptr = Native.GlobalLock(handle);
                if (ptr == IntPtr.Zero) return "";
                try { return Marshal.PtrToStringUni(ptr)?.Trim() ?? ""; }
                finally { Native.GlobalUnlock(handle); }
            }
            finally { Native.CloseClipboard(); }
        }
        return "";
    }

    public static bool SetText(string text)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (!Native.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(40);
                continue;
            }
            try
            {
                Native.EmptyClipboard();
                var bytes = Encoding.Unicode.GetBytes((text ?? "") + "\0");
                var hMem = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)(uint)bytes.Length);
                if (hMem == IntPtr.Zero) return false;
                var ptr = Native.GlobalLock(hMem);
                if (ptr == IntPtr.Zero)
                {
                    Native.GlobalFree(hMem);
                    return false;
                }
                try { Marshal.Copy(bytes, 0, ptr, bytes.Length); }
                finally { Native.GlobalUnlock(hMem); }

                if (Native.SetClipboardData(Native.CF_UNICODETEXT, hMem) == IntPtr.Zero)
                {
                    Native.GlobalFree(hMem);
                    return false;
                }
                return true; // memória agora é do clipboard
            }
            finally { Native.CloseClipboard(); }
        }
        return false;
    }
}

/// <summary>Digita texto no campo que está em foco (equivalente ao que o Espanso faz).</summary>
public static class TypingService
{
    public static bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var ok = true;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                ok &= SendVirtualKey(Native.VK_RETURN);
                continue;
            }
            if (ch == '\r') continue;
            ok &= SendUnicode(ch);
        }
        return ok;
    }

    private static bool SendUnicode(char ch)
    {
        var inputs = new[]
        {
            new Native.INPUT { type = Native.INPUT_KEYBOARD, U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = Native.KEYEVENTF_UNICODE } } },
            new Native.INPUT { type = Native.INPUT_KEYBOARD, U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP } } }
        };
        return Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>()) == inputs.Length;
    }

    private static bool SendVirtualKey(ushort vk)
    {
        var inputs = new[]
        {
            new Native.INPUT { type = Native.INPUT_KEYBOARD, U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = 0 } } },
            new Native.INPUT { type = Native.INPUT_KEYBOARD, U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = Native.KEYEVENTF_KEYUP } } }
        };
        return Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>()) == inputs.Length;
    }
}
