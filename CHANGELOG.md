# Changelog

Beamcast is a study project. See the README for the full notice.

## 2.1.4

- A transmissão mantém uma única resolução do início ao fim: a anunciada ao iniciar (ou a do
  preset escolhido durante a transmissão). Quadros de outro tamanho, como uma janela redimensionada
  ou a troca de tela para janela no meio da transmissão, entram com barras pretas em vez de
  recriar o encoder e mudar o formato para quem assiste. Corrige transmissões que se encerravam
  ao escolher uma janela.

## 2.1.3

- Captura de monitor migrada para DXGI Desktop Duplication: não passa pelo mecanismo da borda
  amarela do Windows, então a moldura não aparece em nenhuma versão do Windows 10/11, sem depender
  de consentimento. O cursor é desenhado no quadro pela GPU (GDI sobre a textura). Janelas seguem
  pela Windows.Graphics.Capture com o pedido "sem borda"; monitores em tela girada ou em outra placa
  caem nela também.
- Parar a transmissão agora encerra a captura: nada da tela é lido (e nenhuma borda desenhada)
  depois de "Parar". Para transmitir de novo, escolha a fonte outra vez.
- Pedido de captura sem borda feito de forma síncrona antes de cada sessão e registrado no diag.log.

## 2.1.2

- Borda amarela em volta da tela ou janela compartilhada removida. O Windows desenha essa moldura
  em toda captura e só aceita o pedido `IsBorderRequired = false` depois que o app obtém acesso
  "sem borda" (`GraphicsCaptureAccess.RequestAccessAsync(Borderless)`), que nunca era solicitado.
  Agora o pedido é feito ao abrir o app. Em Windows 10 anterior ao build 20348 a moldura é do
  sistema e não pode ser removida.
- Sumiu o tooltip "Esc" que aparecia ao parar o mouse em qualquer ponto da janela (dica automática
  do atalho de sair da tela cheia).

## 2.1.1

- Atualização automática consertada. Os releases anteriores só tinham o MSI; o Velopack descobre
  versões novas pelo `releases.win.json` e pelos pacotes `.nupkg`, que nunca eram publicados, então
  nenhuma instalação recebia atualização. Agora o `scripts/pack.ps1` baixa o release anterior,
  gera pacote completo e delta, e publica feed + pacotes + Setup.exe + MSI com `vpk upload github`.
- Biblioteca Velopack alinhada à CLI (1.2.0). Serviço de update com log em `diag.log`, uma única
  instância do gerenciador, progresso do download na janela e retomada de atualização já baixada
  ("Reiniciar e concluir"). Notas de versão no release mostram só a seção da versão.
- Instalações 2.0.x/2.1.0 já enxergam este feed e se atualizam sozinhas; daqui em diante toda
  versão chega automaticamente.

## 2.1.0

- Hosts e salas: o app guarda uma lista de hosts (favoritáveis); ao selecionar um, mostra as
  salas públicas dele (`GET /rooms`), as salas favoritas e o campo de código/convite.
- Salas com nome, visibilidade (pública/privada), duração (permanente/temporária com TTL após
  ficar vazia), senha opcional, política de transmissão (todos ou só o dono) e limite de pessoas.
  Salas privadas usam códigos de 10 caracteres e não aparecem em lista.
- Dono da sala: token gerado na criação (o host guarda só o hash; o app guarda com DPAPI).
  O dono edita a sala, troca ou remove a senha, gera convites com validade (1 h/24 h/7 d/sem
  prazo) e número de usos, revoga convites, expulsa membros e apaga a sala.
- Salas sem senha continuam cifradas de ponta a ponta: a chave é aleatória e um membro a entrega
  a quem entra por ECDH P-256 + HKDF + AES-256-GCM; o host só repassa o pacote.
- Convites `BC-` v3 carregam host, código, token e (em salas com senha) a chave: quem tem o link
  entra sem digitar a senha enquanto ele valer.
- Reconexão automática: se a conexão cair, o app tenta voltar por até 5 minutos com espera
  crescente, republica a transmissão em andamento e retoma o que estava sendo assistido.
