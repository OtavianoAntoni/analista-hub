using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace AnalistaHub.Services;

public sealed record PingSimPhase(string Fase, bool Ok, string Detalhe);

public sealed record PingSimResult(bool Ok, int DebounceMs, int Disparos, int Cancelados, List<PingSimPhase> Fases);

/// <summary>
/// Diagnóstico da subferramenta de ping sem tocar na tela e sem depender da rede: exercita o MESMO
/// <see cref="PingCoordinator"/> e a MESMA <see cref="PingSession"/> da HUD, com um emissor ICMP falso
/// que responde na hora (ou com atraso, para provar o cancelamento). É a evidência de
/// `--ping-sim`: debounce, cancelamento por edição e a trava de 100.
/// </summary>
public static class PingSimulation
{
    public static async Task<PingSimResult> RunAsync(int debounceMs)
    {
        var fases = new List<PingSimPhase>();
        var fake = new FakeIcmp();
        var session = new PingSession(1000, fake.Send);

        PingSample? last = null;
        using var signal = new SemaphoreSlim(0, int.MaxValue);

        void OnUpdate(PingSample? sample)
        {
            last = sample;
            if (sample is not null) signal.Release();
        }

        var sw = new Stopwatch();

        using (var coord = new PingCoordinator(session, debounceMs, OnUpdate))
        {
            // ---------------------------------------------------------------- 1. debounce
            // endereço pela metade não pinga: digitar não é erro
            coord.Edit("10.0.0.");
            var disparouIncompleto = await signal.WaitAsync(250);

            sw.Restart();
            coord.Edit("10.0.0.1");
            var chegou = await signal.WaitAsync(4000);
            var medido = sw.ElapsedMilliseconds;

            var debounceOk = !disparouIncompleto && chegou && last?.Address == "10.0.0.1" &&
                             Math.Abs(medido - debounceMs) <= 200;
            fases.Add(new PingSimPhase("debounce",
                debounceOk,
                $"esperado ~{debounceMs} ms depois da última tecla; medido {medido} ms; " +
                $"incompleto disparou={disparouIncompleto}; resposta={last?.Text ?? "nenhuma"}"));

            // ---------------------------------------------------------------- 2. cancelamento
            // primeiro eco demora 500 ms: a edição seguinte tem de cancelá-lo, sem deixar nada solto
            fake.DelayMs = 500;
            signal.Wait(0);                      // limpa sinal pendente
            coord.Edit("10.0.0.2");
            await Task.Delay(debounceMs + 120);  // aqui o eco já está em curso
            coord.Edit("10.0.0.3");
            var chegou3 = await signal.WaitAsync(4000);
            var cancelados = fake.Cancelled;

            var cancelOk = chegou3 && cancelados >= 1 && last?.Address == "10.0.0.3";
            fases.Add(new PingSimPhase("cancelamento",
                cancelOk,
                $"{cancelados} eco(s) cancelado(s) pela edição; resposta final={last?.Address} {last?.Text}"));
        }

        // ---------------------------------------------------------------- 3. trava de 100
        fake.DelayMs = 0;
        signal.Wait(0);
        var before = session.Used;
        using (var rapido = new PingCoordinator(session, 0, OnUpdate))
        {
            var disparos = 0;
            while (!session.Exhausted && disparos < PingSession.MaxProbes + 5)
            {
                rapido.Edit("10.0.0.9");
                if (!await signal.WaitAsync(4000)) break;
                disparos++;
            }

            last = null;
            rapido.Edit("10.0.0.9");             // já estourou: só avisa, não pinga
            await Task.Delay(250);
            var travaOk = session.Used == PingSession.MaxProbes &&
                          last?.Outcome == PingOutcome.BudgetExhausted &&
                          disparos + before == PingSession.MaxProbes;

            fases.Add(new PingSimPhase("limite",
                travaOk,
                $"disparos nesta fase={disparos} (+{before} das anteriores) = {session.Used}/" +
                $"{PingSession.MaxProbes}; mensagem da trava=\"{last?.Text}\""));
        }

        return new PingSimResult(
            Ok: fases.All(f => f.Ok),
            DebounceMs: debounceMs,
            Disparos: session.Used,
            Cancelados: fake.Cancelled,
            Fases: fases);
    }

    /// <summary>Emissor ICMP de mentira: sem rede, com atraso opcional e cancelável.</summary>
    private sealed class FakeIcmp
    {
        public int DelayMs;
        private int _cancelled;

        public int Cancelled => Volatile.Read(ref _cancelled);

        public async Task<IcmpReply> Send(IPAddress address, int timeoutMs, CancellationToken ct)
        {
            var delay = DelayMs;
            if (delay > 0)
            {
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _cancelled);
                    throw;
                }
            }
            ct.ThrowIfCancellationRequested();
            return new IcmpReply(IPStatus.Success, 3);
        }
    }
}
