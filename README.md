<p align="center">
  <img src="src/Beamcast/Assets/Beamcast.png" width="120" alt="Beamcast" />
</p>

<h1 align="center">Beamcast</h1>

<p align="center">
  <strong>Um salão de compartilhamento de tela, self-hosted e cifrado de ponta a ponta.</strong><br />
  Projeto de estudo sobre captura, codecs, áudio por processo e transmissão em tempo real no Windows.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-projeto%20de%20estudo-FF4D6D?style=flat-square" alt="Projeto de estudo" />
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4?style=flat-square&logo=windows&logoColor=white" alt="Windows 10 / 11" />
  <img src="https://img.shields.io/badge/WinUI-3-59C8C8?style=flat-square" alt="WinUI 3" />
  <img src="https://img.shields.io/badge/.NET-8-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET 8" />
</p>

---

## Leia isto primeiro

**O Beamcast existe apenas para estudo.** Ele foi escrito para explorar como captura de tela,
codificação de vídeo por hardware, áudio por processo e um protocolo de transmissão se
encaixam no Windows, e para produzir conclusões e código de referência para um aplicativo
empresarial separado e privado. Cada decisão prioriza aprendizado e medição, não uso em produção.

- **Sem garantia, sem responsabilidade.** O software é fornecido "como está". O autor não se
  responsabiliza por nada que aconteça ao executar, distribuir ou construir em cima dele.
- **Qualquer outro uso é responsabilidade sua.** Se você usar o Beamcast para algo além de
  estudar o código, faz isso por sua conta e risco, e é responsável por cumprir as leis,
  políticas e exigências de consentimento que se aplicam a você. Compartilhar a tela expõe
  tudo o que estiver nela; pense antes de transmitir.
- **Sem suporte, sem promessa de roadmap.** Issues podem ficar sem resposta; o protocolo e a
  organização dos arquivos podem mudar a qualquer momento.
- **Não é endurecido para produção.** O conteúdo vai cifrado de ponta a ponta e a senha nunca
  trafega, mas não houve auditoria de segurança. Use em ambientes que você controla.

O mesmo aviso aparece dentro do app no primeiro uso e na página Sobre.

## Como funciona o salão

1. Alguém sobe o **servidor** (um container Docker) em qualquer máquina com IP ou host
   alcançável: um VPS, o PC de casa atrás de um túnel, um servidor na LAN.
2. No app, cada pessoa digita o **endereço do servidor**, o **nome dela** e cria um salão
   (nome + senha obrigatória) ou entra num salão existente (código + senha).
3. Dentro do salão todo mundo vê quem está online e quais transmissões existem. **Qualquer um
   transmite** a tela (monitor ou janela), com ou sem áudio; **cada um escolhe** qual transmissão
   assistir, para de assistir quando quiser e continua no salão. Várias transmissões ao mesmo
   tempo são normais.

O servidor nunca recebe a senha e não consegue ver nem ouvir nada:

```
chave       = PBKDF2-SHA256(senha, salt, 200 000 rodadas)   fica só nas máquinas dos membros
verificador = HMAC(chave, "verify")                          o servidor guarda isto na criação
prova       = HMAC(verificador, nonce)                        quem entra envia; o servidor confere
conteúdo    = HKDF(chave, "content")                          AES-256-GCM em nomes, títulos, vídeo e áudio
```

O servidor só lê o tipo de cada mensagem e a flag de keyframe, que ele usa para descartar
quadros de um espectador lento e pedir um keyframe ao transmissor sem prejudicar os outros.
Quem sabe o código mas não a senha é recusado antes de ver qualquer coisa.

## Subir o servidor (quem hospeda)

Precisa só de Docker com Compose. Sem Actions, sem registry, sem conta:

```bash
git clone https://github.com/lbss9/Beamcast.git
cd Beamcast
docker compose up -d --build
```

