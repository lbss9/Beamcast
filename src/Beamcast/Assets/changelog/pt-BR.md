## 2.1.10

### Correções

- Na janela de atualização, o texto com o tamanho do download era cortado pelos botões ("Download de 0,7 MB (só o que…"). Agora ele ocupa a linha inteira e os botões ficam logo abaixo.

## 2.1.9

### Novidades

- Editar e apagar salas direto da tela Salas, sem precisar entrar. As salas que você criou ganharam um menu "⋯" (também no clique direito) com "Editar sala…" e "Apagar sala"; o app entra por um instante como dono, aplica a mudança e sai.
- Novo card "Suas salas" no host, com todas as salas que você criou ali, inclusive as privadas, que antes não apareciam em lugar nenhum fora da própria sala.
- Uma estrela ao lado do nome marca as salas de que você é dono.

## 2.1.8

### Correções

- O botão "⋯" do host na tela Salas não abria o menu: o clique também selecionava o host e a lista era reconstruída antes de o menu aparecer. Agora o menu abre normalmente.

## 2.1.7

### Novidades

- A chave do app agora é de cada host. No menu "⋯" do host (ou clicando com o botão direito nele), em "Chave do app…", você informa a chave que aquele servidor exige. Um ícone de chave aparece ao lado do host quando há chave salva.
- Quando um host recusa o app por falta de chave, a tela Salas mostra o botão "Informar chave do app" logo abaixo do aviso. Ao adicionar um host que exige chave, o app já pergunta a chave na hora.
- A seção "Servidor" saiu de Configurações: os hosts e as chaves ficam só na tela Salas.

### Correções

- A chave digitada em Configurações não era usada em um host que já estava na lista, então o host continuava recusando o app. Cada host guarda a própria chave agora.

### Segurança

- A chave do app fica protegida no disco para a sua conta do Windows (DPAPI), como as senhas de sala lembradas. Chaves salvas por versões anteriores são convertidas na primeira abertura.

## 2.1.6

### Melhorias

- Nova janela de atualização: mostra a versão atual e a nova, o que muda por categoria, o tamanho do download e o progresso, com um único botão "Instalar e reiniciar".
- Notas de versão em português e inglês, no idioma configurado no app.

## 2.1.5

### Correções

- No Windows 10, escolher uma janela para transmitir falhava com a mensagem "interface marshalled para um thread diferente" e a transmissão era encerrada. A captura de janela agora é preparada da forma correta para esse Windows.

### Melhorias

- Falhas de captura registram a etapa exata no `diag.log`, o que acelera o diagnóstico.

## 2.1.4

### Correções

- Escolher uma janela, redimensioná-la ou trocar de tela para janela no meio da transmissão não encerra mais a transmissão. O vídeo mantém a resolução escolhida ao iniciar; imagens com outra proporção entram com barras pretas.

## 2.1.3

### Correções

- A borda amarela ao redor da tela compartilhada não aparece mais em nenhuma versão do Windows 10 ou 11: a captura de tela inteira passou a usar outra tecnologia do Windows (Desktop Duplication), que não desenha essa moldura.
- Parar a transmissão encerra a captura imediatamente. Nada da tela é lido depois de "Parar".

## 2.1.2

### Correções

- Removido o tooltip "Esc" que aparecia ao parar o mouse em qualquer ponto da janela.
- Primeira tentativa de remover a borda amarela (Windows 11); concluída na 2.1.3.

## 2.1.1

### Correções

- Atualização automática consertada. As versões anteriores publicavam apenas o instalador, sem o feed que o app consulta, então nenhuma instalação recebia atualizações. A partir daqui as versões novas chegam sozinhas.

### Melhorias

- A janela de atualização mostra o progresso do download e retoma uma atualização já baixada.

## 2.1.0

### Novidades

- Tela Salas: lista de hosts favoritos, salas públicas do host, salas favoritas e entrada por código ou convite.
- Salas públicas ou privadas, permanentes ou temporárias, com senha opcional, limite de pessoas e opção de só o dono transmitir.
- Dono da sala: edita a sala, troca ou remove a senha, gera convites com validade e número de usos, revoga convites, expulsa membros e apaga a sala.
- Reconexão automática: se a internet cair, o app volta sozinho, republica a sua transmissão e retoma o que você assistia.

### Segurança

- Tudo continua cifrado de ponta a ponta, inclusive em salas sem senha: a chave é entregue entre os próprios membros e o servidor nunca a vê.
- Tentativas erradas de senha, convite ou código são limitadas por endereço e por sala.

## 2.0.1

### Correções

- Quem cai sem avisar sai do salão em até 30 segundos, mesmo atrás de túnel, e a transmissão dele é encerrada para os outros.

## 2.0.0

### Novidades

- Salão self-hosted: suba o servidor com `docker compose up -d --build`, digite o endereço no app, crie um salão com senha ou entre com código e senha.
- No salão, qualquer pessoa transmite (várias ao mesmo tempo) e cada um escolhe o que assistir; dá para parar de assistir sem sair.
- Áudio como no Discord: ao compartilhar a tela, o som dos apps vai junto, menos o das chamadas de voz; ao compartilhar uma janela, só o som daquele app.
- Volume e mudo para quem assiste; título da transmissão.

### Segurança

- Tudo cifrado de ponta a ponta com a senha do salão; o servidor nunca vê senha, nomes, vídeo ou áudio.

## 1.1.0

### Novidades

- Transmissão pela internet pelo servidor Beamcast: sem abrir porta, funciona com CGNAT.
- Vídeo e áudio cifrados de ponta a ponta; nem o servidor consegue ver.
- Código de convite gerado a cada sessão; modo direto continua disponível para a mesma rede.
- Configurações: endereço do servidor e chave do app.

## 1.0.0

### Novidades

- Primeira versão de estudo (pipeline em GPU): transmita um monitor ou janela para quem tiver o código de convite.
- Vídeo H.264/HEVC pelo encoder de hardware (VP8 na CPU como reserva) com controle de fila por espectador e recuperação por keyframe.
- Sala com senha (a senha nunca trafega em texto puro).
- Presets de qualidade, fps, bitrate e cursor; prévia ao vivo; pausar e retomar.
- Tela cheia no espectador e estatísticas dos dois lados.
- Atualização automática pelo GitHub Releases.
