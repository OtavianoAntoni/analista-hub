using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace AnalistaHub.Services;

/// <summary>Resultado da assinatura de um script.</summary>
public sealed class AssinaturaArquivo
{
    [JsonPropertyName("arquivo")] public string Arquivo { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("motivo")] public string Motivo { get; set; } = "";
}

/// <summary>Estado da assinatura local (vai para assinatura.json e para as telas).</summary>
public sealed class AssinaturaEstado
{
    [JsonPropertyName("status")] public string Status { get; set; } = "ausente";
    [JsonPropertyName("motivo")] public string Motivo { get; set; } = "";
    [JsonPropertyName("escopo")] public string Escopo { get; set; } = "";
    [JsonPropertyName("thumbprint")] public string Thumbprint { get; set; } = "";
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("criadoEm")] public string CriadoEm { get; set; } = "";
    [JsonPropertyName("validoAte")] public string ValidoAte { get; set; } = "";
    [JsonPropertyName("politica")] public string Politica { get; set; } = "";
    [JsonPropertyName("podeAssinar")] public bool PodeAssinar { get; set; }
    [JsonPropertyName("pasta")] public string Pasta { get; set; } = "";
    [JsonPropertyName("arquivos")] public List<AssinaturaArquivo> Arquivos { get; set; } = new();
    [JsonPropertyName("verificadoEm")] public string VerificadoEm { get; set; } = "";

    [JsonIgnore] public bool Ok => Status == "ok";
    [JsonIgnore] public string ThumbprintCurto =>
        Thumbprint.Length > 12 ? Thumbprint[..12] + "…" : Thumbprint;
}

/// <summary>
/// SIG-001 — assinatura local dos scripts das ferramentas.
///
/// PROBLEMA: o app já chama o PowerShell com <c>-ExecutionPolicy Bypass</c>, mas quando a política
/// vem de GPO (MachinePolicy/UserPolicy) a flag não sobrepõe nada — sob <c>AllSigned</c> um .ps1 sem
/// assinatura válida de certificado confiável não roda.
///
/// SOLUÇÃO: a própria máquina cria a âncora de confiança (raiz + publicador no repositório de
/// certificados do usuário ou da máquina) e assina os scripts que o app executa. Isto NÃO é controle
/// de segurança: qualquer processo do mesmo usuário pode assinar o que quiser com a mesma âncora. O
/// objetivo é satisfazer a política de execução.
///
/// Implementado em .NET puro (e não em PowerShell) porque consertar um bloqueio de script chamando
/// script cai na mesma política. O formato do bloco foi provado contra o próprio Windows: um .ps1
/// assinado aqui volta <c>Valid</c> em <c>Get-AuthenticodeSignature</c>.
/// </summary>
public static class ToolSigning
{
    public const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";
    private const string ContentTypeSpcIndirectData = "1.3.6.1.4.1.311.2.1.4";
    private const string SpcSipInfoOid = "1.3.6.1.4.1.311.2.1.30";
    private const string SpcOpusInfoOid = "1.3.6.1.4.1.311.2.1.12";
    private const string SpcStatementTypeOid = "1.3.6.1.4.1.311.2.1.11";
    private const string SpcIndividualPurposeOid = "1.3.6.1.4.1.311.2.1.21";
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";
    private const string SignedDataOid = "1.2.840.113549.1.7.2";
    /// <summary>
    /// No SignerInfo da Authenticode o algoritmo de assinatura é declarado como id-ecPublicKey
    /// (1.2.840.10045.2.1) com parâmetro NULL — é o que o SignTool/Set-AuthenticodeSignature grava.
    /// Com ecdsa-with-SHA256 o Windows responde "nenhuma assinatura encontrada".
    /// </summary>
    private const string EcPublicKey = "1.2.840.10045.2.1";
    private const string RsaPkcs1 = "1.2.840.113549.1.1.1";

    /// <summary>GUID do SIP de arquivo plano — o mesmo que o SignTool/Set-AuthenticodeSignature grava.</summary>
    private static readonly byte[] SipGuid =
        { 0x1f, 0xcc, 0x3b, 0x60, 0x59, 0x4b, 0x08, 0x4e, 0xb7, 0x24, 0xd2, 0xc6, 0x29, 0x7e, 0xf3, 0x51 };

