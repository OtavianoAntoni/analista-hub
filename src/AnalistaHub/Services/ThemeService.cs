using AnalistaHub.Views;

namespace AnalistaHub.Services;

/// <summary>
/// Troca de tema em tempo de execução, sem reiniciar o aplicativo.
///
/// Só mudar <c>RequestedTheme</c> não basta: metade da interface é criada em código com a cor do
/// tema vigente no momento da criação (cartões, ícones de status, textos secundários). Por isso a
/// troca faz três coisas, nesta ordem, fora do evento que a disparou:
///
///   1. redefine o tema na raiz de cada janela viva;
///   2. redesenha a página atual (todos os elementos nascem com as cores novas);
///   3. recria as janelas flutuantes (busca, aviso e consulta preservando o termo digitado).
/// </summary>
public static class ThemeService
{
    public static void Apply()
    {
        var janela = App.MainWin;
        if (janela is null) return;

        // Fora do evento do controle que mudou o tema: reconstruir a página de dentro do
        // SelectionChanged do combo derrubava o próprio combo.
        janela.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                janela.ApplyThemeNow();
                AppPaths.Log($"tema: interface redesenhada ({App.Settings.Theme})");
            }
            catch (Exception ex) { AppPaths.Log("tema: falha ao redesenhara janela principal — " + ex.Message); }

            try { HudController.Rebuild(); } catch (Exception ex) { AppPaths.Log("tema: HUD — " + ex.Message); }
            try { ToastController.Rebuild(); } catch (Exception ex) { AppPaths.Log("tema: aviso — " + ex.Message); }
            try { SsePanelController.Rebuild(); } catch (Exception ex) { AppPaths.Log("tema: consulta — " + ex.Message); }
        });
    }
}