O servidor escuta na porta `47710`. No app, o endereço é `ws://SEU-IP:47710` (ou só `SEU-IP`).
Variáveis opcionais num `.env` ao lado do `docker-compose.yml`:

| Variável | Efeito |
| --- | --- |
| `BEAMCAST_PORT` | porta publicada no host (padrão `47710`) |
| `BEAMCAST_APP_KEY` | quando definida, o app precisa carregar a mesma chave (Configurações → Servidor) |
| `BEAMCAST_LOUNGE_TTL_HOURS` | horas que um salão vazio sobrevive; `0` (padrão) mantém até apagar o volume |

Os salões (código, nome, salt e verificador; nunca conteúdo) ficam no volume `beamcast_data`
e sobrevivem a reinícios. `GET /health` mostra salões, membros e transmissões ativos.

**Pela internet:** abra a porta `47710/tcp` no roteador, ou coloque o container atrás de um
reverse proxy/túnel com TLS (Cloudflare Tunnel, Caddy, nginx) e use `wss://seu-host` no app.
Como todo membro só faz conexão de saída, CGNAT e firewall de quem entra não importam.

**Banda:** o servidor recebe cada transmissão uma vez e reenvia uma vez por espectador. 1080p60 a
~12 Mbps por espectador; 4K60 H.264 a ~40 Mbps; HEVC gasta ~35% menos.

## O que o app faz

- **Captura** de monitor ou janela pela Windows Graphics Capture API, que fica na GPU.
- **Vídeo**: conversão BGRA→NV12 no D3D11 Video Processor e encoder H.264/HEVC por hardware via
  Media Foundation (AMD, NVIDIA, Intel, D3D12) com perfil de baixa latência; VP8 por software
  como reserva. Decodificação DXVA direto para um `SwapChainPanel`.
- **Áudio**: loopback **por processo** (a mesma API que Discord e Teams usam). Ao compartilhar
  uma janela, captura só o app dela; ao compartilhar a tela inteira, captura todos os apps com
  sessão de áudio **exceto** Discord, Teams, Zoom, Slack, WhatsApp e afins, para a voz da chamada
  não ser retransmitida. Mixagem em quadros de 20 ms e Opus a 128 kbps, com ocultação de perda
  no receptor.
- **Rede**: um WebSocket por membro, mensagens cifradas, keyframe sob demanda, fila por
  espectador no servidor. Presets até 2160p/120 fps.

## Compilar

Requisitos: Windows 10 1809+ (Windows 11 recomendado; áudio por processo pede build 20348+),
.NET 8 SDK.

```powershell
dotnet build Beamcast.sln -c Release
dotnet test tests/Beamcast.Tests
dotnet run --project src/Beamcast
```

O servidor também roda sem Docker: `dotnet run --project src/Beamcast.Server` (porta 8080).
`scripts/pack.ps1` gera o MSI com Velopack; os workflows do GitHub publicam o release do app
a partir da versão do csproj, e o app procura atualizações no GitHub Releases.

Para depurar, crie um arquivo vazio `%LOCALAPPDATA%\Beamcast\diag.on` (ou defina
`BEAMCAST_DIAG=1`): o app escreve `diag.log` na mesma pasta com eventos de encoder, keyframe e
fila.

## Organização

```
src/Beamcast          app WinUI 3
├── Capture/          Windows.Graphics.Capture + D3D11, enumeração de fontes
├── Codec/            VP8, encoders/decoders Media Foundation, conversor de vídeo
├── Audio/            loopback por processo, seletor de fontes, mixer, Opus, player
├── Net/              protocolo do salão, criptografia, cliente
├── Services/         LoungeService, BroadcastService, WatchService, UpdateService
└── Pages/            Salão (entrada), Sala, Configurações, Sobre
src/Beamcast.Server   servidor do salão (ASP.NET Core, WebSocket)
tests/Beamcast.Tests  testes xunit das partes de lógica pura
docker-compose.yml    sobe o servidor a partir do código
```
