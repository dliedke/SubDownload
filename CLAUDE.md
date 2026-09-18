# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Visão geral

App console .NET 10 (C#, Windows) que baixa legenda **PT-BR** para um `.mkv`/`.mp4`,
chamado pelo menu de contexto do Explorer. Fontes, em ordem: **OpenSubtitles**
primeiro, **subtitlecat.com** só se faltar. Nunca aciona tradução — só baixa `.srt`
que já existem. Código, comentários, mensagens de console e README são em português
(comentários no código sem acento).

## Comandos

```powershell
dotnet build                                  # build (Debug) — SubDownload.csproj está na raiz do repo
.\install.ps1                                 # publish single-file self-contained win-x64 -> %LOCALAPPDATA%\SubDownload, registra menu (HKCU) e REINICIA o Explorer
.\uninstall.ps1                               # remove menu e app (também reinicia o Explorer)

# rodar direto (após build)
bin/Debug/net10.0/SubDownload.exe "C:\Filmes\Filme.2023.1080p.mkv"
bin/Debug/net10.0/SubDownload.exe --download-alts "C:\Filmes\Filme.2023.1080p.mkv"
bin/Debug/net10.0/SubDownload.exe --clean-alts "C:\Filmes\Filme.2023.1080p.mkv"
```

Não há projeto de testes nem linter. Para testar de ponta a ponta, crie um arquivo
falso (> 128 KB, senão o hash do OpenSubtitles é pulado) com nome de release real, ex.
`Oppenheimer.2023.1080p.BluRay.x264-GalaxyRG.mkv`, e prefira `--download-alts`: o modo
principal chama `PlayVideo`, que abre o arquivo no player padrão.

## Arquitetura

Os três modos (`Program.Main`):
- **principal** → salva `Nome.srt` e abre o vídeo no player.
- `--download-alts` → pula a primeira legenda da sequência e salva até 2 como
  `Nome.1.srt`, `Nome.2.srt`. A "pulada" é recalculada por nova pesquisa (não há
  estado entre execuções), então as duas execuções dependem da ordenação ser
  determinística.
- `--clean-alts` → apaga `Nome.<n>.srt`; nunca toca `Nome.srt`.

Fluxo de busca (`Program.CollectReadySubtitlesAsync` / `EnumerateAllSourcesAsync`):
um `IAsyncEnumerable<ReadySubtitle>` que concatena as fontes, com dedupe por URL,
`skip`/`take`. Como é lazy, o subtitlecat só é consultado se o OpenSubtitles não
bastar. Falha no OpenSubtitles vira aviso (não aborta); falha na pesquisa do
subtitlecat propaga até o `catch` do `Main`.

`ReadySubtitle` carrega um delegate `DownloadTextAsync` — cada fonte sabe baixar e
decodificar o próprio arquivo. Depois disso, tudo passa por `SdhCleaner.RemoveSdh`
(que também renumera os blocos) e é salvo em UTF-8 sem BOM.

- **`Sources/OpenSubtitlesClient`**: API REST **legada** `rest.opensubtitles.org` (sem chave;
  a API nova `api.opensubtitles.com` exige chave). Particularidades da API:
  parâmetros no path em ordem alfabética (`moviebytesize-/moviehash-/query-/sublanguageid-`),
  **não aceita múltiplos idiomas numa busca** (por isso `pob` e depois `por`),
  `subencoding-utf8` no link não converte nada. Filtra `SubSumCD == 1`, `.srt`, ano
  igual e nome do filme como prefixo dos tokens do arquivo (hash match dispensa o
  nome). Download vem `.gz` em encoding original (UTF-8 estrito, senão `SubEncoding`/CP1252)
  e tem blocos de propaganda injetados (`osdb.link`, `OpenSubtitles`) removidos em `RemoveAds`.
- **`Sources/SubtitleCatClient`** + **`Sources/SubtitleCatParser`**: scraping por regex do HTML
  (sem lib de HTML). Pesquisa, ranqueia, abre até 20 páginas de candidatos procurando
  `id="download_pt-BR"` (fallback `pt`).
- **`Matching/ReleaseMatcher`**: Jaccard ponderado entre tokens do nome do arquivo e do título
  do candidato; tags de release (`cam`, `1080p`, `x265`, `bluray`…) pesam 3×. Usado
  pelas duas fontes.
- **`Matching/MovieNameParser`**: query = nome do arquivo até o ano (`Filme.2023`); sem ano,
  usa o nome inteiro.

## Instalador

As chaves do registro em `HKCU:\Software\Classes\SystemFileAssociations\<ext>\shell`
têm prefixo numérico (`SubDownload1BaixarPTBR`, `SubDownload2BaixarAlts`,
`SubDownload3ClearAlts`) porque o Explorer ordena os itens pelo nome da chave. Ao
renomear/adicionar itens, atualize **`install.ps1` e `uninstall.ps1`** e mantenha os
nomes antigos na lista de limpeza.

`install.ps1` tem dois modos (detectados por `SubDownload.csproj` existir ou não ao
lado do script): dentro do repo, compila com `dotnet publish`; num pacote de release
(zip do GitHub Releases), usa o `SubDownload.exe` que já vem publicado, sem exigir
.NET instalado. `Install.bat`/`Uninstall.bat` são wrappers de duplo clique dos `.ps1`
(`-ExecutionPolicy Bypass`) para quem baixa o zip.

## Release automático (GitHub Actions)

`.github/workflows/release.yml` publica um novo GitHub Release **a cada push na
`main`**: calcula a próxima versão (bump de patch, ex. `v1.0.0` -> `v1.0.1`), cria e
envia a tag, compila self-contained `win-x64` e sobe o zip
(`SubDownload.exe` + `install.ps1` + `uninstall.ps1` + `Install.bat` +
`Uninstall.bat` + `README.md`). **Não crie/envie tags manualmente** — cada commit
enviado para `main` já dispara isso sozinho. Para subir minor/major em vez de patch,
disparar manualmente pela aba Actions (`workflow_dispatch`, escolhendo o bump).
Ao mexer no conteúdo do pacote de release (ex. novo arquivo que precisa ir no zip),
atualize a lista de `Copy-Item` no workflow junto com `install.ps1`/`uninstall.ps1`.
