# SubDownload

Adiciona um item **"Baixar Legenda (PT-BR)"** ao menu de contexto do Windows
Explorer para arquivos `.mkv` e `.mp4`. Ao clicar, o app:

1. Extrai o nome do filme + ano do nome do arquivo (ex:
   `Coyote.vs.Acme.2026.1080p.HEVC.x265.RMTeam.mkv` -> `Coyote.vs.Acme.2026`).
2. Pesquisa em `https://www.subtitlecat.com/index.php?search=...`.
3. Ordena todos os resultados por o quão parecidos são com a release local
   (mesma tag de fonte/qualidade — CAM, TS, WEB-DL, 2160p, HEVC/x265, grupo
   etc.), não só o título.
4. Abre a página do candidato mais parecido e procura a legenda **Portuguese
   (Brazil)** (`pt-BR`, com fallback para `pt`) **já pronta para download**.
   Se esse candidato não tiver, passa para o próximo mais parecido, e assim
   por diante — o app nunca aciona tradução (nem via Google Translate nem via
   o botão "Translate" do site); só baixa arquivos `.srt` que já existem.
5. Remove marcações **SDH** (legenda para surdos/deficientes auditivos) do
   texto baixado — descrições de som entre colchetes/parênteses (ex:
   `[música tocando]`, `(risos)`) e prefixos de quem fala em maiúsculas (ex:
   `ANGIE:`). Funciona tanto se só parte da legenda for SDH quanto se a
   legenda inteira for (nesse caso o bloco inteiro é descartado e a
   numeração é reajustada).
6. Salva o `.srt` na mesma pasta do vídeo, com o mesmo nome do arquivo (ex:
   `Coyote.vs.Acme.2026.1080p.HEVC.x265.RMTeam.srt`).
7. Se outros candidatos parecidos também tiverem pt-BR/pt pronta, baixa até
   2 alternativas extras e salva como `NomeDoArquivo.1.srt`,
   `NomeDoArquivo.2.srt` — pra trocar manualmente se a principal estiver
   fora de sincronia (o player só carrega automaticamente a que tem o nome
   exato do vídeo).
8. Se nenhum dos candidatos pesquisados tiver pt-BR/pt pronta, o app avisa e
   não baixa nada (não tenta traduzir).

## Estrutura

```
src/SubDownload/
  Program.cs             orquestração (args -> busca -> match -> download)
  MovieNameParser.cs      extrai "título + ano" do nome do arquivo
  SubtitleCatParser.cs    parsing do HTML do subtitlecat.com (regex)
  ReleaseMatcher.cs       escolhe o resultado mais parecido com a release local
  SdhCleaner.cs           remove marcações SDH do texto da legenda baixada
install.ps1               publica o app e registra o menu de contexto (HKCU)
uninstall.ps1              remove o menu de contexto e o app instalado
```

## Algoritmo de similaridade (`ReleaseMatcher`)

O nome do arquivo local e o título de cada resultado são tokenizados
(separando por qualquer caractere não alfanumérico, em minúsculas). A
similaridade é um **Jaccard ponderado**: tokens que são tags de release
conhecidas (`cam`, `ts`, `hdts`, `web-dl`, `bluray`, `1080p`, `2160p`, `x265`,
`hevc`, `ac3`, `hdr`, nome de grupo etc.) pesam mais do que palavras comuns do
título. Isso garante que, se o arquivo local é um `CAM`, o candidato `CAM`
ganha do `HEVC`; se é `2160p`, o candidato `2160p` ganha do `1080p`; e assim
por diante — sem precisar de uma lista fixa de casos.

## Remoção de SDH (`SdhCleaner`)

Depois de baixar a legenda, o texto passa por uma limpeza de marcações SDH:

- Remove tudo entre `[colchetes]` e `(parênteses)` — inclusive quando a
  marcação é quebrada em mais de uma linha dentro do mesmo bloco (ex:
  `[empresário\nfala indistintamente]`).
- Remove prefixos de identificação de quem fala em maiúsculas no início da
  linha (ex: `ANGIE:`), mantendo o resto da fala.
- Se, depois de limpar, uma linha ficar sem nenhuma letra ou número (ex: só
  sobrou um `-` de marcação de diálogo), a linha é descartada.
- Se um bloco inteiro ficar vazio (a fala toda era SDH), o bloco é removido
  e a numeração dos blocos seguintes é reajustada.

Testado contra uma legenda real fortemente SDH (mais de 450 blocos 100%
SDH e ~230 falas parcialmente limpas) sem sobrar colchetes soltos no
resultado.

## Instalação

Pré-requisito: [.NET SDK](https://dotnet.microsoft.com/download) (usado só
para compilar; o executável final é autocontido e não exige .NET instalado
na máquina de destino).

```powershell
cd C:\GitLab\SubDownload
.\install.ps1
```

O script:
- Publica `SubDownload.exe` como executável único, self-contained, `win-x64`.
- Copia para `%LOCALAPPDATA%\SubDownload\SubDownload.exe`.
- Registra o menu de contexto em `HKCU:\Software\Classes\SystemFileAssociations`
  para `.mkv` e `.mp4` — **não precisa de administrador** e vale só para o
  usuário atual.
- O item do menu usa um ícone próprio (`assets/SubDownload.ico`, embutido no
  `.exe` via `<ApplicationIcon>`) em vez do ícone genérico do executável.

Depois disso, clique com o botão direito num `.mkv` ou `.mp4` no Explorer e
escolha **"Baixar Legenda (PT-BR)"**.

## Desinstalação

```powershell
.\uninstall.ps1
```

## Uso manual (sem Explorer)

```powershell
SubDownload.exe "C:\Filmes\Coyote.vs.Acme.2026.1080p.HEVC.x265.RMTeam.mkv"
```

## Limitações conhecidas

- Depende da estrutura HTML atual do subtitlecat.com; se o site mudar o
  layout, o parsing (regex) pode precisar de ajuste.
- Se o filme não tiver ano no nome do arquivo, a pesquisa usa o nome inteiro
  como melhor esforço.
- Se não houver legenda `pt-BR` nem `pt` **já pronta** em nenhum dos
  candidatos verificados (até 20, dos mais parecidos aos menos), o app avisa
  e lista os idiomas disponíveis no último verificado, sem baixar nada. O app
  não traduz nada por conta própria (nem via Google Translate direto, nem
  automatizando o botão "Translate" do site).