    private const string BeginMarker = "# SIG # Begin signature block";
    private const string EndMarker = "# SIG # End signature block";

    /// <summary>Estado em disco. Fica junto dos outros dados do usuário.</summary>
    public static string EstadoFile => Path.Combine(AppPaths.DataDir, "assinatura.json");

    /// <summary>Assunto do certificado: produto, usuário e máquina — nunca host, domínio ou cliente.</summary>
    public static string Assunto(bool maquina) =>
        $"CN=Analista Hub — Assinatura Local ({Environment.UserName}@{Environment.MachineName})";

    /// <summary>Pasta com os scripts que o app executa (ao lado do executável).</summary>
    public static string PastaFerramentas => Path.Combine(UpdateService.InstallDir, "tools");

    // ------------------------------------------------------------------ política

    /// <summary>
    /// Política de execução EFETIVA do Windows PowerShell 5.1. A precedência é
    /// MachinePolicy &gt; UserPolicy &gt; Machine &gt; User (as duas primeiras vêm de GPO e não podem ser
    /// sobrepostas por flag nem por Set-ExecutionPolicy).
    /// </summary>
    public static (string Nome, bool DeGpo, bool Bloqueia) Politica()
    {
        string Ler(RegistryKey raiz, string caminho)
        {
            try
            {
                using var chave = raiz.OpenSubKey(caminho);
                return chave?.GetValue("ExecutionPolicy") as string ?? "";
            }
            catch { return ""; }
        }

        const string politicas = @"SOFTWARE\Policies\Microsoft\Windows\PowerShell";
        const string shellIds = @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell";

        var maquinaGpo = Ler(Registry.LocalMachine, politicas);
        var usuarioGpo = Ler(Registry.CurrentUser, politicas);
        var maquina = Ler(Registry.LocalMachine, shellIds);
        var usuario = Ler(Registry.CurrentUser, shellIds);

        // precedência documentada: MachinePolicy > UserPolicy > CurrentUser > LocalMachine
        var efetiva = new[] { maquinaGpo, usuarioGpo, usuario, maquina }
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "Restricted";

        var deGpo = !string.IsNullOrWhiteSpace(maquinaGpo) || !string.IsNullOrWhiteSpace(usuarioGpo);
        var bloqueia = efetiva.Equals("Restricted", StringComparison.OrdinalIgnoreCase);
        return (efetiva, deGpo, bloqueia);
    }

    // ------------------------------------------------------------------ estado

    public static AssinaturaEstado? LerEstado()
    {
        try
        {
            if (!File.Exists(EstadoFile)) return null;
            return JsonSerializer.Deserialize<AssinaturaEstado>(File.ReadAllText(EstadoFile));
        }
        catch (Exception ex)
        {
            AppPaths.Log("ToolSigning: estado ilegível — " + ex.Message);
            return null;
        }
    }

