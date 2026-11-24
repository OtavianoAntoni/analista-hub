using AnalistaHub.Models;

namespace AnalistaHub.Services;

/// <summary>
/// Validação do valor de um campo declarado no manifesto. Uma regra só, usada pela HUD (o que o
/// analista digita) e pelo atalho da ferramenta (o que veio da área de transferência).
///
/// Foi a falta disso que produziu o NET-004: a HUD recusava o IP inválido, mas o atalho mandava o
/// valor direto para o script, que falhava por conta própria e mostrava uma segunda mensagem.
/// </summary>
public static class FieldValidation
{
    /// <summary>Devolve null quando o valor serve; caso contrário, a frase que o usuário lê.</summary>
    public static string? Check(ToolInput field, string value)
    {
        switch ((field.Type ?? "text").Trim().ToLowerInvariant())
        {
            case "ip":
            {
                // mesma política do ping (IpTarget): uma regra só para o campo de IP
                var state = IpTarget.Classify(value, out _, out var problem);
                return state switch
                {
                    IpTextState.Valid => null,
                    // o valor digitado aparece exatamente como veio — nada de normalização silenciosa.
                    // O detalhe do IpTarget repete o próprio valor, então aqui vai só a explicação.
                    IpTextState.InvalidOctet => $"IP inválido: {value} — esperado quatro números de 0 a 255.",
                    IpTextState.IPv6 => $"IP inválido: {value} — {IpTarget.IPv6Message}.",
                    _ => $"IP inválido: {value} — esperado um IPv4, ex.: 10.0.0.1."
                };
            }
            case "mac":
            {
                var hex = new string(value.Where(Uri.IsHexDigit).ToArray());
                return hex.Length >= 6
                    ? null
                    : $"«{field.Label}»: preciso de pelo menos 6 dígitos hex (ex.: 00:1A:2B).";
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Valor para exibir e registrar: campos declarados <c>sensitive</c> aparecem mascarados.
    /// A contagem de caracteres fica, para o analista conferir se colou o valor inteiro.
    /// </summary>
    public static string VisibleValue(ToolInput campo, string valor)
    {
        if (!campo.Sensitive || string.IsNullOrEmpty(valor)) return valor;
        return $"{new string('•', Math.Clamp(valor.Length, 3, 8))} ({valor.Length} caracteres)";
    }
}
