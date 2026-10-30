# MultiSound

**Reproduza o áudio do Windows em vários dispositivos ao mesmo tempo** — por exemplo, uma
Harman Kardon Go + Play 3 e uma MIFA A90 tocando a mesma música do Spotify, do YouTube, do VLC
ou de qualquer outro programa, sem aplicativos das fabricantes.

- Aplicativo nativo para Windows 10 e 11 (C# / .NET 8 / WPF), sem Electron, sem navegador embutido.
- Captura o áudio do sistema via **WASAPI** e envia um fluxo independente para cada dispositivo.
- Atraso (delay) e volume ajustáveis por dispositivo, **durante a reprodução**.
- **Sincronização automática** pelo microfone do notebook (ou da webcam).
- Correção automática e gradual de *drift* entre os relógios dos dispositivos.
- Reconexão automática quando uma caixa desconecta e volta.
- Ícone na bandeja, inicia minimizado, lembra os dispositivos, tema escuro, interface em português.
- Funciona 100% offline. Sem cadastro, sem assinatura, sem drivers virtuais.

> ⚠️ Este projeto foi desenvolvido e testado automaticamente sem acesso a caixas Bluetooth reais.
> Os componentes de processamento (buffer, conversão, reamostragem, sincronização) têm testes
> automatizados; a interface é testada em um Windows real no CI. **O funcionamento com as suas
> caixas precisa ser confirmado no seu PC** — veja [docs/TESTES-FISICOS.md](docs/TESTES-FISICOS.md).

---

## 1. Instalação

1. Baixe `MultiSound-win-x64.zip` (aba **Actions → build → Artifacts**, ou **Releases** quando houver uma tag).
2. Extraia e execute `MultiSound.exe`. Não precisa instalar nada (o .NET já vai dentro do executável).
   - O Windows SmartScreen pode avisar que o app não é assinado: clique em **Mais informações → Executar assim mesmo**.
   - Existe também uma versão leve (`MultiSound-win-x64-requer-dotnet.zip`), que exige o
     [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) instalado.
3. As configurações ficam em `%APPDATA%\MultiSound\settings.json` e o registro de eventos em
   `%APPDATA%\MultiSound\multisound.log`. Para desinstalar, apague o executável e essa pasta.

## 2. Uso rápido

1. **Pareie e conecte as caixas** em *Configurações do Windows → Bluetooth e dispositivos*.
   Cada caixa aparece como um dispositivo de saída de áudio.
2. **Escolha a saída padrão do Windows** (ícone de volume da barra de tarefas) — veja a seção 3.
   Recomendado: **alto-falantes do notebook (ou outro dispositivo que não está na lista), no mudo**.
3. Abra o MultiSound e marque as caixas (ex.: Harman Kardon e MIFA A90).
4. Clique em **INICIAR REPRODUÇÃO**.
5. Toque qualquer coisa no Spotify, YouTube, VLC… O áudio sai nas caixas selecionadas.
6. Se ouvir eco entre as caixas, ajuste o **Atraso** (seção 4).
7. Pode minimizar ou fechar a janela: o MultiSound continua na bandeja (perto do relógio).

## 3. De onde vem o áudio (e como evitar eco/realimentação)

O MultiSound captura o áudio que os aplicativos enviam ao Windows e o redistribui. Há dois modos:

### Modo "Todos os aplicativos" (recomendado)

Captura o áudio de **todos os processos, exceto o próprio MultiSound** (API de *process loopback*
do Windows). Como o áudio que o MultiSound envia às caixas fica fora da captura, **não existe
caminho de realimentação**, qualquer que seja a configuração.

- Os apps continuam tocando também na **saída padrão do Windows**. Por isso:
  - Escolha como saída padrão um dispositivo que **não** esteja marcado no MultiSound
    (ex.: alto-falantes do notebook) e **coloque-o no mudo**. Neste modo a captura em geral não é
    afetada pelo mudo — confirme no medidor **Origem** do app.
  - Se a saída padrão for uma das caixas marcadas, o MultiSound **não toca nela de novo** (evita som
    duplicado) e mostra "Ignorado": ela recebe o áudio direto do Windows, sem atraso ajustável — o
    que costuma deixar as outras caixas atrasadas em relação a ela.
- Disponibilidade: documentado pela Microsoft a partir do build 20348 (Windows 11). Em Windows 10
  2004 ou mais recente costuma funcionar. **Se o Windows recusar, o app avisa e passa
  automaticamente para o modo de loopback** abaixo.

### Modo "Loopback de um dispositivo de saída"

Captura a mixagem enviada a um dispositivo específico (por padrão, a saída padrão do Windows).

- Esse dispositivo de origem **nunca** recebe o áudio de volta (o app o marca como "Ignorado":
  seria realimentação infinita).
- O volume/mudo do dispositivo de origem **afeta a captura** (é assim que o loopback do Windows
  funciona). Use como origem um dispositivo que esteja ativo mas que você não esteja ouvindo — por
  exemplo uma saída HDMI de monitor sem caixas — com volume em 100%.
- Uma saída P2 de fone sem nada plugado geralmente aparece como "desconectada" e não serve como origem.

## 4. Sincronização das caixas

Cada caixa Bluetooth tem latência própria (normalmente entre 100 e 300 ms, variando por modelo,
codec e até a cada reconexão). O Windows não informa esse atraso interno, então ele é **medido com
um microfone** ou ajustado de ouvido.

Pré-requisito: todas as caixas devem estar com status **Reproduzindo**. Se uma aparecer como
**Ignorado**, ela é a saída padrão do Windows — veja a seção 3.

### Automática (recomendada): SINCRONIZAR COM O MICROFONE

1. Com a reprodução ativa, deixe o notebook mais ou menos **à mesma distância** das caixas e faça silêncio.
2. Clique em **SINCRONIZAR COM O MICROFONE**. Cada caixa toca um som de varredura (*chirp*), uma de
   cada vez, três rodadas (~15 s). O áudio dos apps fica em silêncio durante a medição.
3. O MultiSound grava pelo microfone do notebook/webcam, localiza cada chirp por correlação cruzada,
   calcula quanto cada caixa chega depois da outra e **aplica os atrasos**.

Por que o microfone do notebook e não o das caixas: abrir o microfone de uma caixa Bluetooth troca o
perfil dela de estéreo (A2DP) para viva-voz (HFP) — a latência medida não seria a da música. Por isso
o app **recusa microfones Bluetooth** e usa o microfone padrão do Windows se ele não for Bluetooth
(senão, o primeiro microfone não Bluetooth disponível). Como uma única gravação ouve todas as caixas, o
atraso do próprio microfone se cancela.

Precisão: em teste simulado (eco da sala, ruído, microfone a 16–48 kHz) o erro é menor que 1 ms. Na
prática, a distância conta: cada 34 cm de diferença entre as caixas e o notebook somam ~1 ms. Se o
resultado vier com aviso de instabilidade, repita mais perto e em silêncio.

### Manual: TESTAR SINCRONIZAÇÃO

1. Clique em **TESTAR SINCRONIZAÇÃO**: todas as caixas tocam um bipe por segundo.
2. Fique entre as caixas. Se ouvir dois bipes, descubra qual toca **antes** (use **Mudo** para ouvir
   uma de cada vez) e aumente o **Atraso** dela até ouvir um único bipe.
3. Clique em **ENCERRAR TESTE**. Os atrasos são salvos.

Exemplo: `Harman Kardon: 0 ms` · `MIFA A90: +120 ms`.

Depois de sincronizadas, o app mantém o alinhamento sozinho: cada saída tem um controlador que
corrige a diferença de relógio (drift) ajustando a taxa de reamostragem em até ±0,2 % — algo
inaudível — e um salto curto com *fade* (sem estalo) quando o erro passa de 60 ms.

## 5. Configurações

Em **Configurações** (no fim da janela):

| Opção | Efeito |
|---|---|
| Iniciar minimizado na bandeja | abre só com o ícone da bandeja |
| Iniciar com o Windows | registra em `HKCU\...\Run` (sem administrador) |
| Iniciar a reprodução automaticamente | ao abrir, já toca nas caixas salvas |
| Fechar a janela mantém na bandeja | o X apenas esconde; use **Sair** no menu da bandeja para encerrar |
| Buffer de segurança (padrão 60 ms) | aumente se ouvir falhas/estalos; diminua para menos atraso |
| Latência por saída (padrão 40 ms) | buffer WASAPI de cada dispositivo; aumente se um dispositivo falhar |

## 6. Limitações técnicas (leia)

- **Bluetooth no Windows**: o Windows usa **um único rádio Bluetooth por vez** — colocar um segundo
  adaptador USB não adiciona capacidade. Windows 10/11 aceita várias caixas A2DP conectadas ao
  mesmo tempo (cada uma vira um dispositivo de saída), mas a largura de banda do rádio é
  compartilhada (inclusive com Wi-Fi 2,4 GHz e mouse/teclado Bluetooth). Com adaptadores antigos ou
  baratos, duas caixas podem picotar e três ou mais podem não funcionar. Isso é limitação de
  hardware/driver; o MultiSound não tem como contornar por software.
  - Alternativa de hardware confiável: um **transmissor Bluetooth USB** que aparece no Windows como
    placa de som USB (não usa a pilha Bluetooth do Windows). O MultiSound trata como qualquer saída.
- **Perfil "viva-voz" (Hands-Free/HFP)**: algumas caixas aparecem duas vezes. Use sempre o
  dispositivo "Estéreo"/"Stereo"/"Alto-falantes"; o "Hands-Free" é mono, 8–16 kHz, e abri-lo derruba
  a qualidade da caixa. O app marca esses dispositivos como **Viva-voz**.
- **Sincronização perfeita não é garantida**: a latência interna das caixas pode variar alguns
  milissegundos ao longo do tempo e muda a cada reconexão. Reajuste o atraso se perceber eco.
- **Atraso em relação ao vídeo**: o caminho captura → buffer → Bluetooth soma ~60–100 ms ao atraso
  natural do Bluetooth. Em vídeos, a voz pode parecer levemente atrasada.
- **Conteúdo protegido**: alguns apps com DRM bloqueiam a captura do seu áudio pelo Windows.
- **Modo exclusivo**: se outro programa abrir uma caixa em modo exclusivo, o MultiSound não consegue
  usá-la (mensagem "em uso exclusivo"). Desmarque "Permitir que aplicativos assumam o controle
  exclusivo" nas propriedades do dispositivo.
- **Medição automática**: precisa de um microfone que não seja Bluetooth (notebook, webcam, USB) e de
  silêncio. Ela mede o atraso **no ponto onde está o microfone**; se você ouve de outro lugar da sala,
  diferenças de distância às caixas alteram um pouco o resultado (~3 ms por metro).
  Se a privacidade do Windows bloquear o microfone para apps da área de trabalho, o app avisa.

## 7. Solução de problemas

| Sintoma | O que fazer |
|---|---|
| Caixa "Reconectando…" | Verifique se ela está conectada no Windows; o app tenta de novo a cada 3 s e ao detectar a reconexão. |
| Som picotando em uma caixa | Aumente o buffer de segurança e a latência por saída; aproxime as caixas do PC; desligue outros dispositivos Bluetooth; prefira Wi-Fi 5 GHz. |
| Eco/duas batidas | Use SINCRONIZAR COM O MICROFONE (ou o teste de bipes). |
| Medição "não ouviu" uma caixa | Ela pode estar no mudo, com volume baixo, longe, ou o microfone bloqueado em Privacidade → Microfone. |
| Medidor "Origem" parado com música tocando | No modo loopback, a origem está no mudo/volume baixo ou não é a saída onde o app toca. No modo "todos os apps", confira se o app de música não está mudo no Mixer de Volume. |
| "Ignorado" em uma caixa | Ela é a origem da captura ou a saída padrão do Windows; veja a seção 3. |
| Qualquer outro problema | **Configurações → Abrir pasta de registros** e veja `multisound.log`. |

## 8. Compilar a partir do código

Requisitos: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Compila em Windows,
Linux ou macOS (a interface só executa no Windows).

```bash
dotnet test tests/MultiSound.Core.Tests
```

```bash
dotnet publish src/MultiSound.App -c Release -r win-x64 -o dist
```

O resultado é `dist/MultiSound.exe` (autocontido, x64). O CI do GitHub (`.github/workflows/build.yml`)
faz o mesmo em `windows-latest`, roda um teste de fumaça da interface e publica os `.zip` como
artefatos (e como Release ao criar uma tag `v*`).

Protótipo de linha de comando (útil para diagnóstico):

```bash
dotnet run --project tools/MultiSound.Cli -- listar
```

```bash
dotnet run --project tools/MultiSound.Cli -- tocar 1 2 --atraso 2=120
```

```bash
dotnet run --project tools/MultiSound.Cli -- tocar 1 2 --medir
```

```bash
dotnet run --project tools/MultiSound.Cli -- diag
```

## 9. Testes

- **Automatizados** (`tests/MultiSound.Core.Tests`, rodam em qualquer SO): buffer circular (inclusive
  concorrência), conversão de formatos (float/16/24/32 bits, mono/estéreo/5.1/7.1), reamostrador
  (fidelidade de senoides, SNR > 80 dB, razão variável), controlador de drift (convergência com
  ±1200 ppm), pipeline completo com relógios simulados (nível do buffer, ausência de underruns,
  atraso relativo entre saídas = atraso configurado ±6 ms, troca de atraso sem descontinuidade,
  silêncio da captura), medição pelo microfone (gravação simulada com eco, ruído e latência de
  microfone desconhecida: erro < 1 ms; ruído puro não gera falsa detecção), cálculo dos atrasos,
  roteamento anti-realimentação, configurações JSON.
- **Teste de fumaça da interface** (CI em Windows): abre o app, inicializa o áudio, renderiza a
  janela e salva uma captura de tela.
- **Testes físicos** (precisam do seu PC e das caixas): roteiro em
  [docs/TESTES-FISICOS.md](docs/TESTES-FISICOS.md).

## 10. Estrutura

```
src/MultiSound.Core     Processamento puro (sem Windows): buffers, conversão, reamostrador,
                        controle de drift, pipeline por saída, sinal de teste, configurações
src/MultiSound.Audio    WASAPI: captura (por processo e loopback), saídas, dispositivos, motor
src/MultiSound.App      Interface WPF, bandeja, instância única, inicialização com o Windows
tools/MultiSound.Cli    Protótipo/diagnóstico em linha de comando
tests/                  Testes automatizados do núcleo
docs/                   Pesquisa/arquitetura e roteiro de testes físicos
```

Detalhes técnicos e decisões: [docs/ARQUITETURA.md](docs/ARQUITETURA.md).

## Créditos

- [NAudio](https://github.com/naudio/NAudio) (licença MIT) — acesso a WASAPI/Core Audio.
- Captura por processo baseada na amostra *ApplicationLoopback* da Microsoft.
