// SubDownload
// Baixa a legenda em Portugues (Brasil) de um filme, a partir do site subtitlecat.com,
// pesquisando pelo nome do arquivo de video passado como argumento (integracao com o
// menu de contexto do Windows Explorer). Salva o .srt na mesma pasta do video, com o
// mesmo nome do arquivo.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SubDownload;

internal static class Program
{
    private const string BaseUrl = "https://www.subtitlecat.com";
    private const string CleanAltsFlag = "--clean-alts";
    private const string DownloadAltsFlag = "--download-alts";
    private static readonly string[] SupportedExtensions = { ".mkv", ".mp4" };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string? mode = args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0]
            : null;

        var validMode = mode is null
            || string.Equals(mode, CleanAltsFlag, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, DownloadAltsFlag, StringComparison.OrdinalIgnoreCase);

        var pathArgIndex = mode is null ? 0 : 1;

        if (!validMode || args.Length <= pathArgIndex || string.IsNullOrWhiteSpace(args[pathArgIndex]))
        {
            Console.WriteLine("=== SubDownload - legenda PT-BR (subtitlecat.com) ===\n");
            Console.WriteLine("Uso: SubDownload.exe \"caminho\\para\\filme.mkv\"");
            Console.WriteLine($"     SubDownload.exe {DownloadAltsFlag} \"caminho\\para\\filme.mkv\"");
            Console.WriteLine($"     SubDownload.exe {CleanAltsFlag} \"caminho\\para\\filme.mkv\"");
            return Finish(1);
        }

        var videoPath = args[pathArgIndex].Trim('"');

        try
        {
            if (string.Equals(mode, CleanAltsFlag, StringComparison.OrdinalIgnoreCase))
                return RunCleanAlts(videoPath);
            if (string.Equals(mode, DownloadAltsFlag, StringComparison.OrdinalIgnoreCase))
                return await RunDownloadAltsAsync(videoPath);
            return await RunAsync(videoPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"[ERRO] {ex.Message}");
            return Finish(1);
        }
    }

    private const int MaxAlternates = 2; // quantas alternativas o "--download-alts" traz no maximo

    private static async Task<int> RunAsync(string videoPath)
    {
        Console.WriteLine("=== SubDownload - legenda PT-BR (subtitlecat.com) ===\n");

        if (!File.Exists(videoPath))
        {
            Console.WriteLine($"[ERRO] Arquivo nao encontrado: {videoPath}");
            return Finish(1);
        }

        var ext = Path.GetExtension(videoPath);
        if (!SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[ERRO] Extensao nao suportada: {ext} (use .mkv ou .mp4)");
            return Finish(1);
        }

        var fileNameNoExt = Path.GetFileNameWithoutExtension(videoPath);
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;

        Console.WriteLine($"Arquivo:  {fileNameNoExt}{ext}");

        using var http = CreateHttpClient();

        var ranked = await SearchAndRankAsync(http, fileNameNoExt);
        if (ranked is null) return Finish(1);

        // Baixa so a legenda do melhor match (na pratica, quase sempre ja serve). Se ele
        // nao tiver pt-BR (ou pt) JA PRONTA, vai tentando os proximos mais parecidos (sem
        // acionar nenhuma traducao — so usa o que o site ja tem pronto). As demais so sao
        // baixadas sob demanda, pelo menu "Baixar Legendas Alternativas".
        var (found, lastAvailableLangs) = await FindReadySubtitlesAsync(http, ranked, skip: 0, take: 1);

        if (found.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("[ERRO] Nenhum dos resultados pesquisados tem legenda em Portugues (Brasil) pronta para download.");
            if (lastAvailableLangs.Count > 0)
                Console.WriteLine("Idiomas disponiveis no ultimo resultado verificado: " + string.Join(", ", lastAvailableLangs));
            return Finish(1);
        }

        Console.WriteLine();
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var (_, srtUrl) = found[0];

        Console.WriteLine($"Baixando legenda: {srtUrl}");
        var srtBytes = await http.GetByteArrayAsync(srtUrl);
        var srtText = utf8NoBom.GetString(srtBytes);

        var cleanResult = SdhCleaner.RemoveSdh(srtText);
        if (cleanResult.BlocksModified > 0 || cleanResult.BlocksRemoved > 0)
        {
            Console.WriteLine($"  Removendo SDH: {cleanResult.BlocksModified} fala(s) limpa(s), " +
                               $"{cleanResult.BlocksRemoved} bloco(s) 100% SDH removido(s).");
        }

        var destPath = Path.Combine(folder, fileNameNoExt + ".srt");
        await File.WriteAllTextAsync(destPath, cleanResult.Srt, utf8NoBom);

        Console.WriteLine();
        Console.WriteLine($"[OK] Legenda salva em: {destPath}");
        Console.WriteLine("Se ela estiver fora de sincronia, use 'Baixar Legendas Alternativas' no menu do Explorer.");

        PlayVideo(videoPath);
        return Finish(0);
    }

    private static async Task<int> RunDownloadAltsAsync(string videoPath)
    {
        Console.WriteLine("=== SubDownload - legendas alternativas (subtitlecat.com) ===\n");

        if (!File.Exists(videoPath))
        {
            Console.WriteLine($"[ERRO] Arquivo nao encontrado: {videoPath}");
            return Finish(1);
        }

        var ext = Path.GetExtension(videoPath);
        if (!SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[ERRO] Extensao nao suportada: {ext} (use .mkv ou .mp4)");
            return Finish(1);
        }

        var fileNameNoExt = Path.GetFileNameWithoutExtension(videoPath);
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;

        Console.WriteLine($"Arquivo:  {fileNameNoExt}{ext}");

        using var http = CreateHttpClient();

        var ranked = await SearchAndRankAsync(http, fileNameNoExt);
        if (ranked is null) return Finish(1);

        // Pula o melhor match (esse ja foi baixado como legenda principal pelo outro menu)
        // e baixa os proximos mais parecidos que tambem tenham pt-BR/pt pronta.
        var (found, lastAvailableLangs) = await FindReadySubtitlesAsync(http, ranked, skip: 1, take: MaxAlternates);

        if (found.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("[ERRO] Nenhuma legenda alternativa em Portugues (Brasil) pronta para download alem da principal.");
            if (lastAvailableLangs.Count > 0)
                Console.WriteLine("Idiomas disponiveis no ultimo resultado verificado: " + string.Join(", ", lastAvailableLangs));
            return Finish(1);
        }

        Console.WriteLine();
        var savedPaths = new List<string>();
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        for (var i = 0; i < found.Count; i++)
        {
            var (_, srtUrl) = found[i];

            Console.WriteLine($"Baixando legenda alternativa ({i + 1}/{found.Count}): {srtUrl}");
            var srtBytes = await http.GetByteArrayAsync(srtUrl);
            var srtText = utf8NoBom.GetString(srtBytes);

            var cleanResult = SdhCleaner.RemoveSdh(srtText);
            if (cleanResult.BlocksModified > 0 || cleanResult.BlocksRemoved > 0)
            {
                Console.WriteLine($"  Removendo SDH: {cleanResult.BlocksModified} fala(s) limpa(s), " +
                                   $"{cleanResult.BlocksRemoved} bloco(s) 100% SDH removido(s).");
            }

            // Alternativas ganham sufixo .1, .2 etc — o usuario troca manualmente pra .srt
            // se a principal estiver fora de sincronia.
            var destPath = Path.Combine(folder, fileNameNoExt + $".{i + 1}.srt");
            await File.WriteAllTextAsync(destPath, cleanResult.Srt, utf8NoBom);
            savedPaths.Add(destPath);
        }

        Console.WriteLine();
        Console.WriteLine($"[OK] {savedPaths.Count} legenda(s) alternativa(s) salva(s):");
        foreach (var alt in savedPaths)
            Console.WriteLine($"  - {alt}");
        Console.WriteLine("Troque manualmente o nome pra .srt se a principal estiver fora de sincronia.");

        return Finish(0);
    }

    private static async Task<List<ReleaseMatcher.RankedCandidate>?> SearchAndRankAsync(HttpClient http, string fileNameNoExt)
    {
        var searchQuery = MovieNameParser.BuildSearchQuery(fileNameNoExt);
        Console.WriteLine($"Pesquisa: {searchQuery}");

        var searchUrl = $"{BaseUrl}/index.php?search={Uri.EscapeDataString(searchQuery)}";
        Console.WriteLine($"\nBuscando em: {searchUrl}");
        var searchHtml = await http.GetStringAsync(searchUrl);

        var candidates = SubtitleCatParser.ParseSearchResults(searchHtml);
        if (candidates.Count == 0)
        {
            Console.WriteLine("[ERRO] Nenhum resultado encontrado para essa pesquisa.");
            return null;
        }

        Console.WriteLine($"\n{candidates.Count} resultado(s) encontrado(s):");
        foreach (var c in candidates)
            Console.WriteLine($"  - {c.Title}");

        var ranked = ReleaseMatcher.RankBySimilarity(fileNameNoExt, candidates);
        Console.WriteLine($"\n>> Melhor correspondencia: {ranked[0].Candidate.Title}");
        return ranked;
    }

    /// <summary>
    /// Percorre os candidatos ranqueados (do mais parecido ao menos parecido) e coleta
    /// legendas pt-BR/pt JA PRONTAS para download (nunca aciona traducao). Pula os
    /// primeiros <paramref name="skip"/> matches encontrados (ex: o que ja foi baixado
    /// como principal) e para depois de coletar <paramref name="take"/> novos.
    /// </summary>
    private static async Task<(List<(SubtitleCandidate Candidate, string SrtUrl)> Found, List<string> LastAvailableLangs)> FindReadySubtitlesAsync(
        HttpClient http, List<ReleaseMatcher.RankedCandidate> ranked, int skip, int take)
    {
        const int MaxCandidatesToCheck = 20;
        var found = new List<(SubtitleCandidate Candidate, string SrtUrl)>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> lastAvailableLangs = new();
        var matched = 0;

        foreach (var rankedCandidate in ranked.Take(MaxCandidatesToCheck))
        {
            if (found.Count >= take) break;

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

                // Evita contar/salvar a mesma legenda duas vezes (candidatos diferentes
                // podem apontar pro mesmo arquivo .srt).
                if (!seenUrls.Add(srtUrl))
                    continue;

                matched++;
                if (matched <= skip)
                {
                    Console.WriteLine($"  (pulando \"{candidate.Title}\" - ja usada como legenda principal)");
                    continue;
                }

                found.Add((candidate, srtUrl));
                Console.WriteLine($"  -> pt-BR/pt pronta em: {candidate.Title}");
                continue;
            }

            lastAvailableLangs = SubtitleCatParser.ListAvailableLanguages(pageHtml);
            Console.WriteLine($"  (sem pt-BR pronta em \"{candidate.Title}\", tentando o proximo mais parecido...)");
        }

        return (found, lastAvailableLangs);
    }

    private static int RunCleanAlts(string videoPath)
    {
        Console.WriteLine("=== SubDownload - limpar legendas alternativas (.1, .2 ...) ===\n");

        if (!File.Exists(videoPath))
        {
            Console.WriteLine($"[ERRO] Arquivo nao encontrado: {videoPath}");
            return Finish(1);
        }

        var ext = Path.GetExtension(videoPath);
        if (!SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[ERRO] Extensao nao suportada: {ext} (use .mkv ou .mp4)");
            return Finish(1);
        }

        var fileNameNoExt = Path.GetFileNameWithoutExtension(videoPath);
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;

        Console.WriteLine($"Arquivo: {fileNameNoExt}{ext}");

        // As legendas alternativas sao salvas como "NomeDoArquivo.1.srt", "NomeDoArquivo.2.srt"
        // etc. A legenda principal (sem sufixo numerico) nunca e removida por este comando.
        var altPattern = new Regex(
            "^" + Regex.Escape(fileNameNoExt) + @"\.\d+\.srt$",
            RegexOptions.IgnoreCase);

        var toDelete = Directory.EnumerateFiles(folder)
            .Where(f => altPattern.IsMatch(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (toDelete.Count == 0)
        {
            Console.WriteLine("\nNenhuma legenda alternativa (.1.srt, .2.srt ...) encontrada para este arquivo.");
            return Finish(0);
        }

        Console.WriteLine();
        var removed = 0;
        foreach (var path in toDelete)
        {
            try
            {
                File.Delete(path);
                Console.WriteLine($"  [removido] {Path.GetFileName(path)}");
                removed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [ERRO] Falha ao remover {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"[OK] {removed} legenda(s) alternativa(s) removida(s).");
        return Finish(0);
    }

    private static void PlayVideo(string videoPath)
    {
        try
        {
            Console.WriteLine();
            Console.WriteLine("Abrindo o video no player padrao...");
            Process.Start(new ProcessStartInfo(videoPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[aviso] Nao foi possivel abrir o video automaticamente: {ex.Message}");
        }
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

    private static int Finish(int code) => code;
}
