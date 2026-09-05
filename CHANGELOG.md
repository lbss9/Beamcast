# Changelog

Beamcast is a study project. See the README for the full notice.

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