    private static void SalvarEstado(AssinaturaEstado estado)
    {
        try
        {
            AppPaths.Ensure();
            estado.VerificadoEm = DateTime.Now.ToString("o");
            File.WriteAllText(EstadoFile, JsonSerializer.Serialize(estado,
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            AppPaths.Log($"ToolSigning: estado {estado.Status} ({estado.Motivo}) em {EstadoFile}");
        }
        catch (Exception ex) { AppPaths.Log("ToolSigning: gravar estado falhou — " + ex.Message); }
    }

    // ------------------------------------------------------------------ certificado

    /// <summary>Certificado da assinatura local, se já existir e ainda valer.</summary>
    public static X509Certificate2? LocalizarCertificado(StoreLocation local, bool exigirValidade = true)
    {
        try
        {
            using var store = new X509Store(StoreName.My, local);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates
                .Where(c => c.Subject.Contains("Analista Hub", StringComparison.OrdinalIgnoreCase) &&
                            c.Subject.Contains("Assinatura Local", StringComparison.OrdinalIgnoreCase))
                .Where(c => c.HasPrivateKey || local == StoreLocation.LocalMachine)
                .Where(c => !exigirValidade || c.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30))
                .OrderByDescending(c => c.NotAfter)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            AppPaths.Log($"ToolSigning: procurar certificado em {local} falhou — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Cria o certificado com chave privada NÃO exportável (chave persistida no KSP, política de
    /// exportação None). Nada é gravado em .pfx: a chave nasce e morre no repositório de chaves.
    /// </summary>
    public static X509Certificate2? CriarCertificado(bool maquina, out string erro)
    {
        erro = "";
        try
        {
            var nomeChave = "AnalistaHub-CodeSigning-" + (maquina ? "Maquina" : Environment.UserName);
            var parametros = new CngKeyCreationParameters
            {
                Provider = new CngProvider("Microsoft Software Key Storage Provider"),
                KeyUsage = CngKeyUsages.Signing,
                ExportPolicy = CngExportPolicies.None,
                KeyCreationOptions = maquina ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None
            };

            // mesma chave recriada (renovação): sobrescreve a anterior em vez de acumular
            parametros.KeyCreationOptions |= CngKeyCreationOptions.OverwriteExistingKey;

            using var chave = CngKey.Create(CngAlgorithm.ECDsaP256, nomeChave, parametros);
            using var ecdsa = new ECDsaCng(chave);

            var pedido = new CertificateRequest(new X500DistinguishedName(Assunto(maquina)), ecdsa,
                HashAlgorithmName.SHA256);
            pedido.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            pedido.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            pedido.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(CodeSigningEku) }, false));
            pedido.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(pedido.PublicKey, false));

            var certificado = pedido.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(10));

            var local = maquina ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
            using (var store = new X509Store(StoreName.My, local))
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(certificado);
            }

            // relê do repositório: é o que prova que a chave persistiu (não é efêmera do processo)
            var relido = new X509Certificate2(certificado.RawData);
            using (var store = new X509Store(StoreName.My, local))
            {
                store.Open(OpenFlags.ReadOnly);
                relido = store.Certificates.Find(X509FindType.FindByThumbprint, certificado.Thumbprint, false)
                    .FirstOrDefault() ?? relido;
            }