- Favoritos: estrela em hosts e salas; senha da sala pode ser lembrada (DPAPI).
- Segurança: limite de tentativas por endereço (5/10 min) e por sala (30/10 min) para senha,
  convite e código; IP real lido de `CF-Connecting-IP`/`X-Forwarded-For` atrás de proxy.
- Servidor: `rooms.json` (migra `lounges.json` da 2.0), `BEAMCAST_HOST_NAME`, varredura de salas
  temporárias a cada minuto. Protocolo v3: apps 2.0.x não conectam.

## 2.0.1

- Heartbeat entre app e servidor: quem some sem avisar (queda de rede, app fechado à força) é
  removido do salão em até 30 s mesmo atrás de proxy/túnel, e a transmissão dele é encerrada
  para os outros.

## 2.0.0

- Modelo de salão (`Beamcast.Server`): qualquer pessoa sobe o servidor com `docker compose up -d --build`
  a partir do repositório; no app, endereço do servidor + nome + criar salão (nome e senha
  obrigatória) ou entrar (código e senha). Sem Actions nem registry para o servidor.
- Dentro do salão: lista de membros e de transmissões; qualquer membro transmite, várias
  transmissões ao mesmo tempo, cada um escolhe o que assistir e pode parar sem sair.
- Segurança: PBKDF2 (200k) → chave do salão; o servidor guarda só um verificador HMAC e checa
  a entrada por desafio/resposta; nomes, títulos, vídeo e áudio em AES-256-GCM (HKDF da chave).
  O servidor nunca vê senha nem conteúdo. Salões persistem em volume; TTL opcional.
- Áudio: loopback por processo (ActivateAudioInterfaceAsync + Process_Loopback) como Discord/Teams;
  ao compartilhar janela captura só o app, ao compartilhar a tela captura tudo menos apps de voz
  (Discord, Teams, Zoom, Slack, WhatsApp…) e o próprio Beamcast; mixer de 20 ms; Opus 128 kbps;
  player com ocultação de perda; volume e mudo no espectador.
- Configurações de transmissão (qualidade, fps, bitrate, codec, cursor, áudio, título) agora
  ficam na aba Transmitir da sala.
- Removidos: modo direto TCP, relay antigo (`Beamcast.Relay`), convite com segredo por sessão.
  Protocolo v2 do salão: versões 1.x não conectam.

## 1.1.0

- Relay (`Beamcast.Relay`): transmissão pela internet sem abrir porta; salas por código, chave do
  app, fan-out com controle de fila por espectador no servidor.
- Criptografia de ponta a ponta (AES-256-GCM) com segredo por sessão dentro do código de convite,
  nos dois modos (relay e direto). Protocolo de rede v2: versões anteriores não conectam.
- Página Transmitir: escolha entre servidor e direto; código de convite gerado ao iniciar.
- Configurações: endereço do servidor e chave do app.
- Protocolo preparado para áudio (Opus) e captura por processo (base do próximo passo).

## 1.0.0

- First study build: broadcast a monitor or window to anyone with the invite code.
- GPU pipeline: Windows.Graphics.Capture stays on the GPU, the D3D11 video processor converts to
  NV12, and the hardware H.264/HEVC encoder (Media Foundation: AMD, NVIDIA, Intel, D3D12) encodes
  with a low-latency profile. Viewers decode with DXVA straight into a SwapChainPanel.
- VP8 on the CPU as a fallback for machines without a hardware encoder.
- TCP transport with per-viewer back-pressure and keyframe recovery; encoder is recreated when a
  driver ignores the keyframe request.
- Password-protected rooms with challenge/response (the password never leaves the machine).
- Quality presets up to Source/2160p, 15–120 fps, bitrate and cursor controls, codec selector.
- Live preview, pause/resume, viewer list, and stream stats on both ends.
- Viewer fullscreen (double-click or the button, Esc to leave).
- Study-only notice on first launch and on the About page.
- Automatic updates through Velopack and GitHub Releases.
- pt-BR and English UI.
