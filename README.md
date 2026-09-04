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
codificação de vídeo (VP8 hoje, H.264 em seguida) e um pequeno protocolo de transmissão se
encaixam no Windows, e para produzir conclusões e código de referência para um aplicativo
empresarial separado e privado. Cada decisão de projeto aqui prioriza aprendizado e medição,
não uso em produção.

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

- Capturar um monitor ou uma janela com a Windows Graphics Capture API e ler os frames de
  volta do Direct3D 11.
- Redimensionar frames na CPU e codificar em VP8 (libvpx via `SIPSorceryMedia.Encoders`), com
  medições do custo de codificação por resolução.
- Um protocolo TCP mínimo com prefixo de tamanho, senha por desafio/resposta, recuperação por
  keyframe e controle de fila por espectador, para que um espectador lento nunca trave os outros.
- Exibir os frames decodificados num `WriteableBitmap` no WinUI 3, incluindo tela cheia.
- Empacotamento e atualização automática com Velopack e GitHub Releases.

Medições até agora (desktop classe Ryzen, VP8 por software):

| Tamanho de saída | Tempo de codificação por frame |
| ---------------- | ------------------------------ |
| 1280×720         | ~16 ms                         |
| 2560×1080        | ~35 ms                         |

## Como funciona

```
Windows.Graphics.Capture ─► frame BGRA ─► escala p/ preset ─► VP8 (libvpx) ─► fan-out TCP
                                                                                  │
                                       WriteableBitmap ◄─ BGRA ◄─ decode VP8 ◄────┘ (cada espectador)
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

## Organização do projeto

```
src/Beamcast
├── Capture/    Windows.Graphics.Capture + leitura D3D11, enumeração de fontes
├── Codec/      wrapper VP8, tipos de frame, escalador bilinear
├── Net/        protocolo, servidor host, cliente espectador, códigos de convite, autenticação
├── Services/   BroadcastService, WatchService, UpdateService
├── Pages/      Transmitir, Assistir, Configurações, Sobre
└── Controls/   VideoView (superfície WriteableBitmap)
tests/Beamcast.Tests   testes xunit das partes de lógica pura
```

## Próximos experimentos

- Áudio do sistema (WASAPI loopback + Opus).
- H.264 por hardware via Media Foundation, comparado ao VP8 por software.
- Um relay opcional para ninguém precisar liberar porta no roteador.
