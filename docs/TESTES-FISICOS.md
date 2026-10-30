# Roteiro de testes físicos (Windows + caixas Bluetooth)

Estes testes **não** podem ser automatizados no CI: exigem o seu PC, o adaptador Bluetooth e as caixas.
Anote o resultado de cada item. Em caso de falha, anexe `%APPDATA%\MultiSound\multisound.log`.

Ambiente: Windows __ (Configurações → Sistema → Sobre → build: _____), adaptador Bluetooth: _______,
caixas: Harman Kardon Go + Play 3, MIFA A90.

## 0. Diagnóstico rápido

```bash
multisound-cli diag
```

(ou `dotnet run --project tools/MultiSound.Cli -- diag`) — lista os dispositivos e diz se a captura
"todos os apps" funciona neste Windows. Com uma música tocando, os bytes devem ser > 0.

## 1. Enumeração
- [ ] As duas caixas aparecem como "Conectado"/"Disponível", com a etiqueta **Bluetooth**.
- [ ] Se aparecer uma entrada "Hands-Free"/"Fone de ouvido", ela tem a etiqueta **Viva-voz**.
- [ ] Desligar uma caixa → ela passa a "Desconectado" em poucos segundos; religar → volta.

## 2. Reprodução simultânea (modo "Todos os aplicativos")
Saída padrão do Windows: alto-falantes do notebook, no mudo.
- [ ] Marcar as duas caixas → INICIAR → tocar Spotify: som nas duas caixas.
- [ ] YouTube no navegador: som nas duas.
- [ ] VLC (ou outro player): som nas duas.
- [ ] Medidor "Origem" se mexe com o notebook no mudo.
- [ ] Nenhum som duplicado nos alto-falantes do notebook (estão no mudo).
- [ ] Pausar a música por 1 minuto e voltar: as caixas voltam a tocar sem intervenção.

## 3. Sincronização
- [ ] TESTAR SINCRONIZAÇÃO: bipes nas duas caixas.
- [ ] Ajustar o atraso da caixa adiantada até ouvir um bipe só. Valor encontrado: ____ ms.
- [ ] Ajustes durante a reprodução não geram estalos.
- [ ] Após 30 minutos tocando, ainda sem eco perceptível (drift compensado).
- [ ] Coluna "drift" estabiliza (valores típicos: algumas dezenas a poucas centenas de ppm).

## 4. Desconexão / reconexão
- [ ] Desligar a MIFA durante a música → status "Reconectando…", a Harman continua tocando.
- [ ] Religar a MIFA → volta a tocar sozinha (status "Reproduzindo"), com o mesmo atraso.
- [ ] Repetir com a Harman.

## 5. Bandeja e ciclo de vida
- [ ] Fechar a janela (X) → continua tocando; ícone na bandeja.
- [ ] Clique no ícone → janela volta. Menu → Parar/Iniciar funciona.
- [ ] Menu → Sair → o processo `MultiSound.exe` some do Gerenciador de Tarefas e as caixas param.
- [ ] Abrir o app duas vezes → a segunda apenas traz a janela da primeira.
- [ ] "Iniciar com o Windows" + "Iniciar reprodução automaticamente" → após reiniciar o PC, toca nas caixas salvas.

## 6. Modo loopback (fallback)
Saída padrão = HDMI do monitor (ou outro dispositivo não usado), volume 100%.
- [ ] Modo "Loopback de um dispositivo de saída" → origem "Saída padrão do Windows": som nas caixas.
- [ ] Marcar a própria saída padrão → aparece como "Ignorado" (sem eco/realimentação).

## 7. Desempenho
- [ ] Gerenciador de Tarefas: CPU do MultiSound < 3 % e memória < 120 MB com duas caixas.
- [ ] Coluna "falhas" (underruns) estável durante 10 minutos. Se aumentar, subir o buffer de segurança.

## 8. Três ou mais saídas (opcional)
- [ ] Adicionar uma terceira saída (ex.: fone P2 ou caixa USB). Anotar se o Bluetooth picota.
