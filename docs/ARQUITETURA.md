# Arquitetura e pesquisa de viabilidade

## 1. Pesquisa (Etapa 1)

### APIs de áudio do Windows

| Necessidade | API escolhida | Observações |
|---|---|---|
| Enumerar saídas e saber quando conectam/desconectam | `IMMDeviceEnumerator` + `IMMNotificationClient` (via NAudio) | Ids de endpoint são estáveis entre reconexões Bluetooth, o que permite reconhecer as caixas salvas. |
| Capturar o áudio do sistema | **WASAPI process loopback** (`ActivateAudioInterfaceAsync` com `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, modo `EXCLUDE_TARGET_PROCESS_TREE` com o PID do próprio app) | Captura todos os apps exceto o MultiSound ⇒ sem realimentação por construção. Implementado com interop próprio (o NAudio 2.2.1 não expõe esse modo). |
| Captura alternativa | **WASAPI loopback** de um endpoint (`AUDCLNT_STREAMFLAGS_LOOPBACK`) | Disponível desde o Vista; usado como fallback. Pós-volume do endpoint e não entrega pacotes quando nada toca. |
| Tocar em cada dispositivo | **WASAPI modo compartilhado, orientado a eventos** (`WasapiOut` do NAudio), um por dispositivo, no formato de mixagem do próprio dispositivo | Sem modo exclusivo (não "rouba" a caixa de outros apps). |

Alternativas descartadas:
- *Driver virtual* (VB-Cable, Voicemeeter, etc.): exigiria instalar driver de terceiros — pedido explícito para evitar.
- *"Ouvir este dispositivo"* do Windows: não sincroniza nem permite atraso por saída.
- *WinRT `AudioGraph`*: funciona, mas adiciona camadas sem ganho para este caso e tem menos controle de buffer.

### Bluetooth

- Cada caixa A2DP conectada vira um endpoint de renderização independente; o Windows 10/11 permite
  várias conexões A2DP simultâneas no mesmo adaptador, **mas não garante desempenho**: o rádio é
  único (o Windows usa só um adaptador Bluetooth por vez) e a banda é dividida entre as caixas e
  demais periféricos; coexistência com Wi-Fi 2,4 GHz piora. Isso não é contornável por software.
- Cada endpoint Bluetooth tem **seu próprio relógio de consumo**; a captura tem outro. Sem
  compensação, os buffers cresceriam ou esvaziariam lentamente (drift de dezenas a centenas de ppm).
- A **latência interna** da caixa (decodificação + buffer do receptor, 100–300 ms) não é reportada
  ao Windows de forma confiável; por isso a compensação entre caixas é manual (atraso por saída).
- Caixas com microfone expõem também um endpoint **Hands-Free (HFP)** mono 8/16 kHz; abri-lo força a
  conexão para HFP e destrói a qualidade. O app detecta e avisa.

## 2. Fluxo de dados

```
Apps (Spotify, YouTube, …)
        │  mixagem do Windows
        ▼
 Captura (thread própria, MMCSS "Pro Audio")
   ProcessLoopbackCapture  ─ ou ─  EndpointLoopbackCapture
        │ bytes no formato da captura
        ▼
 SampleConverter → estéreo float32           CaptureGapFiller: injeta silêncio quando
        │                                    nenhum app toca (mantém o relógio contínuo)
        │ (sinal de teste substitui o áudio quando ativo)
        ├──────────────────────┬──────────────────────┐
        ▼                      ▼                      ▼
 OutputPipeline A        OutputPipeline B        … (N saídas)
  ring buffer próprio     ring buffer próprio
  atraso alvo = buffer de segurança + atraso da saída
  DriftController (PI) → razão de reamostragem
  StereoResampler (sinc Kaiser 32 taps, razão variável)
  volume/mudo com rampa, fades em saltos
        │                      │
        ▼                      ▼
 WasapiOut (compartilhado,  WasapiOut …
 formato de mixagem do dispositivo, thread do NAudio)
```

### Threads

- **Thread de controle (MTA)** — `AudioThread`: todos os objetos COM de controle (enumerador,
  dispositivos, abertura/fechamento de saídas, reconexão). Evita problemas de apartamento COM com a
  thread STA da interface WPF e serializa as operações.
- **Thread de captura**: converte e escreve em cada pipeline (cópias curtas sob lock).
- **Uma thread de reprodução por saída** (NAudio): lê do pipeline. Exceções nunca escapam do
  callback (o adaptador devolve silêncio e registra o erro).
- **Interface**: consulta um instantâneo do motor 4×/s apenas enquanto toca.

## 3. Sincronização

- **Atraso alvo por saída** = buffer de segurança (padrão 60 ms, comum a todas) + atraso do usuário.
  Diferenças de atraso entre saídas são exatas (verificado em teste: ±6 ms, com 44,1/48 kHz e
  drift simulado).
- **Drift**: o nível do buffer é medido no meio de cada bloco (independe do período do dispositivo),
  suavizado (constante de tempo 1 s) e alimenta um controlador PI (Kp = 0,1 s⁻¹, Ki = 0,005 s⁻²,
  ζ ≈ 0,7) que ajusta a razão do reamostrador em no máximo ±2000 ppm (≈ 3,5 cents, inaudível).
  Testado com relógios de −400 a +1200 ppm: erro final < 1 ms, zero underruns.
- **Saltos**: mudança de atraso pelo usuário (ou erro > 60 ms por 0,5 s) ⇒ *fade-out* de 8 ms,
  reposicionamento do buffer, *fade-in*. Sem estalos (verificado: variação máxima entre amostras
  consecutivas < 0,05 numa senoide de 0,8).
- **Underrun/overflow**: contados por saída e exibidos. Underrun leva ao estado de preenchimento
  (silêncio até atingir o alvo). Overflow descarta as amostras mais antigas (latência limitada).
  Silêncio da captura (nenhum app tocando) não conta como falha.

## 4. Prevenção de realimentação e duplicação (`RoutingPlanner`)

| Situação | Decisão |
|---|---|
| Modo loopback e a saída é o dispositivo capturado | Não toca (realimentação) |
| Modo "todos os apps" e a saída é a saída padrão do Windows | Não toca (os apps já tocam ali; tocaria duas vezes) |
| Mesmo dispositivo listado duas vezes | Apenas uma saída |
| Segunda instância do app | Bloqueada (mutex); apenas traz a janela existente para frente |

## 5. Reconexão

- `IMMNotificationClient` avisa mudança de estado → tentativa imediata de reabrir a saída.
- Falha de reprodução (`AUDCLNT_E_DEVICE_INVALIDATED` etc.) → saída fechada e marcada "Reconectando…".
- Verificação a cada 3 s para saídas pendentes e para a captura.
- Mudança da saída padrão do Windows → replanejamento (e reinício da captura quando ela segue a saída padrão).

## 6. Encerramento limpo

`Sair` (bandeja) ou fechar com a opção desmarcada: para todas as saídas (`WasapiOut.Stop/Dispose`,
que aguarda as threads), para a captura (aguarda a thread e libera os objetos COM), remove o
registro de notificações, encerra a thread de controle e salva as configurações. Nenhuma thread em
primeiro plano é criada, então o processo não fica "pendurado".
