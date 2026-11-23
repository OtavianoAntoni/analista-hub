"""Verifica a janela do Analista Hub: renderizada? fecha com Alt+F4? reabre sem ficar branca?

Uso:
    python build/test-janela-branca.py <exe> <ciclos>

Cada ciclo:
  1. mede as cores da janela (renderizada = centenas; branca = 1)
  2. manda Alt+F4 (o fechamento que o usuário tentaria) e verifica que a janela sobrevive
  3. reabre pelo mesmo caminho do menu do ícone (segunda instância) e mede de novo
"""
import ctypes
import subprocess
import sys
import time
from ctypes import wintypes

from PIL import ImageGrab

user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.SetProcessDPIAware()

MONITOR2 = (2000, 400)
LIMITE_BRANCO = 80


def find_main(_pid=None):
    """Procura a janela principal pelo título, sem depender do PID (o app pode reiniciar)."""
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        buf = ctypes.create_unicode_buffer(256)
        user32.GetWindowTextW(hwnd, buf, 256)
        if buf.value == "Analista Hub":
            found.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    return found[0] if found else None


def cores(hwnd):
    r = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    box = (r.left + 8, r.top + 40, r.right - 8, r.bottom - 8)
    if box[2] <= box[0] or box[3] <= box[1]:
        return 0
    img = ImageGrab.grab(bbox=box, all_screens=True).convert("RGB")
    return len(set(img.getdata()))


def alt_f4():
    subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         "Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.SendKeys]::SendWait('%{F4}')"],
        timeout=40)


def main():
    exe = sys.argv[1]
    ciclos = int(sys.argv[2]) if len(sys.argv) > 2 else 3

    user32.SetCursorPos(*MONITOR2)
    time.sleep(0.3)

    proc = subprocess.Popen([exe])
    time.sleep(8)

    janela = find_main(proc.pid)
    if not janela:
        print("ERRO: janela nao abriu")
        proc.kill()
        return 2

    falhas = 0
    for i in range(1, ciclos + 1):
        antes = cores(janela)

        alt_f4()
        time.sleep(3.0)
        sobreviveu = bool(find_main())

        subprocess.run([exe], timeout=25)      # mesma via do menu do icone
        time.sleep(10)                          # o app pode reiniciar o processo (~2 s) antes de abrir

        janela = find_main()
        depois = cores(janela) if janela else 0

        ok = depois >= LIMITE_BRANCO
        if not ok:
            falhas += 1

        print(f"ciclo {i}: inicial={antes} cores | Alt+F4 sobreviveu={sobreviveu} | "
              f"reaberta={depois} cores -> {'OK' if ok else 'FALHOU'}")

    proc.kill()
    print(f"resultado: {ciclos - falhas}/{ciclos} ciclos ok")
    return 0 if falhas == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
