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


def ativar(hwnd):
    """Traz a janela para a frente sem depender de quem está em foco.

    `SetForegroundWindow` sozinho é recusado pelo Windows quando quem chama não é o dono do foco;
    anexar à thread do foreground (o mesmo truque do `UiKit.ForceForeground` do app) libera a troca.
    """
    fg = user32.GetForegroundWindow()
    if fg == hwnd:
        return True

    tid_fg = user32.GetWindowThreadProcessId(fg, None)
    tid_alvo = user32.GetWindowThreadProcessId(hwnd, None)
    anexado = False
    if tid_fg and tid_alvo and tid_fg != tid_alvo:
        user32.AttachThreadInput(tid_alvo, tid_fg, True)
        anexado = True
    try:
        user32.ShowWindow(hwnd, 9)          # SW_RESTORE
        user32.SetForegroundWindow(hwnd)
        user32.BringWindowToTop(hwnd)
    finally:
        if anexado:
            user32.AttachThreadInput(tid_alvo, tid_fg, False)

    time.sleep(0.4)
    return user32.GetForegroundWindow() == hwnd


def alt_f4(janela):
    """Pede o fechamento pelo mesmo caminho do Alt+F4 — e SÓ na janela do app.

    A versão anterior enviava `SendKeys('%{F4}')` para a janela em foco, sem ativar o alvo: se o
    dono da máquina estivesse em outro programa (o editor, o chat do agente), quem fechava era ELE.
    Agora: ativa o alvo e, se o Windows não liberar o foco, pede o fechamento por mensagem
    (`WM_SYSCOMMAND`/`SC_CLOSE`) — mesmo caminho que o Alt+F4 percorre, sem teclado global.
    """
    if ativar(janela):
        subprocess.run(
            ["powershell", "-NoProfile", "-Command",
             "Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.SendKeys]::SendWait('%{F4}')"],
            timeout=40)
        return "Alt+F4"

    WM_SYSCOMMAND, SC_CLOSE = 0x0112, 0xF060
    user32.PostMessageW(janela, WM_SYSCOMMAND, SC_CLOSE, 0)
    return "SC_CLOSE"


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

        metodo = alt_f4(janela)
        time.sleep(3.0)
        sobreviveu = bool(find_main())

        subprocess.run([exe], timeout=25)      # mesma via do menu do icone
        time.sleep(10)                          # o app pode reiniciar o processo (~2 s) antes de abrir

        janela = find_main()
        depois = cores(janela) if janela else 0

        ok = depois >= LIMITE_BRANCO
        if not ok:
            falhas += 1

        print(f"ciclo {i}: inicial={antes} cores | {metodo} sobreviveu={sobreviveu} | "
              f"reaberta={depois} cores -> {'OK' if ok else 'FALHOU'}")

    proc.kill()
    print(f"resultado: {ciclos - falhas}/{ciclos} ciclos ok")
    return 0 if falhas == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
