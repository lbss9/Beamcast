# Changelog

Beamcast is a study project. See the README for the full notice.

## 2.1.10

- `UpdateWindow.xaml`: o `StatusText` ficava num `StackPanel` horizontal, que mede o filho com
  largura infinita, então `TextWrapping` nunca quebrava e a coluna `*` cortava o texto ao lado
  dos botões (janela de 520 px). Status agora fica num `Grid` próprio (spinner Auto + texto `*`)
  com largura inteira; botões numa linha abaixo, alinhados à direita. O `ProgressRing` some
  (`Visibility`) quando inativo, em vez de deixar um recuo vazio.

## 2.1.9

- `Services/RoomManagement.cs`: sessão curta de dono para editar/apagar sem entrar na sala.
  `OpenAsync` faz `LoungeClient.JoinAsync` com `OwnerToken` e `ManageOnly = true` (salas com
  senha continuam exigindo a senha: a chave nasce dela); `UpdateAsync` manda `RoomUpdate` (+
  `ChangePasswordAsync`) e espera um `RoomInfo` por mensagem (timeout 8 s); `DeleteAsync` manda
  `RoomDelete` e espera o `Closed` com `room_deleted`. Token não reconhecido → sala sai de
  `OwnedRooms` e a UI mostra `Lounge_NotOwner`.
- `RoomJoinOptions.ManageOnly`: pula a espera do handoff de chave (ECDH) em salas sem senha,
  já que a sessão não lê mídia.
- `LoungePage`: card "Suas salas" (`OwnedRooms` do host, inclui privadas), menu "⋯" nas linhas
  de sala que o usuário é dono (Editar/Apagar, também `ContextFlyout`), estrela de dono no
  título, `ManageAsync` com anel de progresso e refresh das listas; senha pedida no mesmo
  diálogo do Entrar. `LoungeService.ForgetOwnedRoom`.
- Harness `managecheck` (vault): 17 checks contra servidor local (senha errada/ausente com
  token de dono, renomear + privar, troca de senha derruba os outros e mantém a sessão de dono,
  apagar fecha com `room_deleted`, `ManageOnly` entra em 3 ms com alguém dentro, token errado
  não é dono).

## 2.1.8

- `LoungePage.BuildHostRow`: o `Tapped` da linha do host disparava também no clique dos botões
  (estrela e "⋯"); `SelectHost` → `RefreshHosts` limpava o `ItemsControl` e o `MenuFlyout`,
  ancorado num botão já removido, fechava na hora. Os botões marcam `Tapped` como tratado e o
  menu é aberto por `ShowAt` no `Click`. Comportamento relatado pelo autor na 2.1.7.

## 2.1.7

- Chave do app por host: `SavedHost.ProtectedAppKey` (DPAPI via `SecretStore`) substitui o campo
  em texto puro `SavedHost.AppKey` e o global `AppSettings.RelayAppKey`; `SettingsStore.Sanitize`
  migra os dois na carga (chave legada em texto → protegida; `RelayAppKey` → host de `RelayUrl`
  quando este não tem chave) e zera os campos antigos. `LoungeService.AppKeyFor` lê só do host.
- Causa do bug: `AppKeyFor` fazia `host?.AppKey ?? RelayAppKey`, e como o host usado por último
  está sempre na lista, a chave editada em Configurações nunca era consultada; `RememberHost`
  ainda sobrescrevia `RelayAppKey` com a chave (vazia) do host a cada uso.
- Tela Salas: menu "⋯" por host (`MenuFlyout`, também no clique direito) com "Chave do app…" e
  "Remover host"; ícone de chave na legenda do host; `ShowError` centraliza o texto de erro e
  exibe o botão "Informar chave do app" em `bad_key`; ao adicionar host que responde `bad_key`
  o diálogo abre sozinho. Diálogo `EditHostKeyAsync` com `PasswordBox` (Peek).
- Configurações: seção "Servidor" removida (URL e chave), junto com as strings `Settings_Relay*`.
- Harness `settingscheck` (vault) valida a migração e o arquivo real sem abrir a UI.

## 2.1.6

- Janela de atualização redesenhada: versão atual e nova, categorias (Correções, Novidades,
  Melhorias), tamanho do download (incremental ou completo), barra de progresso e um botão
  "Instalar e reiniciar". Textos revistos para deixar claro o que muda e que salas, favoritos e
  configurações são mantidos.
- Notas de versão do release passam a vir do changelog voltado ao usuário, em pt-BR e inglês
  (marcadores `<!-- lang:xx -->`); o app mostra o idioma configurado. O `CHANGELOG.md` técnico
  continua no repositório.
- Changelog do app reorganizado por categoria em todas as versões.

## 2.1.5

- Captura de janela: os objetos da Windows.Graphics.Capture passam a ser criados num thread MTA
  dedicado (como o OBS faz). No Windows 10, criá-los no thread da interface deixava o item de
  captura preso a esse thread e a seleção de uma janela falhava com "interface marshalled para um
  thread diferente" (RPC_E_WRONG_THREAD), derrubando a prévia e a transmissão.
- Falhas de captura registram a etapa e a exceção completa no `diag.log`, e a mensagem na tela
  indica a etapa.

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
