using System.Net;
using System.Net.NetworkInformation;

namespace AnalistaHub.Services;

/// <summary>Como o texto digitado no campo se apresenta para o ping.</summary>
public enum IpTextState
{
    /// <summary>Campo vazio/vazio de espaço.</summary>
    Empty,
    /// <summary>Ainda sendo digitado (menos de 4 octetos, ou octeto aberto): não é erro.</summary>
    Incomplete,
    /// <summary>IPv6: fora do escopo desta versão (a mensagem explica).</summary>
    IPv6,
    /// <summary>Quatro octetos, mas algum inválido (ex.: 10.0.0.300).</summary>
    InvalidOctet,
    Valid
}

/// <summary>
/// Política única de endereço do campo `type: "ip"` — usada pela validação de execução da HUD
/// (`CommandHud.ValidateField`) e pelo ping. Antes cada uma tinha a sua cópia da regra.
/// </summary>
public static class IpTarget
{
    public const string IPv6Message = "IPv6 ainda fora do escopo do ping do HUB";

    /// <summary>Classifica o texto e, quando válido, devolve o endereço (CIDR é descartado).</summary>
    public static IpTextState Classify(string? raw, out IPAddress? address, out string? problem)
    {
        address = null;
        problem = null;

        var text = (raw ?? "").Trim();
        if (text.Length == 0) return IpTextState.Empty;

        // IPv6 tem dois-pontos; o campo do HUB é IPv4 (ver docs/SUBFERRAMENTA-PING.md §2)
        if (text.Contains(':'))
        {
            problem = IPv6Message;
            return IpTextState.IPv6;
        }

        // máscara CIDR: o ping vai para o endereço, não para a rede
        var slash = text.IndexOf('/');
        if (slash >= 0) text = text[..slash].Trim();

        var parts = text.Split('.');
        if (parts.Length != 4) return IpTextState.Incomplete;

        var bytes = new byte[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) return IpTextState.Incomplete;   // "10.0.0." enquanto digita
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var octet))
            {
                problem = $"octeto inválido em «{text}»";
                return IpTextState.InvalidOctet;
            }
            bytes[i] = octet;
        }

        address = new IPAddress(bytes);
        return IpTextState.Valid;
    }

    public static bool TryParse(string? raw, out IPAddress? address, out string? problem) =>
        Classify(raw, out address, out problem) == IpTextState.Valid;
}

public enum PingOutcome { Ok, Timeout, Unreachable, Error, BudgetExhausted }

/// <summary>Um retorno ICMP — a "1 linha" que a HUD mostra e o que o aviso repete.</summary>
public sealed record PingSample(DateTime When, string Address, PingOutcome Outcome, long? RoundtripMs, string Text)
{
    public bool Reachable => Outcome == PingOutcome.Ok;
}

/// <summary>Resposta crua de um eco ICMP (o padrão vem da pilha do Windows).</summary>
public readonly record struct IcmpReply(IPStatus Status, long RoundtripMs);

/// <summary>
/// Sessão de ping de UMA invocação de ferramenta: conta os disparos (trava de 100), guarda os
/// últimos retornos e mede cada pacote com timeout próprio.
///
/// Não abre processo nenhum: usa a API de ICMP do Windows (`System.Net.NetworkInformation.Ping`),
/// então cancelar é só cancelar o token — não existe `ping.exe` órfão para limpar.
/// </summary>
public sealed class PingSession
{
    /// <summary>Trava de segurança por invocação (reset a cada abertura da ferramenta).</summary>
    public const int MaxProbes = 100;

    public const string BudgetShort = "limite de 100 pings";
    public const string BudgetMessage = "Limite de 100 pings nesta consulta — reabra a ferramenta para reiniciar.";

    private readonly object _lock = new();
    private readonly List<PingSample> _recent = new();
    private readonly int _timeoutMs;
    private readonly Func<IPAddress, int, CancellationToken, Task<IcmpReply>> _send;
    private int _probes;

    public PingSession(int timeoutMs, Func<IPAddress, int, CancellationToken, Task<IcmpReply>>? sender = null)
    {
        _timeoutMs = Math.Clamp(timeoutMs, 200, 5000);
        _send = sender ?? SendViaWindows;
    }

    public int TimeoutMs => _timeoutMs;

    public int Used { get { lock (_lock) return _probes; } }

    public int Remaining => Math.Max(0, MaxProbes - Used);

    public bool Exhausted => Used >= MaxProbes;

    public IReadOnlyList<PingSample> Recent { get { lock (_lock) return _recent.ToList(); } }

