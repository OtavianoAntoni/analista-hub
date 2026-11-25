"""Matriz de redimensionamento da janela principal (UI-004) — captura por HWND, sem roubar foco.

Uso:
    python build/test-janela-resize.py <exe> [--out <pasta>] [--keep]

Para cada tamanho: SetWindowPos (SWP_NOACTIVATE), espera, PrintWindow (PW_RENDERFULLCONTENT),
mede cores distintas e o percentual de pixels quase-brancos (indício de área não pintada).
Também prova o tamanho mínimo: pede 400x300 e mostra o que o Windows entregou.
"""
import ctypes
import os
import subprocess
import sys
import time
from ctypes import wintypes

from PIL import Image

u = ctypes.WinDLL("user32", use_last_error=True)
g = ctypes.WinDLL("gdi32", use_last_error=True)
try:
    # PerMonitorV2: as unidades de SetWindowPos passam a ser as mesmas que o app usa.
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    u.SetProcessDPIAware()

SWP_NOSIZE, SWP_NOMOVE, SWP_NOZORDER, SWP_NOACTIVATE = 0x1, 0x2, 0x4, 0x10
TAMANHOS = [(1120, 760), (1280, 720), (1600, 900), (1920, 1080), (2560, 1440), (3840, 2160), (400, 300)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG), ("biHeight", wintypes.LONG),
                ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
                ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD),
                ("biClrImportant", wintypes.DWORD)]


def achar(titulo="Analista Hub"):
    achadas = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, _):
        b = ctypes.create_unicode_buffer(256)
        u.GetWindowTextW(hwnd, b, 256)
        if b.value == titulo and u.IsWindowVisible(hwnd):
            achadas.append(hwnd)
        return True

    u.EnumWindows(cb, 0)
    return achadas[0] if achadas else None


def capturar(hwnd):
    r = wintypes.RECT()
    u.GetClientRect(hwnd, ctypes.byref(r))
    w, h = r.right, r.bottom
    if w <= 0 or h <= 0:
        return None
    hdc = u.GetDC(hwnd)
    mem = g.CreateCompatibleDC(hdc)
    bmp = g.CreateCompatibleBitmap(hdc, w, h)
    g.SelectObject(mem, bmp)
    u.PrintWindow(hwnd, mem, 2)

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth, bi.biHeight = w, -h
    bi.biPlanes, bi.biBitCount, bi.biCompression = 1, 32, 0
    buf = ctypes.create_string_buffer(w * h * 4)
    g.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    img = Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).convert("RGB")

    g.DeleteObject(bmp)
    g.DeleteDC(mem)
    u.ReleaseDC(hwnd, hdc)
    return img


def metricas(img):
    px = list(img.getdata())
    cores = len(set(px))
    brancos = px.count((255, 255, 255)) * 100.0 / max(1, len(px))
    # bbox de tudo que NAO e branco puro: se ele para de crescer com a janela, o layout nao acompanhou
    from PIL import ImageChops
    bbox = ImageChops.difference(img, Image.new("RGB", img.size, (255, 255, 255))).getbbox()
    return cores, brancos, bbox


def main():
    exe = sys.argv[1]
    out = "evidence"
    if "--out" in sys.argv:
        out = sys.argv[sys.argv.index("--out") + 1]
    os.makedirs(out, exist_ok=True)

    proc = subprocess.Popen([exe], cwd=os.path.dirname(os.path.abspath(exe)))
    time.sleep(9)

    hwnd = achar()
    if not hwnd:
        print("ERRO: janela 'Analista Hub' nao apareceu")
        proc.kill()
        return 2

    r = wintypes.RECT()
    u.GetWindowRect(hwnd, ctypes.byref(r))
    print(f"hwnd={hwnd} inicial={r.right - r.left}x{r.bottom - r.top}")

    print(f"{'pedido':>12} | {'janela':>11} | {'cliente':>11} | {'cores':>6} | {'branco%':>7} | conteudo(bbox) | arquivo")
    for (w, h) in TAMANHOS:
        u.SetWindowPos(hwnd, 0, 20, 20, w, h, SWP_NOZORDER | SWP_NOACTIVATE)
        time.sleep(1.5)
        img = capturar(hwnd)
        if img is None:
            print(f"{w}x{h} -> sem area de cliente")
            continue
        cores, pct, bbox = metricas(img)
        if cores <= 3:                      # 1 captura pode pegar frame intermediario
            time.sleep(1.2)
            img = capturar(hwnd)
            cores, pct, bbox = metricas(img)
        wr = wintypes.RECT()
        u.GetWindowRect(hwnd, ctypes.byref(wr))
        cr = wintypes.RECT()
        u.GetClientRect(hwnd, ctypes.byref(cr))
        nome = os.path.join(out, f"resize-{w}x{h}.png")
        img.save(nome)
        tam = f"{bbox[2]-bbox[0]}x{bbox[3]-bbox[1]}" if bbox else "vazio"
        print(f"{w:>5}x{h:<6} | {wr.right-wr.left:>5}x{wr.bottom-wr.top:<5} | "
              f"{cr.right:>5}x{cr.bottom:<5} | {cores:>6} | {pct:>6.2f}% | {tam:>13} | {nome}")

    if "--keep" not in sys.argv:
        proc.kill()
    return 0


if __name__ == "__main__":
    sys.exit(main())
