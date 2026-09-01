// SubDownload
// Baixa a legenda em Portugues (Brasil) de um filme, a partir do site subtitlecat.com,
// pesquisando pelo nome do arquivo de video passado como argumento (integracao com o
// menu de contexto do Windows Explorer). Salva o .srt na mesma pasta do video, com o
// mesmo nome do arquivo.

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SubDownload;

internal static class Program
{
    private const string BaseUrl = "https://www.subtitlecat.com";
    private static readonly string[] SupportedExtensions = { ".mkv", ".mp4" };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== SubDownload - legenda PT-BR (subtitlecat.com) ===\n");

        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.WriteLine("Uso: SubDownload.exe \"caminho\\para\\filme.mkv\"");
            return Pause(1);
        }

        var videoPath = args[0].Trim('"');

        try
        {
            return await RunAsync(videoPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"[ERRO] {ex.Message}");
            return Pause(1);
        }
    }

    private static async Task<int> RunAsync(string videoPath)
    {
        if (!File.Exists(videoPath))
        {
            Console.WriteLine($"[ERRO] Arquivo nao encontrado: {videoPath}");
            return Pause(1);
        }

        var ext = Path.GetExtension(videoPath);
        if (!SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[ERRO] Extensao nao suportada: {ext} (use .mkv ou .mp4)");
            return Pause(1);
        }

        var fileNameNoExt = Path.GetFileNameWithoutExtension(videoPath);
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;

        Console.WriteLine($"Arquivo:  {fileNameNoExt}{ext}");

        var searchQuery = MovieNameParser.BuildSearchQuery(fileNameNoExt);
        Console.WriteLine($"Pesquisa: {searchQuery}");

        using var http = CreateHttpClient();

        var searchUrl = $"{BaseUrl}/index.php?search={Uri.EscapeDataString(searchQuery)}";
        Console.WriteLine($"\nBuscando em: {searchUrl}");
        var searchHtml = await http.GetStringAsync(searchUrl);

        var candidates = SubtitleCatParser.ParseSearchResults(searchHtml);
        if (candidates.Count == 0)
        {
            Console.WriteLine("[ERRO] Nenhum resultado encontrado para essa pesquisa.");
            return Pause(1);
        }

        Console.WriteLine($"\n{candidates.Count} resultado(s) encontrado(s):");
        foreach (var c in candidates)
            Console.WriteLine($"  - {c.Title}");

        var ranked = ReleaseMatcher.RankBySimilarity(fileNameNoExt, candidates);
        Console.WriteLine($"\n>> Melhor correspondencia: {ranked[0].Candidate.Title}");

        // Tenta o melhor match primeiro; se ele nao tiver legenda pt-BR (ou pt) JA PRONTA
        // para download, vai tentando os proximos mais parecidos (sem acionar nenhuma
        // traducao — so usa o que o site ja tem pronto). Continua coletando mais alguns
        // matches (nao so o primeiro) pra salvar como alternativas, caso a melhor fique
        // fora de sincronia.
        const int MaxCandidatesToCheck = 20;
        const int MaxAlternates = 2; // total salvo = 1 principal + ate MaxAlternates extras
        var found = new List<(SubtitleCandidate Candidate, string SrtUrl)>();
        List<string> lastAvailableLangs = new();

        foreach (var rankedCandidate in ranked.Take(MaxCandidatesToCheck))
        {
            if (found.Count > MaxAlternates) break;

            var candidate = rankedCandidate.Candidate;
            var pageUrl = $"{BaseUrl}/{candidate.Href}";

            string pageHtml;
            try
            {
                pageHtml = await http.GetStringAsync(pageUrl);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [aviso] falha ao abrir {candidate.Title}: {ex.Message}");
                continue;
            }

            var link = SubtitleCatParser.FindSubtitleDownloadLink(pageHtml, "pt-BR")
                       ?? SubtitleCatParser.FindSubtitleDownloadLink(pageHtml, "pt");

            if (link is not null)
            {
                var srtUrl = link.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? link
                    : $"{BaseUrl}/{link.TrimStart('/')}";

                // Evita salvar a mesma legenda duas vezes (candidatos diferentes podem
                // apontar pro mesmo arquivo .srt).
                if (found.Any(f => string.Equals(f.SrtUrl, srtUrl, StringComparison.OrdinalIgnoreCase)))
                    continue;

                found.Add((candidate, srtUrl));
                Console.WriteLine($"  -> pt-BR/pt pronta em: {candidate.Title}");
                continue;
            }

            lastAvailableLangs = SubtitleCatParser.ListAvailableLanguages(pageHtml);
            Console.WriteLine($"  (sem pt-BR pronta em \"{candidate.Title}\", tentando o proximo mais parecido...)");
        }

        if (found.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("[ERRO] Nenhum dos resultados pesquisados tem legenda em Portugues (Brasil) pronta para download.");
            if (lastAvailableLangs.Count > 0)
                Console.WriteLine("Idiomas disponiveis no ultimo resultado verificado: " + string.Join(", ", lastAvailableLangs));
            return Pause(1);
        }

        Console.WriteLine();
        var savedPaths = new List<string>();
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        for (var i = 0; i < found.Count; i++)
        {
            var (candidate, srtUrl) = found[i];

            Console.WriteLine($"Baixando legenda ({i + 1}/{found.Count}): {srtUrl}");
            var srtBytes = await http.GetByteArrayAsync(srtUrl);
            var srtText = utf8NoBom.GetString(srtBytes);

            var cleanResult = SdhCleaner.RemoveSdh(srtText);
            if (cleanResult.BlocksModified > 0 || cleanResult.BlocksRemoved > 0)
            {
                Console.WriteLine($"  Removendo SDH: {cleanResult.BlocksModified} fala(s) limpa(s), " +
                                   $"{cleanResult.BlocksRemoved} bloco(s) 100% SDH removido(s).");
            }

            // A melhor vai com o nome exato do video (pra tocar automaticamente no player).
            // As alternativas ganham sufixo .1, .2 etc — o usuario troca manualmente se a
            // principal estiver fora de sincronia.
            var suffix = i == 0 ? "" : $".{i}";
            var destPath = Path.Combine(folder, fileNameNoExt + suffix + ".srt");
            await File.WriteAllTextAsync(destPath, cleanResult.Srt, utf8NoBom);
            savedPaths.Add(destPath);
        }

        Console.WriteLine();
        Console.WriteLine($"[OK] Legenda salva em: {savedPaths[0]}");
        if (savedPaths.Count > 1)
        {
            Console.WriteLine("Alternativas salvas (troque manualmente se a principal estiver fora de sincronia):");
            foreach (var alt in savedPaths.Skip(1))
                Console.WriteLine($"  - {alt}");
        }
        return Pause(0);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        return http;
    }

    private static int Pause(int code)
    {
        Console.WriteLine("\nPressione qualquer tecla para fechar...");
        try { Console.ReadKey(true); } catch { /* sem console interativo (ex: execucao em pipeline) */ }
        return code;
    }
}
