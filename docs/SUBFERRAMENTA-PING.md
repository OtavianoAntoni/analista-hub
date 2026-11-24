# Subferramenta — ping do destino (NET-001)

Componente reutilizável: qualquer ferramenta do HUB que peça um **IP** pode pedir, no manifesto, que o
destino seja pingado enquanto o analista digita. Primeiro consumidor: `:mkt` (SN da RB pelo IP).

Fonte no código: `src/AnalistaHub/Services/IpPing.cs` (`IpTarget`, `PingSession`, `PingCoordinator`).
Regra permanente: `docs/REGRAS-DE-FERRAMENTA.md` **R14**. Gate: `--check-tools`.

---

## 1. Declaração no manifesto (como outra ferramenta invoca)

```json
"inputs": [
  { "key": "ip", "label": "IP da RB", "type": "ip", "ping": true }
]
```

- `ping: true` só é aceito em campo `type: "ip"` — o gate recusa o contrário.
- Ferramenta sem `ping` não pinga nada: a subferramenta é opcional e declarativa.

## 2. Comportamento

| Etapa | Regra |
|---|---|
| Gatilho | o campo contém um IPv4 completo e válido (4 octetos). CIDR é aceito: `10.0.0.1/32` pinga `10.0.0.1` |
| Espera | **400 ms** de debounce depois da última tecla (`PingDebounceMs`, configurável em Configurações) |
| Edição | qualquer alteração no texto — em qualquer octeto — **cancela** o ping em andamento e reagenda o debounce |
| Pacote | 1 eco ICMP por disparo, timeout **1 s** (`PingTimeoutMs`, configurável) |
| Exibição na HUD | **1 linha** ao lado do campo: latência (`1 ms`) ou `sem resposta` / `destino inalcançável`, com **opacidade 75%** |
| Resultado | o aviso de resultado da ferramenta mostra os **2 últimos retornos** de ping do endereço consultado |
| Trava | máximo **100 disparos por invocação**; ao estourar, para de pingar e avisa. Contador reiniciado a cada abertura da ferramenta. Um disparo cancelado por edição conta (foi uma tentativa) |
| IPv6 | **fora do escopo desta versão**: o campo continua IPv4 e um endereço IPv6 mostra «IPv6 ainda fora do escopo do ping do HUB» |

### Estados exibidos

| Texto | Quando | Cor |
|---|---|---|
| `3 ms` | eco respondido | `UiKit.Ok` |
| `sem resposta` | timeout do pacote | `UiKit.Bad` |
| `destino inalcançável` | host/rede/proibido inalcançável | `UiKit.Bad` |
| `ping indisponível: …` | falha local (socket, pilha ICMP) | `UiKit.Bad` |
| `limite de 100 pings` | trava atingida; a mensagem completa vai para o rodapé | `UiKit.Warn` |

## 3. Sem processo filho (requisito de cancelamento)

O ping usa `System.Net.NetworkInformation.Ping` (ICMP via pilha do Windows), **não** `ping.exe`.
Consequências, que são o motivo da escolha:

- cancelar é cancelar o `CancellationToken` — não existe processo para matar nem órfão a limpar;
- 1 disparo = 1 eco, com latência medida pelo próprio Windows;
- não exige administrador (ICMP echo é liberado para usuário comum; socket bruto não seria).

## 4. API (para a próxima ferramenta que quiser pingar)

```csharp
// 1. classificar o texto digitado
IpTextState estado = IpTarget.Classify(texto, out var ip, out var problema);

// 2. sessão: orçamento de 100 disparos, timeout por pacote
var sessao = new PingSession(timeoutMs);
PingSample amostra = await sessao.ProbeAsync("10.0.0.1", ct);

// 3. coordenação de digitação: debounce + cancelamento
var coord = new PingCoordinator(sessao, debounceMs, amostra => MostrarNaTela(amostra));
campo.TextChanged += (_, _) => coord.Edit(campo.Text);
coord.Dispose();   // ao fechar a tela
```

`PingSession.Summary(endereco, 2)` devolve `"3 ms · 4 ms"` — é o texto que o aviso de resultado usa.

## 5. Não-objetivos

- Não faz traceroute, não mede perda, não escolhe rota.
- Não pinga sem declaração no manifesto (nada de ping automático em todo campo de IP).
- Não guarda histórico em disco: os retornos vivem na memória da invocação.
- Não substitui a bolinha de status do serviço (R5), que continua medindo TCP/credencial.

## 6. Como isso é provado

| Prova | Comando |
|---|---|
| ICMP real, latência e timeout | `AnalistaHub.exe --ping 10.0.0.1 --count 3` |
| Debounce, cancelamento por edição e trava de 100 | `AnalistaHub.exe --ping-sim --debounce 400` |
| Sem processo órfão | `tasklist \| findstr /i ping.exe` depois de cancelar no HUD |
| Regra do manifesto | `AnalistaHub.exe --check-tools` |
