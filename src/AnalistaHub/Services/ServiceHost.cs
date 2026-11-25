using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AnalistaHub.Services;

/// <summary>
/// Endereço de serviço digitado pelo usuário (MKT-002). Aceita IPv4, hostname ou "host:porta";
/// recusa o que não é endereço e compõe a URL de autenticação usada pela validação de credenciais.
/// </summary>
public static class ServiceHost
{
    private static readonly Regex NomeValido = new(
        @"^(?=.{1,253}$)[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$",
        RegexOptions.Compiled);

    /// <summary>Separa "host:porta"; sem porta (ou porta inválida) usa a do manifesto.</summary>
    public static (string Host, int Porta) Separar(string entrada, int portaPadrao)
    {
        var texto = (entrada ?? "").Trim();
        if (texto.Length == 0) return ("", portaPadrao);

        if (texto.Count(c => c == ':') == 1)
        {
            var idx = texto.IndexOf(':');
            var host = texto[..idx].Trim();
            if (int.TryParse(texto[(idx + 1)..], out var porta) && porta is > 0 and <= 65535)
                return (host, porta);
        }
        return (texto, portaPadrao);
    }

    /// <summary>Motivo da recusa, ou null quando o endereço é aceitável.</summary>
    public static string? Problema(string entrada, int portaPadrao)
    {
        var texto = (entrada ?? "").Trim();
        if (texto.Length == 0) return "Informe o endereço do servidor.";

        var (host, porta) = Separar(texto, portaPadrao);
        if (host.Length == 0) return "Informe o endereço do servidor.";
        if (porta is <= 0 or > 65535) return "Porta fora da faixa 1–65535.";

        // "999.999.999.999" passaria como nome de host; quando só há dígitos e pontos, exige IPv4 real.
        if (host.All(c => char.IsDigit(c) || c == '.'))
        {
            return IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
                ? null
                : "IPv4 inválido. Use quatro números de 0 a 255 (ex.: 10.0.0.1).";
        }

        if (!NomeValido.IsMatch(host))
            return "Host inválido. Use IP (10.0.0.1) ou nome (servidor.interno).";
        return null;
    }

    /// <summary>URL de autenticação do inventário, derivada do host+porta.</summary>
    public static string UrlAuth(string host, int porta) => $"http://{host}:{porta}/api/auth_ep";
}