            AppPaths.Log($"ToolSigning: certificado criado — {certificado.Subject} · " +
                         $"thumbprint {certificado.Thumbprint} · escopo {(maquina ? "máquina" : "usuário")} · " +
                         $"chave persistida relida: {relido.HasPrivateKey}");
            return relido;
        }
        catch (Exception ex)
        {
            erro = ex.Message;
            AppPaths.Log("ToolSigning: criar certificado falhou — " + ex);
            return null;
        }
    }

    /// <summary>
    /// Ancora o certificado como raiz e publicador confiável. Idempotente (compara thumbprint).
    /// No repositório do usuário isso é silencioso; no da máquina o Windows pede confirmação.
    /// </summary>
    public static bool Ancorar(X509Certificate2 certificado, bool maquina, out string erro)
    {
        erro = "";
        var local = maquina ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
        try
        {
            foreach (var nome in new[] { StoreName.Root, StoreName.TrustedPublisher })
            {
                using var store = new X509Store(nome, local);
                store.Open(OpenFlags.ReadWrite);
                var jaExiste = store.Certificates.Find(X509FindType.FindByThumbprint, certificado.Thumbprint, false)
                    .Count > 0;
                if (jaExiste) continue;
                // só a parte pública vai para a âncora
                using var publico = new X509Certificate2(certificado.RawData);
                store.Add(publico);
                AppPaths.Log($"ToolSigning: {certificado.Thumbprint} ancorado em {local}\\{nome}");
            }
            return true;
        }
        catch (Exception ex)
        {
            erro = ex.Message;
            AppPaths.Log($"ToolSigning: ancorar em {local} falhou — {ex}");
            return false;
        }
    }

    // ------------------------------------------------------------------ assinatura

    /// <summary>
    /// Assina um .ps1. O conteúdo é preservado byte a byte (inclusive o BOM UTF-8); só o bloco
    /// <c># SIG # Begin signature block</c> é acrescentado no fim, como o PowerShell espera.
    /// </summary>
    public static bool Assinar(string caminho, X509Certificate2 certificado, out string erro, out string sha256)
    {
        erro = "";
        sha256 = "";
        try
        {
            var bytes = File.ReadAllBytes(caminho);
            var conteudo = RemoverBloco(bytes);

            // O Windows hasheia o TEXTO do script em UTF-16LE (é o SIP de arquivo plano dos .ps1) e
            // decodifica o arquivo como: UTF-8 se tem BOM, UTF-16 se tem BOM, ANSI se NÃO tem BOM.
            // Provado comparando o digest dos blocos que o próprio Set-AuthenticodeSignature grava.
            var dados = bytesParaHash(conteudo);
            var digest = SHA256.HashData(dados);

            var spc = MontarSpcIndirectData(digest);
            var der = MontarCms(spc, certificado);

            var final = new List<byte>(conteudo.Length + 2048);
            final.AddRange(conteudo);
            final.AddRange(Encoding.ASCII.GetBytes("\r\n" + Bloco(der)));
            File.WriteAllBytes(caminho, final.ToArray());

            sha256 = Convert.ToHexString(SHA256.HashData(final.ToArray())).ToLowerInvariant();
            AppPaths.Log($"ToolSigning: assinado {caminho} ({final.Count} bytes, " +
                         $"cert {certificado.Thumbprint}, sha256 {sha256[..12]}…)");
            return true;
        }
        catch (Exception ex)
        {
            erro = ex.Message;
            AppPaths.Log($"ToolSigning: assinar {caminho} falhou — {ex}");
            return false;
        }
    }

    /// <summary>Bytes de conteúdo de um DER de um único elemento (descarta tag e tamanho).</summary>
    private static byte[] ConteudoDoDer(byte[] der)
    {
        if (der.Length < 2) return der;
        var inicio = 2;
        if ((der[1] & 0x80) != 0) inicio += der[1] & 0x7F;
        return inicio < der.Length ? der[inicio..] : der;
    }

    /// <summary>
    /// Bytes que entram no hash do SIP: o conteúdo do script convertido para UTF-16LE do mesmo jeito
    /// que o Windows converte — BOM UTF-8 → UTF-8; BOM UTF-16 → UTF-16; sem BOM → página ANSI do
    /// sistema. Errar isso faz o Windows responder "hash não confere" (ou não achar assinatura).
    /// </summary>
    private static byte[] bytesParaHash(byte[] conteudo)
    {
        if (conteudo.Length >= 3 && conteudo[0] == 0xEF && conteudo[1] == 0xBB && conteudo[2] == 0xBF)
            return Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(conteudo));

        if (conteudo.Length >= 2 && conteudo[0] == 0xFF && conteudo[1] == 0xFE)
            return conteudo[2..];

        if (conteudo.Length >= 2 && conteudo[0] == 0xFE && conteudo[1] == 0xFF)
            return Encoding.Unicode.GetBytes(Encoding.BigEndianUnicode.GetString(conteudo[2..]));

        return Encoding.Unicode.GetBytes(Ansi(conteudo));
    }

    /// <summary>Decodifica bytes com a página ANSI do sistema (CP_ACP) — igual ao Windows.</summary>
    private static string Ansi(byte[] dados)
    {
        try
        {
            var chars = MultiByteToWideChar(0 /* CP_ACP */, 0, dados, dados.Length, null, 0);
            if (chars > 0)
            {
                var buffer = new char[chars];
                if (MultiByteToWideChar(0, 0, dados, dados.Length, buffer, chars) > 0)
                    return new string(buffer);
            }
        }
        catch (Exception ex) { AppPaths.Log("ToolSigning: decodificar ANSI falhou — " + ex.Message); }
        return Encoding.Latin1.GetString(dados);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] bytes, int tamanho,
        char[]? texto, int tamanhoTexto);

    /// <summary>
    /// Bloco de assinatura no formato que o PowerShell reconhece: marcadores + base64 em linhas de
    /// 64 colunas, cada uma comentada com "# ".
    /// </summary>
    private static string Bloco(byte[] der)
    {
        var b64 = Convert.ToBase64String(der);
        var sb = new StringBuilder(BeginMarker.Length + b64.Length + b64.Length / 64 * 2 + 64);
        sb.Append(BeginMarker).Append("\r\n");
        for (var i = 0; i < b64.Length; i += 64)
            sb.Append("# ").Append(b64, i, Math.Min(64, b64.Length - i)).Append("\r\n");
        sb.Append(EndMarker).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Tira o bloco de assinatura (e o CRLF que ele acrescentou) do conteúdo do arquivo.</summary>
    private static byte[] RemoverBloco(byte[] bytes)
    {
        var marcador = Encoding.ASCII.GetBytes(BeginMarker);
        var inicio = bytes.AsSpan().IndexOf(marcador);
        if (inicio < 0) return bytes;
        if (inicio >= 2 && bytes[inicio - 2] == (byte)'\r' && bytes[inicio - 1] == (byte)'\n')
            inicio -= 2;
        return bytes[..inicio];
    }

    /// <summary>SpcIndirectDataContent: identificação do SIP + digest do conteúdo.</summary>
    private static byte[] MontarSpcIndirectData(byte[] digest)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())              // SpcAttributeTypeAndOptionalValue
            {
                writer.WriteObjectIdentifier(SpcSipInfoOid);
                using (writer.PushSequence())          // SpcSipInfo
                {
                    writer.WriteInteger(0x00010000);   // versão 1.0
                    writer.WriteOctetString(SipGuid);
                    for (var i = 0; i < 5; i++) writer.WriteInteger(0);   // reservados
                }
            }
            using (writer.PushSequence())              // DigestInfo
            {
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(Sha256Oid);
                    writer.WriteNull();
                }
                writer.WriteOctetString(digest);
            }
        }
        return writer.Encode();
    }

    /// <summary>
    /// PKCS#7 SignedData da Authenticode: o conteúdo encapsulado é o SpcIndirectDataContent
    /// <b>direto</b> dentro de [0] (e não dentro de um OCTET STRING, como o SignedCms do .NET faria —
    /// por isso o CMS é montado à mão).
    /// </summary>
    private static byte[] MontarCms(byte[] conteudoSpc, X509Certificate2 certificado)
    {
        var ecdsa = certificado.GetECDsaPrivateKey();
        var algoritmoAssinatura = ecdsa is not null ? EcPublicKey : RsaPkcs1;

        // atributos autenticados, na mesma composição que o Set-AuthenticodeSignature grava:
        // SpcSpOpusInfo, contentType, SpcStatementType e messageDigest. O SPC_STATEMENT_TYPE com
        // "assinatura individual" (1.3.6.1.4.1.311.2.1.21) é o que faz o Windows reconhecer o bloco
        // como assinatura Authenticode — sem ele o arquivo volta como "não assinado".
        var atributos = new AsnWriter(AsnEncodingRules.DER);
        using (atributos.PushSetOf())
        {
            using (atributos.PushSequence())                                     // SpcSpOpusInfo (vazio)
            {
                atributos.WriteObjectIdentifier(SpcOpusInfoOid);
                using (atributos.PushSetOf())
                using (atributos.PushSequence())
                {
                    using (atributos.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                        atributos.WriteEncodedValue(new byte[] { 0x80, 0x00 });   // programName: unicode vazio
                    using (atributos.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true)))
                        atributos.WriteEncodedValue(new byte[] { 0x80, 0x00 });   // moreInfo: link vazio
                }
            }
            using (atributos.PushSequence())
            {
                atributos.WriteObjectIdentifier("1.2.840.113549.1.9.3");             // contentType
                using (atributos.PushSetOf()) atributos.WriteObjectIdentifier(ContentTypeSpcIndirectData);
            }
            using (atributos.PushSequence())                                        // SpcStatementType
            {
                atributos.WriteObjectIdentifier(SpcStatementTypeOid);
                using (atributos.PushSetOf())
                using (atributos.PushSequence())
                    atributos.WriteObjectIdentifier(SpcIndividualPurposeOid);
            }
            using (atributos.PushSequence())
            {
                atributos.WriteObjectIdentifier("1.2.840.113549.1.9.4");             // messageDigest
                // O hash é sobre o CONTEÚDO do SpcIndirectDataContent (sem o cabeçalho SEQUENCE),
                // não sobre o DER inteiro — é o que o Set-AuthenticodeSignature grava, provado
                // comparando os blocos byte a byte.
                using (atributos.PushSetOf())
                    atributos.WriteOctetString(SHA256.HashData(ConteudoDoDer(conteudoSpc)));
            }
        }
        var derAtributos = atributos.Encode();          // tag 0x31 (SET) — é o que se assina
        // ECDSA no padrão do CMS é SEQUENCE{r,s} (RFC 3279); o SignData padrão devolve r||s cru, que
        // o Windows não valida (o arquivo volta como "não assinado").
        var assinatura = ecdsa is not null
            ? ecdsa.SignData(derAtributos, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
            : AssinarRsa(certificado, derAtributos);

        // a MESMA estrutura entra no SignerInfo com tag [0] IMPLICIT: só o primeiro byte muda
        var atributosImplicitos = (byte[])derAtributos.Clone();
        atributosImplicitos[0] = 0xA0;

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())                                        // ContentInfo
        {
            writer.WriteObjectIdentifier(SignedDataOid);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))   // [0] EXPLICIT
            using (writer.PushSequence())                                    // SignedData
            {
                writer.WriteInteger(1);
                using (writer.PushSetOf())                                   // digestAlgorithms
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(Sha256Oid);
                    writer.WriteNull();
                }

                using (writer.PushSequence())                                // ContentInfo do conteúdo
                {
                    writer.WriteObjectIdentifier(ContentTypeSpcIndirectData);
                    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                        writer.WriteEncodedValue(conteudoSpc);               // direto, sem OCTET STRING
                }

                using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0, true)))   // certificates [0]
                    writer.WriteEncodedValue(certificado.RawData);

                using (writer.PushSetOf())                                   // signerInfos
                using (writer.PushSequence())
                {
                    writer.WriteInteger(1);
                    using (writer.PushSequence())                            // issuerAndSerialNumber
                    {
                        writer.WriteEncodedValue(certificado.IssuerName.RawData);
                        // SerialNumberBytes é big-endian (o GetSerialNumber devolve little-endian e
                        // gera um INTEGER que não casa com o certificado: TRUST_E_NO_SIGNER_CERT)
                        writer.WriteInteger(certificado.SerialNumberBytes.Span);
                    }
                    using (writer.PushSequence())
                    {
                        writer.WriteObjectIdentifier(Sha256Oid);
                        writer.WriteNull();
                    }
                    writer.WriteEncodedValue(atributosImplicitos);
                    using (writer.PushSequence())
                    {
                        writer.WriteObjectIdentifier(algoritmoAssinatura);
                        writer.WriteNull();
                    }
                    writer.WriteOctetString(assinatura);
                }
            }
        }
        return writer.Encode();
    }

    private static byte[] AssinarRsa(X509Certificate2 certificado, byte[] dados)
    {
        using var rsa = certificado.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("certificado sem chave RSA/ECDSA utilizável");
        return rsa.SignData(dados, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    // ------------------------------------------------------------------ verificação

    /// <summary>
    /// O arquivo está assinado por nós e o conteúdo não foi alterado depois? Compara o digest gravado
    /// dentro do bloco com o digest recalculado do texto atual — é o que detecta .ps1 trocado por
    /// outra pessoa ou por uma atualização. (A validação de confiança da cadeia fica com o Windows.)
    /// </summary>
    public static bool AssinaturaConfere(string caminho)
    {
        try
        {
            var bytes = File.ReadAllBytes(caminho);
            var marcador = Encoding.ASCII.GetBytes(BeginMarker);
            var inicio = bytes.AsSpan().IndexOf(marcador);
            if (inicio < 0) return false;

            var conteudo = RemoverBloco(bytes);
            var digest = SHA256.HashData(bytesParaHash(conteudo));
            var esperado = DigestDoBloco(bytes[inicio..]);
            return esperado is not null && esperado.AsSpan().SequenceEqual(digest);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"ToolSigning: verificar {caminho} falhou — {ex.Message}");
            return false;
        }
    }

    /// <summary>Extrai o digest do SpcIndirectDataContent do bloco (base64 → DER).</summary>
    private static byte[]? DigestDoBloco(ReadOnlySpan<byte> bloco)
    {
        var texto = Encoding.ASCII.GetString(bloco);
        var b64 = new StringBuilder();
        foreach (var linha in texto.Split('\n'))
        {
            var limpa = linha.Trim();
            // só as linhas de base64: "# <64 chars>" — os marcadores têm espaço e são ignorados
            if (limpa.Length <= 2 || limpa[0] != '#' || limpa[1] != ' ') continue;
            var corpo = limpa[2..].Trim();
            if (corpo.Length == 0) continue;
            if (!corpo.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')) continue;
            b64.Append(corpo);
        }
        if (b64.Length == 0) return null;

        var der = Convert.FromBase64String(b64.ToString());
        var reader = new AsnReader(der, AsnEncodingRules.BER);
        var contentInfo = reader.ReadSequence();
        contentInfo.ReadObjectIdentifier();                              // 1.2.840.113549.1.7.2
        var wrapper = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
        var signedData = wrapper.ReadSequence();
        signedData.ReadInteger();                                        // version
        signedData.ReadSetOf();                                          // digestAlgorithms
        var info = signedData.ReadSequence();                            // contentInfo do conteúdo
        info.ReadObjectIdentifier();
        var conteudo = info.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
        var spc = conteudo.ReadSequence();
        spc.ReadSequence();                                              // SpcAttributeTypeAndOptionalValue
        var digestInfo = spc.ReadSequence();
        digestInfo.ReadSequence();                                       // AlgorithmIdentifier
        return digestInfo.ReadOctetString();
    }

    // ------------------------------------------------------------------ orquestração

    /// <summary>
    /// Verifica e, quando cabível, assina os <c>tools\*.ps1</c> que mudaram. Devolve o estado final.
    /// Nunca lança: qualquer falha vira estado + log (a ferramenta mostra a mensagem).
    /// <paramref name="maquina"/> força o escopo de máquina (usado pelo instalador elevado, que é
    /// quem assina no modo todos os usuários).
    /// </summary>
    public static AssinaturaEstado Ensure(bool forcar = false, bool maquina = false)
    {
        var estado = new AssinaturaEstado { Pasta = PastaFerramentas };
        var (politica, deGpo, bloqueia) = Politica();
        estado.Politica = politica + (deGpo ? " (GPO)" : "");

        try
        {
            if (!Directory.Exists(PastaFerramentas))
            {
                estado.Status = "ausente";
                estado.Motivo = "pasta de ferramentas não encontrada: " + PastaFerramentas;
                SalvarEstado(estado);
                return estado;
            }

            var scripts = Directory.GetFiles(PastaFerramentas, "*.ps1");
            var gravavel = PodeEscrever(PastaFerramentas);

            if (bloqueia)
            {
                estado.Status = "politica_bloqueia";
                estado.Motivo = $"a política desta máquina proíbe scripts ({estado.Politica}); " +
                                "é preciso liberação do TI — assinar não resolve";
                estado.Arquivos.AddRange(scripts.Select(s => new AssinaturaArquivo
                {
                    Arquivo = Path.GetFileName(s),
                    Status = "bloqueado",
                    Motivo = "política Restricted"
                }));
                SalvarEstado(estado);
                return estado;
            }

            if (!gravavel && !maquina)
            {
                // modo todos-os-usuários: a pasta é da máquina, quem assina é o instalador elevado.
                // O app nunca eleva (app.manifest = asInvoker) e nunca assina com a chave de outro.
                estado.Escopo = "machine";
                estado.PodeAssinar = false;
                var daMaquina = LocalizarCertificado(StoreLocation.LocalMachine, exigirValidade: false);
                estado.Thumbprint = daMaquina?.Thumbprint ?? "";
                estado.Subject = daMaquina?.Subject ?? "";
                estado.ValidoAte = daMaquina?.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd") ?? "";
                estado.CriadoEm = daMaquina?.NotBefore.ToUniversalTime().ToString("yyyy-MM-dd") ?? "";

                foreach (var script in scripts)
                {
                    var ok = AssinaturaConfere(script);
                    estado.Arquivos.Add(new AssinaturaArquivo
                    {
                        Arquivo = Path.GetFileName(script),
                        Status = ok ? "ok" : "invalida",
                        Sha256 = HashArquivo(script),
                        Motivo = ok ? "" : "a assinatura das ferramentas precisa do instalador"
                    });
                }

                estado.Status = estado.Arquivos.All(a => a.Status == "ok") ? "ok" : "somente_leitura";
                estado.Motivo = estado.Status == "ok"
                    ? "scripts assinados pelo instalador (modo todos os usuários)"
                    : "a assinatura das ferramentas precisa do instalador (pasta da máquina)";
                SalvarEstado(estado);
                return estado;
            }

            // ---- assinatura: escopo do usuário (app) ou da máquina (instalador elevado)
            estado.Escopo = maquina ? "machine" : "user";
            estado.PodeAssinar = true;
            var local = maquina ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;

            var certificado = LocalizarCertificado(local);
            if (certificado is null)
            {
                certificado = CriarCertificado(maquina, out var erroCriacao);
                if (certificado is null)
                {
                    estado.Status = "invalida";
                    estado.Motivo = "não consegui criar o certificado de assinatura: " + erroCriacao;
                    SalvarEstado(estado);
                    return estado;
                }
            }

            estado.Thumbprint = certificado.Thumbprint;
            estado.Subject = certificado.Subject;
            estado.CriadoEm = certificado.NotBefore.ToUniversalTime().ToString("yyyy-MM-dd");
            estado.ValidoAte = certificado.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd");

            if (!certificado.HasPrivateKey)
            {
                estado.Status = "invalida";
                estado.Motivo = "o certificado existe no repositório mas a chave privada não está acessível";
                SalvarEstado(estado);
                return estado;
            }

            if (!Ancorar(certificado, maquina, out var erroAncora))
            {
                estado.Status = "invalida";
                estado.Motivo = "não consegui ancorar o certificado como confiável: " + erroAncora;
                SalvarEstado(estado);
                return estado;
            }

            var anterior = LerEstado();
            var assinados = 0;

            foreach (var script in scripts)
            {
                var nome = Path.GetFileName(script);
                var hashAtual = HashArquivo(script);
                var registro = anterior?.Arquivos.FirstOrDefault(a => a.Arquivo == nome);
                var confere = AssinaturaConfere(script);

                // já assinado por este certificado e conteúdo intacto: não reescreve o arquivo
                if (!forcar && confere && registro is not null && registro.Sha256 == hashAtual)
                {
                    estado.Arquivos.Add(new AssinaturaArquivo
                    {
                        Arquivo = nome, Status = "ok", Sha256 = hashAtual, Motivo = "já assinado"
                    });
                    continue;
                }

                if (Assinar(script, certificado, out var erro, out var hashNovo))
                {
                    assinados++;
                    estado.Arquivos.Add(new AssinaturaArquivo
                    {
                        Arquivo = nome, Status = "ok", Sha256 = hashNovo,
                        Motivo = confere ? "reassinado (hash mudou)" : "assinado"
                    });
                }
                else
                {
                    estado.Arquivos.Add(new AssinaturaArquivo
                    {
                        Arquivo = nome, Status = "invalida", Sha256 = hashAtual, Motivo = erro
                    });
                }
            }

            estado.Status = estado.Arquivos.All(a => a.Status == "ok") ? "ok" : "invalida";
            estado.Motivo = estado.Status == "ok"
                ? $"{scripts.Length} script(s) assinado(s)" + (assinados > 0 ? $" · {assinados} agora" : " · nenhum precisava")
                : "alguns scripts não puderam ser assinados";
            SalvarEstado(estado);
            return estado;
        }
        catch (Exception ex)
        {
            estado.Status = "invalida";
            estado.Motivo = ex.Message;
            AppPaths.Log("ToolSigning.Ensure: " + ex);
            SalvarEstado(estado);
            return estado;
        }
    }

    private static string HashArquivo(string caminho)
    {
        try { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(caminho))).ToLowerInvariant(); }
        catch { return ""; }
    }

    /// <summary>A pasta aceita escrita? É o que separa "só para mim" de "todos os usuários".</summary>
    public static bool PodeEscrever(string pasta)
    {
        try
        {
            var teste = Path.Combine(pasta, ".escrita-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var fs = new FileStream(teste, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                       FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch { return false; }
    }
}