    /// <summary>
    /// Um disparo. Devolve sempre uma amostra (inclusive falha); a única exceção é o cancelamento,
    /// que sobe como <see cref="OperationCanceledException"/> para quem coordena descartar.
    /// </summary>
    public async Task<PingSample> ProbeAsync(string? text, CancellationToken ct = default)
    {
        if (!IpTarget.TryParse(text, out var address, out var problem) || address is null)
            return Record(new PingSample(DateTime.Now, (text ?? "").Trim(), PingOutcome.Error, null,
                problem ?? "endereço incompleto"));

        if (Exhausted) return BudgetSample(address.ToString());

        lock (_lock) _probes++;

        try
        {
            var reply = await _send(address, _timeoutMs, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var text2 = reply.Status switch
            {
                IPStatus.Success => $"{reply.RoundtripMs} ms",
                IPStatus.TimedOut => "sem resposta",
                IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable
                    or IPStatus.DestinationPortUnreachable or IPStatus.DestinationProhibited => "destino inalcançável",
                _ => "sem resposta"
            };

            var outcome = reply.Status switch
            {
                IPStatus.Success => PingOutcome.Ok,
                IPStatus.TimedOut => PingOutcome.Timeout,
                _ => PingOutcome.Unreachable
            };

            return Record(new PingSample(DateTime.Now, address.ToString(), outcome,
                outcome == PingOutcome.Ok ? reply.RoundtripMs : null, text2));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Record(new PingSample(DateTime.Now, address.ToString(), PingOutcome.Error, null,
                "ping indisponível: " + ex.Message));
        }
    }

    /// <summary>
    /// Os últimos retornos do endereço consultado, para a tela de resultado ("3 ms · 4 ms").
    /// Endereço null/vazio devolve todos (o campo pode ter vindo do clipboard, sem ping).
    /// </summary>
    public string Summary(string? address, int take = 2)
    {
        List<PingSample> seq;
        lock (_lock) seq = _recent.ToList();

        if (IpTarget.TryParse(address, out var ip, out _) && ip is not null)
        {
            var key = ip.ToString();
            seq = seq.Where(s => s.Address == key).ToList();
        }

        return string.Join(" · ", seq.TakeLast(Math.Max(1, take)).Select(s => s.Text));
    }

    public static PingSample BudgetSample(string address) =>
        new(DateTime.Now, address, PingOutcome.BudgetExhausted, null, BudgetShort);

    private PingSample Record(PingSample sample)
    {
        lock (_lock)
        {
            _recent.Add(sample);
            if (_recent.Count > 8) _recent.RemoveRange(0, _recent.Count - 8);
        }
        AppPaths.Log($"ping {sample.Address}: {sample.Text} ({sample.Outcome}) [{Used}/{MaxProbes}]");
        return sample;
    }

    private static async Task<IcmpReply> SendViaWindows(IPAddress address, int timeoutMs, CancellationToken ct)
    {
        using var ping = new Ping();
        // sobrecarga com CancellationToken usa TimeSpan: é ela que permite cancelar de verdade
        var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMs),
            new byte[32], null, ct).ConfigureAwait(false);
        return new IcmpReply(reply.Status, reply.RoundtripTime);
    }
}

/// <summary>
/// Liga o campo de texto ao ping: debounce, cancelamento a cada edição e parada na trava.
/// Sem UI e sem I/O próprio — recebe a sessão e devolve cada retorno por callback, o que permite
/// provar o comportamento pela linha de comando (`--ping-sim`) além da tela.
/// </summary>
public sealed class PingCoordinator : IDisposable
{
    private readonly PingSession _session;
    private readonly int _debounceMs;
    private readonly Action<PingSample?> _onUpdate;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public PingCoordinator(PingSession session, int debounceMs, Action<PingSample?> onUpdate)
    {
        _session = session;
        _debounceMs = Math.Clamp(debounceMs, 0, 5000);
        _onUpdate = onUpdate;
    }

    public int DebounceMs => _debounceMs;

    /// <summary>
    /// Chamado a cada tecla. Cancela o que estiver em curso (debounce ou eco) e reagenda quando o
    /// texto já é um IPv4 completo. Texto incompleto apenas limpa a linha — digitar não é erro.
    /// </summary>
    public void Edit(string? text)
    {
        if (_disposed) return;
        Cancel();

        var state = IpTarget.Classify(text, out var address, out var problem);
        switch (state)
        {
            case IpTextState.Valid:
                if (_session.Exhausted)
                {
                    _onUpdate(PingSession.BudgetSample(address!.ToString()));
                    return;
                }
                var cts = new CancellationTokenSource();
                _cts = cts;
                _ = RunAsync(address!.ToString(), cts);
                return;

            case IpTextState.IPv6:
                // única mensagem de "não faço" que vale mostrar enquanto digita
                _onUpdate(new PingSample(DateTime.Now, (text ?? "").Trim(), PingOutcome.Error, null,
                    problem ?? IpTarget.IPv6Message));
                return;

            default:
                _onUpdate(null);
                return;
        }
    }

    /// <summary>Cancela debounce e eco em curso. Nenhum processo é encerrado: não existe processo.</summary>
    public void Cancel()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        try { cts.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }

    private async Task RunAsync(string address, CancellationTokenSource cts)
    {
        try
        {
            if (_debounceMs > 0) await Task.Delay(_debounceMs, cts.Token).ConfigureAwait(false);
            var sample = await _session.ProbeAsync(address, cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested || _disposed) return;
            _onUpdate(sample);
        }
        catch (OperationCanceledException) { /* edição cancelou: silêncio é o comportamento */ }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested || _disposed) return;
            _onUpdate(new PingSample(DateTime.Now, address, PingOutcome.Error, null, "ping indisponível: " + ex.Message));
        }
    }
}
