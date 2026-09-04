<p align="center">
  <img src="src/Beamcast/Assets/Beamcast.png" width="120" alt="Beamcast" />
</p>

<h1 align="center">Beamcast</h1>

<p align="center">
  <strong>Um projeto de estudo sobre captura de tela, codecs de vídeo e transmissão em tempo real no Windows.</strong><br />
  Não é um produto. Foi feito para aprender, medir e servir de caso de estudo para um outro aplicativo, empresarial e privado.
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
codificação de vídeo por hardware e um pequeno protocolo de transmissão se encaixam no Windows,
e para produzir conclusões e código de referência para um aplicativo empresarial separado e
privado. Cada decisão de projeto aqui prioriza aprendizado e medição, não uso em produção.

- **Sem garantia, sem responsabilidade.** O software é fornecido "como está". O autor não se
  responsabiliza por nada que aconteça ao executar, distribuir ou construir em cima dele.
- **Qualquer outro uso é responsabilidade sua.** Se você usar o Beamcast para algo além de
  estudar o código, faz isso por sua conta e risco, e é responsável por cumprir as leis,
  políticas e exigências de consentimento que se aplicam a você. Compartilhar a tela expõe
  tudo o que estiver nela; pense antes de transmitir.
- **Sem suporte, sem promessa de roadmap.** Issues podem ficar sem resposta; APIs, o protocolo
  de rede e a organização dos arquivos podem mudar a qualquer momento.
- **Não é endurecido.** O tráfego é TCP puro (a senha da sala nunca trafega em texto claro, mas o
  vídeo sim). Não exponha à internet sem entender o que isso significa.

O mesmo aviso aparece dentro do app no primeiro uso e na página Sobre.

## O que o estudo cobre

- Capturar um monitor ou uma janela com a Windows Graphics Capture API e manter o frame na GPU.
- Converter BGRA → NV12 e redimensionar no video processor do D3D11 (bloco de função fixa da GPU).
- Codificar em H.264/HEVC com o encoder de hardware exposto pelo Media Foundation (AMD VCN,
  NVIDIA NVENC, Intel QSV ou o encoder D3D12 genérico), com perfil de baixa latência.
- Decodificar por hardware (Media Foundation + DXVA) direto em textura NV12 e apresentar num
  `SwapChainPanel` sem passar pela CPU.
- Um protocolo TCP mínimo com prefixo de tamanho, senha por desafio/resposta, recuperação por
  keyframe e controle de fila por espectador, para que um espectador lento nunca trave os outros.
- VP8 por software (libvpx) como reserva para máquinas sem encoder de hardware.
- Empacotamento e atualização automática com Velopack e GitHub Releases.

## O que a pesquisa concluiu (e o que virou código)

Estudo de como Sunshine/Moonlight, Parsec e Steam Remote Play fazem 4K60 com latência baixa:

| Princípio | Como está no Beamcast |
| --- | --- |
| Nunca copiar o frame para a CPU | WGC → textura própria (cópia na GPU) → NV12 no video processor → encoder, tudo no mesmo device D3D11 |
| Encoder de hardware, nunca software, para 4K | MFT assíncrono do driver (AMD/NVIDIA/Intel), `MF_LOW_LATENCY`, CBR, VBV de 1 frame, sem B-frames, 1 ref, GOP infinito |
| Fila de profundidade zero | O encoder só recebe um frame quando pede (`METransformNeedInput`); frame antigo é descartado, nunca enfileirado |
| Keyframe só quando alguém precisa | Forçado quando um espectador entra ou cai; se o driver ignorar o pedido (AMD ignora), o encoder é recriado e nasce com IDR |
| Decodificar e apresentar no mesmo lugar | Decode DXVA na thread de rede e blit direto no back buffer; sem salto pela thread de UI |
| Não deixar o Windows limitar a captura | `MinUpdateInterval` = 4 ms (senão o WGC trava em ~60 Hz) e pacing por deadline em vez de "1/fps desde o último" |
| Transporte UDP com FEC (Moonlight, Parsec) | Ainda não. TCP por enquanto; é o próximo passo |

Medições nesta máquina (Ryzen 7 5700X, Radeon RX 6750 XT, monitor 2560×1080 a 75 Hz):

| Caminho | Codificação por frame | Decodificação | Observação |
| --- | --- | --- | --- |
| VP8 por software, 1280×720 | ~16 ms | CPU | reserva |
| VP8 por software, 2560×1080 | ~35 ms | CPU | não sustenta 60 fps |
| H.264 AMD VCN, 2560×1080 @ 60 | ~8 ms | ~0,4 ms (GPU) | 60 fps sustentados, ~36–42 Mbps |
| HEVC AMD VCN, 2560×1080 @ 60 | ~5 ms | ~0,4 ms (GPU) | mesma qualidade com ~1/3 do bitrate |

4K não pôde ser medido aqui (não há monitor 4K); o caminho é o mesmo e a VCN 3.0 é
especificada para 4K60 em H.264 e HEVC.

## Como funciona

```
Windows.Graphics.Capture ─► textura BGRA (GPU) ─► VideoProcessor: escala + NV12 ─► MFT H.264/HEVC ─► fan-out TCP
                                                                                                        │
                    SwapChainPanel ◄─ VideoProcessor: NV12→RGB + letterbox ◄─ decoder DXVA (NV12) ◄─────┘ (cada espectador)
```

Quem transmite hospeda um pequeno servidor TCP; os espectadores conectam direto usando um
código de convite que carrega endereço, porta e senha. Não há conta nem servidor intermediário.

## Compilar a partir do código

Requisitos: Windows 10 1809+ (Windows 11 recomendado), .NET 8 SDK.

```powershell
dotnet build Beamcast.sln -c Release
dotnet test tests/Beamcast.Tests
dotnet run --project src/Beamcast
```

`scripts/pack.ps1` publica um build self-contained e o empacota com Velopack (`vpk`) num MSI
em `artifacts/release`. Os workflows do GitHub criam a tag a partir da versão do csproj e
publicam o release; o app consulta o GitHub Releases em busca de atualizações ao abrir.

Para depurar a pipeline, crie um arquivo vazio `%LOCALAPPDATA%\Beamcast\diag.on` (ou defina
`BEAMCAST_DIAG=1`): o app passa a escrever `diag.log` na mesma pasta com eventos de encoder,
keyframe e do gate de cada espectador.

## Organização do projeto

```
src/Beamcast
├── Capture/    Windows.Graphics.Capture, enumeração de fontes, frame na GPU
├── Codec/      VideoCodec, VP8 (reserva), escalador bilinear
│   └── Gpu/    GpuDevice, VideoProcessorConverter, MfVideoEncoder, MfVideoDecoder, ICodecAPI
├── Render/     SwapChainPresenter (DXGI composition swap chain no SwapChainPanel)
├── Net/        protocolo, servidor host, cliente espectador, códigos de convite, autenticação
├── Services/   BroadcastService, WatchService, UpdateService
├── Pages/      Transmitir, Assistir, Configurações, Sobre
└── Controls/   GpuVideoView
tests/Beamcast.Tests   testes xunit das partes de lógica pura
```

## Próximos experimentos

- Transporte UDP com FEC Reed-Solomon e invalidação de frame de referência (como Moonlight).
- Áudio do sistema (WASAPI loopback + Opus).
- AV1 nas GPUs que expõem encoder AV1.
- Um relay opcional para ninguém precisar liberar porta no roteador.
