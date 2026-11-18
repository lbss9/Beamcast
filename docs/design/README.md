# Design do Beamcast

O visual do app é desenhado no Penpot, no arquivo
[Beamcast — Design](https://design.penpot.app/?screen=workspace&team-id=3eb96054-c396-811e-8008-f61edf7b7998&file-id=7b7d55f9-965b-8000-8008-f75674dd2a00)
(abre para quem tem acesso ao time).

| Página | O que tem |
| --- | --- |
| 00 · Sistema | Cores (claro e escuro), tipografia, espaços, botões, selos, campos, cartão de sala, linha de host |
| 01 · Salas | Tela de salas e os estados: primeiro uso, carregando, host fora do ar, host pede chave, host vazio, tema claro |
| 02 · Sala | Assistindo, ninguém transmitindo, transmitir, configurações da sala |
| 03 · Diálogos | Criar sala, entrar com código, senha, apagar, adicionar host, convite, avisos |
| 04 · Configurações | Tela de configurações |

`redesign/` guarda as imagens exportadas do Penpot e `redesign/app/` os prints do app já com o visual novo.

## Onde isso vive no código

- Cores e estilos: `src/Beamcast/App.xaml` (dicionários de tema `Default` e `Light`, pincéis `Beam*Brush`).
- Peças montadas em código (avatar, selo, ícone): `src/Beamcast/Ui/Beam.cs`.
- Escolha em trilho (Todas / Minhas / Favoritas): `src/Beamcast/Controls/Segmented.cs`.
- Lateral de hosts: `src/Beamcast/MainWindow.SideBar.cs`.

A fonte nas pranchas é Inter; no app é a Segoe UI Variable do Windows.
