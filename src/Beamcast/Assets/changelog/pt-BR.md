## 2.0.0

- Salão self-hosted: suba o servidor com `docker compose up -d --build`, digite o endereço no app, crie um salão com senha ou entre com código e senha.
- No salão, qualquer pessoa transmite (várias ao mesmo tempo) e cada um escolhe o que assistir; pare de assistir sem sair.
- Tudo cifrado de ponta a ponta com a senha do salão; o servidor nunca vê senha, nomes, vídeo ou áudio.
- Áudio como no Discord: ao compartilhar a tela, o som dos apps vai junto, menos o das chamadas de voz; ao compartilhar uma janela, só o som daquele app.
- Volume e mudo para quem assiste; título da transmissão.

## 1.1.0

- Transmissão pela internet pelo servidor Beamcast: sem abrir porta, funciona com CGNAT.
- Vídeo e áudio cifrados de ponta a ponta; nem o servidor consegue ver.
- Código de convite gerado a cada sessão; modo direto continua disponível para a mesma rede.
- Configurações: endereço do servidor e chave do app.

## 1.0.0

- Primeira versão de estudo (pipeline em GPU): transmita um monitor ou janela para quem tiver o código de convite.
- Vídeo H.264/HEVC pelo encoder de hardware (VP8 na CPU como reserva) sobre TCP com controle de fila por espectador e recuperação por keyframe.
- Sala com senha (a senha nunca trafega em texto puro).
- Presets de qualidade, fps, bitrate e cursor; prévia ao vivo; pausar e retomar.
- Tela cheia no espectador e estatísticas dos dois lados.
- Atualização automática pelo GitHub Releases.
