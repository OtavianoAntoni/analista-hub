namespace AnalistaHub.Services;

/// <summary>
/// Lê o PATCHLOG.md que acompanha o aplicativo (vai dentro do pacote). Serve para o usuário ver
/// "o que mudou" sem abrir o repositório: tanto na versão que está instalada quanto nas notas que
/// vêm no canal de atualização.
/// </summary>
public static class PatchNotes
{
    private static string Arquivo => Path.Combine(AppContext.BaseDirectory, "PATCHLOG.md");

    /// <summary>Seção da versão pedida ("2.1" casa com cabeçalho "## 2.1 — data" e com "2.1.0").</summary>
    public static string? SectionFor(string version)
    {
        var secoes = Todas();
        if (secoes.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(version))
        {
            var achada = secoes.FirstOrDefault(s =>
                s.Version == version ||
                version.StartsWith(s.Version + ".", StringComparison.Ordinal) ||
                s.Version.StartsWith(version + ".", StringComparison.Ordinal));
            if (achada.Body.Length > 0) return achada.Body;
        }

        return secoes[0].Body.Length > 0 ? secoes[0].Body : null;
    }

    /// <summary>Todas as seções, da mais nova para a mais antiga.</summary>
    public static List<(string Version, string Body)> Todas()
    {
        var lista = new List<(string, string)>();
        try
        {
            if (!File.Exists(Arquivo)) return lista;

            string? versao = null;
            var corpo = new List<string>();

            void Fechar()
            {
                if (versao is not null)
                    lista.Add((versao, string.Join(Environment.NewLine, corpo).Trim()));
                corpo.Clear();
            }

            foreach (var linha in File.ReadAllLines(Arquivo))
            {
                if (linha.StartsWith("## ", StringComparison.Ordinal))
                {
                    Fechar();
                    var titulo = linha[3..].Trim();
                    var token = titulo.Split(' ', '\t')[0];
                    versao = token.Length > 0 && char.IsDigit(token[0]) ? token : null;
                    continue;
                }
                if (versao is not null) corpo.Add(linha);
            }
            Fechar();
        }
        catch (Exception ex)
        {
            AppPaths.Log("PatchNotes: " + ex.Message);
        }
        return lista;
    }
}
