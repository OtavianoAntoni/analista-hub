using System.Runtime.InteropServices;
using System.Text;
using AnalistaHub.Models;
using AnalistaHub.Services;

namespace AnalistaHub;

/// <summary>
/// Modo de linha de comando, sem janela. Serve para o instalador e para diagnóstico:
///   AnalistaHub.exe --list-tools
///   AnalistaHub.exe --test mkt-serial 10.0.0.1
///   AnalistaHub.exe --probe mkt-serial
/// Imprime JSON e sai com 0 (ok) ou 1 (falha).
/// </summary>
public static class Cli
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    private const int ATTACH_PARENT_PROCESS = -1;

    // janela descartável para testar RegisterHotKey fora da interface (--try-hotkey)
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);

    public static bool IsCliInvocation(string[] args) =>
        args.Length > 0 && args[0] is "--list-tools" or "--test" or "--probe" or "--version" or "--help"
            or "--set-cred" or "--clear-cred" or "--set-trigger" or "--validate-cred" or "--check-tools"
            or "--try-hotkey" or "--whatsnew" or "--hotkeys" or "--ping" or "--ping-sim"
            or "--check-update" or "--download-update" or "--apply-update";

    public static int Run(string[] args, ToolCatalog catalog)
    {
        AttachConsole(ATTACH_PARENT_PROCESS);
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch { }

        var command = args[0];
        var output = new StringBuilder();

        switch (command)
        {
            case "--version":
                output.AppendLine(Json(new { ok = true, version = typeof(App).Assembly.GetName().Version?.ToString(3) }));
                break;

            case "--help":
                output.AppendLine("Analista Hub — uso:");
                output.AppendLine("  AnalistaHub.exe                     abre a janela principal");
                output.AppendLine("  AnalistaHub.exe --list-tools        lista as ferramentas e o que exigem");
                output.AppendLine("  AnalistaHub.exe --test <id> [valor] executa a ferramenta e imprime o resultado");
                output.AppendLine("  AnalistaHub.exe --probe <id>        testa serviço e credenciais da ferramenta");
                output.AppendLine("  AnalistaHub.exe --set-cred <id> <campo>   grava credencial lida do STDIN");
                output.AppendLine("  AnalistaHub.exe --clear-cred <id>   apaga as credenciais guardadas da ferramenta");
                output.AppendLine("  AnalistaHub.exe --validate-cred <id> confirma as credenciais no serviço");
                output.AppendLine("  AnalistaHub.exe --check-tools       valida o catálogo (manifesto, script, campos)");
                output.AppendLine("  AnalistaHub.exe --try-hotkey [combo] testa se o Windows libera o atalho global");
                output.AppendLine("  AnalistaHub.exe --ping <ip> [--count N] [--timeout ms]  pinga o destino (ICMP) e mede");
                output.AppendLine("  AnalistaHub.exe --ping-sim [--debounce ms]  prova debounce, cancelamento e trava de 100");
                output.AppendLine("  AnalistaHub.exe --whatsnew         mostra o que mudou na versão instalada (PATCHLOG)");
                output.AppendLine("  AnalistaHub.exe --check-update      consulta o canal de atualização e imprime o resultado");
                output.AppendLine("  AnalistaHub.exe --download-update   baixa e prepara a atualização (não aplica)");
                output.AppendLine("  AnalistaHub.exe --apply-update      aplica a atualização preparada e reabre o app");
                break;

            case "--hotkeys":
            {
                // Diagnóstico dos atalhos por ferramenta: registra um a um numa janela descartável e
                // relata aceite, recusa e conflito — sem abrir a interface.
                var hwnd = CreateWindowEx(0, "STATIC", "hub-hotkeys-test", 0, 0, 0, 0, 0,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (hwnd == IntPtr.Zero)
                {
                    output.AppendLine(Json(new { ok = false, erro = "não consegui criar janela de teste" }));
                    break;
                }

                var linhas = new List<object>();
                var paleta = SettingsService.Current.Hotkey;

                using (var svc = new HotkeyService(hwnd))
                {
                    var paletaOk = svc.Register(paleta, out var erroPaleta);

                    foreach (var par in SettingsService.Current.ToolHotkeys.OrderBy(p => p.Key))
                    {
                        var conflito = svc.Conflito(par.Value, par.Key);
                        var ok = conflito is null && svc.RegisterTool(par.Key, par.Value, out _);
                        var erro = conflito ?? (ok ? "" : "o Windows recusou esta combinação");
                        linhas.Add(new
                        {
                            ferramenta = par.Key,
                            atalho = par.Value,
                            ok,
                            problema = erro
                        });
                        // mantém registrado: o próximo é checado contra este (é o que revela conflito)
                    }

                    output.AppendLine(Json(new
                    {
                        ok = linhas.Cast<dynamic>().All(l => (bool)l.ok),
                        paleta,
                        paleta_registrada = paletaOk,
                        erro_paleta = erroPaleta,
                        atalhos = linhas
                    }, true));
                }

                DestroyWindow(hwnd);
                break;
            }

            case "--try-hotkey":
            {
                // Diagnóstico do atalho: diz se o Windows libera a combinação AGORA e prova que, quando
                // ela está ocupada, o atalho salvo continua valendo (a troca é transacional).
                var combo = args.ElementAtOrDefault(1) ?? SettingsService.Current.Hotkey;
                var hwnd = CreateWindowEx(0, "STATIC", "hub-hotkey-test", 0, 0, 0, 0, 0,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (hwnd == IntPtr.Zero)
                {
                    output.AppendLine(Json(new { ok = false, erro = "não consegui criar janela de teste" }));
                    break;
                }

                using (var svc = new HotkeyService(hwnd))
                {
                    var salvo = SettingsService.Current.Hotkey;
                    var salvoOk = svc.Register(salvo, out var erroSalvo);
                    var tentadoOk = svc.Register(combo, out var erroTentado);

                    output.AppendLine(Json(new
                    {
                        ok = tentadoOk,
                        salvo,
                        salvo_registrado = salvoOk,
                        erro_salvo = erroSalvo,
                        // o que está REALMENTE gravado no disco agora (prova que a preferência
                        // sobrevive ao próximo início; comparação por valor, não por texto)
                        salvo_no_disco = SettingsService.ReadFromDisk()?.Hotkey,
                        disco_confere = SettingsService.ReadFromDisk()?.Hotkey == salvo,
                        tentado = combo,
                        tentado_registrado = tentadoOk,
                        erro_tentado = erroTentado,
                        ainda_ativo = svc.CurrentCombo
                    }, true));
                }
                DestroyWindow(hwnd);
                break;
            }

            case "--whatsnew":
            {
                // Mostra o que mudou na versão instalada (lido do PATCHLOG.md ao lado do exe).
                var texto = PatchNotes.SectionFor(UpdateService.CurrentVersion);
                if (texto is null)
                    output.AppendLine(Json(new { ok = false, versao = UpdateService.CurrentVersion,
                        motivo = "PATCHLOG.md não encontrado ao lado do programa" }, true));
                else
                    output.AppendLine(Json(new { ok = true, versao = UpdateService.CurrentVersion, notas = texto }, true));
                Write(output.ToString());
                return 0;
            }

            case "--check-tools":
            {
                // --skip-scripts valida só a estrutura do catálogo (base local, campos de credencial)
                // sem exigir o arquivo do script — útil em build que não copiou tools/ ainda.
                // A regra de LOCAL do script vale sempre (ver docs/REGRAS-DE-FERRAMENTA.md R9).
                var skipScripts = args.Contains("--skip-scripts");
                var problems = new List<object>();
                foreach (var tool in catalog.Tools)
                {
                    if (tool.IsBroken)
                    {
                        problems.Add(new { id = tool.Id, problema = "manifesto: " + tool.LoadError });
                        continue;
                    }

                    if (tool.IsDataset)
                    {
                        if (!tool.DatasetExists)
                            problems.Add(new { id = tool.Id, problema = "base não encontrada: " + tool.ExpandedDataset });
                    }
                    else if (!skipScripts && !tool.ScriptExists)
                        problems.Add(new { id = tool.Id, problema = "script não encontrado: " + tool.ExpandedScript });
                    // A regra R9 (script ao lado do manifesto, sem caminho, nunca no Espanso) é
                    // aplicada no carregamento do catálogo: quem viola nem entra como ferramenta
                    // válida — aparece acima como "manifesto: …".

                    if (tool.Credentials is not null && tool.Credentials.Fields.Count == 0)
                        problems.Add(new { id = tool.Id, problema = "credenciais declaradas sem campos" });

                    // R12: a descrição é a frase curta da ferramenta. Metadado técnico (contagem, data,
                    // "agora:", separadores) pertence ao rodapé/estado, não à descrição — foi o que
                    // duplicou texto na HUD e virou ruído nos scripts antigos.
                    var descProblema = DescriptionProblem(tool.Description);
                    if (descProblema is not null)
                        problems.Add(new { id = tool.Id, problema = "descrição: " + descProblema });

                    // Ícone por arquivo (R10): se o manifesto aponta um .ico/.png, o arquivo tem de existir
                    // ao lado dele. Sem isso a ferramenta cai no glifo genérico — funciona, mas não é o
                    // ícone que o analista escolheu.
                    if (tool.IconIsFile && !tool.IconFileExists)
                        problems.Add(new { id = tool.Id, problema = "ícone não encontrado: " + tool.ExpandedIconFile });

                    // R14: "ping" é do campo de IP. Em campo de texto o HUD não teria o que pingar,
                    // e a promessa do manifesto ficaria só no papel.
                    foreach (var campo in tool.Inputs.Where(i => i.Ping &&
                                 !string.Equals(i.Type, "ip", StringComparison.OrdinalIgnoreCase)))
                        problems.Add(new { id = tool.Id, problema = $"ping declarado fora de campo IP: «{campo.Label}» ({campo.Type})" });

                    // CFG-002: o conceito de gatilho saiu do produto no 2.2.1. Um manifesto antigo com
                    // "trigger" carregaria em silêncio (chave desconhecida é ignorada) prometendo algo
                    // que ninguém usa — o gate manda remover.
                    if (ManifestoDeclara("trigger", tool.SourcePath))
                        problems.Add(new { id = tool.Id, problema = "manifesto ainda declara \"trigger\" — remova o campo (CFG-002, 2.2.1)" });
                }

                // Chaves ${CFG:...} que o catálogo pediu e a máquina não tem. NÃO é erro de catálogo
                // (o repositório é público e não traz endereço nenhum) — é aviso de instalação.
                var avisos = LocalConfig.Faltando
                    .Select(k => $"configuração local ausente: {k} (em {LocalConfig.FilePath})")
                    .ToList();

                // NET-002: lista de NMs apontada na configuração mas inacessível (pasta da rede fora
                // do ar, OneDrive não sincronizado, caminho digitado errado)
                var caminhoRede = LocalConfig.Get("sse-rede.path");
                if (!string.IsNullOrWhiteSpace(caminhoRede))
                {
                    var expandido = Environment.ExpandEnvironmentVariables(caminhoRede.Replace('/', '\\').Trim());
                    if (!System.IO.File.Exists(expandido))
                        avisos.Add($"lista de NMs configurada mas inacessível: {expandido}");
                }

                output.AppendLine(Json(new
                {
                    ok = problems.Count == 0,
                    ferramentas = catalog.Tools.Count,
                    problemas = problems,
                    avisos
                }, true));

                Write(output.ToString());
                return problems.Count == 0 ? 0 : 1;
            }

            case "--validate-cred":
            {
                var tool = catalog.Find(args.ElementAtOrDefault(1) ?? "");
                if (tool is null) return Fail(output, "ferramenta não encontrada");
                if (!CredentialValidator.CanValidate(tool)) return Fail(output, "ferramenta sem regra de validação declarada");

                var result = CredentialValidator.ValidateAsync(tool, CancellationToken.None).GetAwaiter().GetResult();
                output.AppendLine(Json(new
                {
                    ok = result.State == AuthState.Ok,
                    ferramenta = tool.Id,
                    estado = result.State.ToString().ToLowerInvariant(),
                    mensagem = result.Message
                }, true));
                Write(output.ToString());
                return result.State == AuthState.Ok ? 0 : 1;
            }

            case "--set-cred":
            {
                var tool = catalog.Find(args.ElementAtOrDefault(1) ?? "");
                if (tool is null) return Fail(output, "ferramenta não encontrada");
                var key = args.ElementAtOrDefault(2) ?? "";
                if (tool.Credentials is null || tool.Credentials.Fields.All(f => !string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)))
                    return Fail(output, $"campo de credencial \"{key}\" não existe na ferramenta {tool.Id}");

                // o valor vem do STDIN: assim não aparece em lista de processos nem em histórico de shell
                string? line;
                try { line = Console.In.ReadLine(); } catch { line = null; }
                if (string.IsNullOrEmpty(line)) return Fail(output, "nenhum valor recebido no STDIN");

                CredentialStore.Set(tool.Id, key, line.Trim());
                var state = Probes.CheckCredentials(tool);
                output.AppendLine(Json(new
                {
                    ok = state.Complete,
                    ferramenta = tool.Id,
                    campo = key,
                    credenciais_completas = state.Complete,
                    faltando = state.Missing
                }, true));
                Write(output.ToString());
                return state.Complete ? 0 : 0;
            }

            case "--clear-cred":
            {
                var tool = catalog.Find(args.ElementAtOrDefault(1) ?? "");
                if (tool is null) return Fail(output, "ferramenta não encontrada");
                CredentialStore.Clear(tool.Id);
                output.AppendLine(Json(new { ok = true, ferramenta = tool.Id, credenciais = "apagadas" }));
                break;
            }

            case "--list-tools":
            {
                var tools = catalog.Tools.Select(t => new
                {
                    id = t.Id,
                    nome = t.Name,
                    tipo = t.IsDataset ? "dataset" : "script",
                    estado = t.IsBroken ? "com problema: " + t.LoadError : "ok",
                    script = t.IsDataset ? null : t.ExpandedScript,
                    script_existe = t.IsDataset ? (bool?)null : t.ScriptExists,
                    base_local = t.IsDataset ? t.ExpandedDataset : null,
                    base_existe = t.IsDataset ? t.DatasetExists : (bool?)null,
                    entradas = t.Inputs.Select(i => new { chave = i.Key, rotulo = i.Label, tipo = i.Type, usa_clipboard = i.EmptyUsesClipboard }),
                    saida_padrao = t.Output.Default,
                    credenciais = t.Credentials is null ? null : new
                    {
                        completo = Probes.CheckCredentials(t).Complete,
                        campos = t.Credentials.Fields.Select(f => f.Label)
                    },
                    servico = t.Network is null ? null : $"{t.Network.Host}:{t.Network.Port}"
                }).ToList();
                output.AppendLine(Json(new { ok = true, pasta_usuario = SettingsService.Current.ToolsFolder, ferramentas = tools },
                    writeIndented: true));
                break;
            }

            case "--probe":
            {
                var tool = catalog.Find(args.ElementAtOrDefault(1) ?? "");
                if (tool is null) return Fail(output, "ferramenta não encontrada");
                bool? service = null;
                if (tool.Network is not null)
                    service = Probes.CheckTcpAsync(tool.Network.Host, tool.Network.Port, 3000).GetAwaiter().GetResult();
                object? cred = null;
                if (tool.Credentials is not null && tool.Credentials.Fields.Count > 0)
                {
                    var state = Probes.CheckCredentials(tool);
                    cred = new { completo = state.Complete, faltando = state.Missing };
                }
                output.AppendLine(Json(new { ok = true, ferramenta = tool.Id, servico_responde = service, credenciais = cred }, true));
                break;
            }

            case "--test":
            {
                var tool = catalog.Find(args.ElementAtOrDefault(1) ?? "");
                if (tool is null) return Fail(output, "ferramenta não encontrada");
                var value = args.ElementAtOrDefault(2) ?? "";

                // ferramenta de base local: consulta em memória, sem processo nem rede
                if (tool.IsDataset)
                {
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    var sse = SseCatalog.Current;
                    var matches = sse.Search(value);
                    started.Stop();

                    var top = matches.FirstOrDefault();
                    var formatted = top is null ? "" : SseCatalog.Format(top);
                    output.AppendLine(Json(new
                    {
                        ok = sse.Loaded && matches.Count > 0,
                        ferramenta = tool.Id,
                        tipo = "dataset",
                        base_local = sse.DataPath,
                        cidades_na_base = sse.Cities.Count,
                        cidades_com_nm = sse.TotalWithAccess,
                        linhas = sse.TotalLines,
                        gerado_em = sse.Generated,
                        entrada = value,
                        encontrados = matches.Count,
                        resultados = matches.Take(20).Select(c => new
                        {
                            cidade = c.Name,
                            uf = c.Uf,
                            sigla_anatel = c.Sigla,
                            acesso = c.HasAccess,
                            origem = c.Origin,
                            redes = c.Rows.Select(r => new { nm = r.Nm, olt = r.Olt, vpn = r.Vpn })
                        }),
                        valor = formatted,
                        milissegundos = Math.Round(started.Elapsed.TotalMilliseconds, 3),
                        erro = sse.Error
                    }, true));

                    if (top is not null && args.Contains("--copy"))
                    {
                        ClipboardService.SetText(formatted);
                        HistoryStore.Append(new HistoryEntry(DateTime.Now, tool.Id, tool.Name,
                            top.Name + " (" + top.Uf + ")", true, started.Elapsed.TotalSeconds, formatted));
                    }

                    Write(output.ToString());
                    return sse.Loaded && top is not null ? 0 : 1;
                }

                var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var first = tool.Inputs.FirstOrDefault();
                if (first is not null && !string.IsNullOrWhiteSpace(value)) inputs[first.Key] = value;

                var result = ToolRunner.RunAsync(tool, inputs, CancellationToken.None).GetAwaiter().GetResult();
                output.AppendLine(Json(new
                {
                    ok = result.Ok,
                    ferramenta = tool.Id,
                    entrada = value,
                    valor = result.Value,
                    detalhe = result.Detail,
                    segundos = Math.Round(result.Elapsed.TotalSeconds, 2),
                    exit = result.ExitCode
                }, true));

                if (result.Ok)
                {
                    ClipboardService.SetText(result.Value);
                    HistoryStore.Append(new HistoryEntry(DateTime.Now, tool.Id, tool.Name, value, true,
                        result.Elapsed.TotalSeconds, result.Value));
                }
                else
                {
                    HistoryStore.Append(new HistoryEntry(DateTime.Now, tool.Id, tool.Name, value, false,
                        result.Elapsed.TotalSeconds, result.Detail));
                }

                Write(output.ToString());
                return result.Ok ? 0 : 1;
            }

            case "--ping":
            {
                // NET-001: prova real de ICMP pela MESMA sessão da HUD (sem processo ping.exe).
                // Task.Run pelo mesmo motivo do --check-update: o CLI roda no thread do WinUI.
                var alvo = args.ElementAtOrDefault(1) ?? "";
                var count = Math.Clamp(IntArg(args, "--count", 1), 1, PingSession.MaxProbes);
                var timeout = Math.Clamp(IntArg(args, "--timeout", SettingsService.Current.PingTimeoutMs), 200, 5000);
                var sessao = new PingSession(timeout);

                var amostras = Task.Run(async () =>
                {
                    var lista = new List<object>();
                    for (var i = 0; i < count; i++)
                    {
                        var a = await sessao.ProbeAsync(alvo).ConfigureAwait(false);
                        lista.Add(new
                        {
                            endereco = a.Address,
                            estado = a.Outcome.ToString().ToLowerInvariant(),
                            latencia_ms = a.RoundtripMs,
                            texto = a.Text
                        });
                        if (!a.Reachable) break;
                        if (i + 1 < count) await Task.Delay(200).ConfigureAwait(false);
                    }
                    return lista;
                }).GetAwaiter().GetResult();

                var alcancou = sessao.Recent.LastOrDefault()?.Reachable == true;
                output.AppendLine(Json(new
                {
                    ok = alcancou,
                    alvo = alvo.Trim(),
                    timeout_ms = sessao.TimeoutMs,
                    disparos = sessao.Used,
                    trava = PingSession.MaxProbes,
                    amostras
                }, true));
                Write(output.ToString());
                return alcancou ? 0 : 1;
            }

            case "--ping-sim":
            {
                // Prova o que não depende da rede: debounce, cancelamento por edição e trava de 100.
                var debounce = Math.Clamp(IntArg(args, "--debounce", SettingsService.Current.PingDebounceMs), 0, 5000);
                var sim = Task.Run(() => PingSimulation.RunAsync(debounce)).GetAwaiter().GetResult();
                output.AppendLine(Json(sim, true));
                Write(output.ToString());
                return sim.Ok ? 0 : 1;
            }

            case "--check-update":
            {
                // Task.Run: o modo CLI roda no thread do WinUI, que tem SynchronizationContext —
                // esperar um async direto nele trava (deadlock). Fora do contexto, não.
                var result = Task.Run(() => UpdateService.CheckAsync()).GetAwaiter().GetResult();
                UpdateService.LastCheck = result;
                SettingsService.Current.LastUpdateCheckUtc = result.CheckedAtUtc.ToString("o");
                SettingsService.Save();

                output.AppendLine(Json(new
                {
                    ok = result.Ok,
                    instalada = result.CurrentVersion,
                    ultima = result.LatestVersion,
                    atualizacao = result.HasUpdate,
                    sha256 = result.Asset?.Sha256,
                    url = result.Asset?.Url,
                    tamanho = result.Asset?.Size,
                    notas = result.Notes,
                    mensagem = UpdateService.Describe(result),
                    canal = UpdateService.FeedUrl,
                    erro = result.Error
                }, true));
                Write(output.ToString());
                return result.Ok ? 0 : 1;
            }

            case "--download-update":
            {
                var check = Task.Run(() => UpdateService.CheckAsync()).GetAwaiter().GetResult();
                if (!check.HasUpdate) return Fail(output, UpdateService.Describe(check));

                var destino = "";
                try
                {
                    destino = Task.Run(() => UpdateService.DownloadAsync(check)).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    return Fail(output, ex.Message);
                }

                output.AppendLine(Json(new
                {
                    ok = true,
                    versao = check.LatestVersion,
                    payload = destino,
                    pendente = SettingsService.Current.PendingUpdateVersion
                }, true));
                Write(output.ToString());
                return 0;
            }

            case "--apply-update":
            {
                var dir = UpdateService.PendingPayloadDir();
                if (dir is null) return Fail(output, "nenhum pacote baixado esperando reinício");

                if (!UpdateService.ApplyAndRestart(dir, out var erro)) return Fail(output, erro);

                output.AppendLine(Json(new { ok = true, aplicar = dir, pasta = UpdateService.InstallDir }, true));
                Write(output.ToString());
                Environment.Exit(0);
                return 0;
            }
        }

        Write(output.ToString());
        return 0;
    }

    private static int Fail(StringBuilder output, string message)
    {
        output.AppendLine(Json(new { ok = false, erro = message }));
        Write(output.ToString());
        return 1;
    }

    private static void Write(string text)
    {
        try { Console.Out.Write(text); Console.Out.Flush(); } catch { }
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "cli-last.json"), text);
        }
        catch { }
    }

    private static int IntArg(string[] args, string name, int fallback)
    {
        var idx = Array.IndexOf(args, name);
        if (idx < 0 || idx + 1 >= args.Length) return fallback;
        return int.TryParse(args[idx + 1], out var value) ? value : fallback;
    }

    /// <summary>
    /// Regra R12 (docs/REGRAS-DE-FERRAMENTA.md): descrição = frase curta do que a ferramenta faz.
    /// Sem contagem, data, "agora:", separadores de metadado ou quebra de linha.
    /// </summary>
    private static string? DescriptionProblem(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return "vazia (descreva o que a ferramenta faz em uma frase)";
        if (desc.Length > 140) return $"longa demais ({desc.Length} caracteres; máximo 140)";
        if (desc.Contains('\n') || desc.Contains('\r')) return "com quebra de linha";
        if (desc.Contains('•') || desc.Contains('·') || desc.Contains(" | "))
            return "com separador de metadado (•, ·, |) — deixe só a frase";
        if (System.Text.RegularExpressions.Regex.IsMatch(desc, @"(?i)\b(agora|fonte|base de|gerado em|atualizado em)\s*:"))
            return "com rótulo de metadado (\"agora:\", \"fonte:\"…)";
        if (System.Text.RegularExpressions.Regex.IsMatch(desc, @"\d{4}-\d{2}-\d{2}"))
            return "com data — data/base é estado, não descrição";
        if (System.Text.RegularExpressions.Regex.IsMatch(desc, @"(?i)\d+\s+(cidades|com\s+nm|ferramentas?|itens?|registros?)"))
            return "com contagem de itens — isso é estado, não descrição";
        return null;
    }

    /// <summary>
    /// O manifesto declara essa chave no JSON? Usado pelo gate para o que saiu do produto (CFG-002:
    /// `trigger`) — chave desconhecida não estoura o carregamento, então sem esta checagem o resíduo
    /// passaria batido para sempre.
    /// </summary>
    private static bool ManifestoDeclara(string chave, string path)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   doc.RootElement.EnumerateObject().Any(p =>
                       string.Equals(p.Name, chave, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static string Json(object value, bool writeIndented = false) =>
        System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions { WriteIndented = writeIndented });
}
