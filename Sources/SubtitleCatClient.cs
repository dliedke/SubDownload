using System.Text;

namespace SubDownload;

/// <summary>
/// Busca legendas pt-BR (com fallback para pt) JA PRONTAS no subtitlecat.com — nunca
/// aciona traducao. As paginas dos candidatos sao abertas sob demanda, do mais parecido
/// com o arquivo local para o menos parecido, conforme o chamador vai consumindo.
/// </summary>
internal static class SubtitleCatClient
{
    public const string SourceName = "subtitlecat.com";
    private const string BaseUrl = "https://www.subtitlecat.com";
    private const int MaxCandidatesToCheck = 20;

    public static async IAsyncEnumerable<ReadySubtitle> FindReadySubtitlesAsync(HttpClient http, string fileNameNoExt)
    {
        var searchQuery = MovieNameParser.BuildSearchQuery(fileNameNoExt);
        var searchUrl = $"{BaseUrl}/index.php?search={Uri.EscapeDataString(searchQuery)}";
        Console.WriteLine($"Buscando em: {searchUrl}");
        var searchHtml = await http.GetStringAsync(searchUrl);

        var candidates = SubtitleCatParser.ParseSearchResults(searchHtml);
        if (candidates.Count == 0)
        {
            Console.WriteLine("  (nenhum resultado encontrado no subtitlecat.com para essa pesquisa)");
            yield break;
        }

        Console.WriteLine($"\n{candidates.Count} resultado(s) encontrado(s):");
        foreach (var c in candidates)
            Console.WriteLine($"  - {c.Title}");

        var ranked = ReleaseMatcher.RankBySimilarity(fileNameNoExt, candidates);
        Console.WriteLine($"\n>> Melhor correspondencia: {ranked[0].Candidate.Title}");

        var yielded = 0;
        List<string> lastAvailableLangs = new();

        foreach (var rankedCandidate in ranked.Take(MaxCandidatesToCheck))
        {
            var candidate = rankedCandidate.Candidate;
            var pageHtml = await TryGetStringAsync(http, $"{BaseUrl}/{candidate.Href}", candidate.Title);
            if (pageHtml is null)
                continue;

            var link = SubtitleCatParser.FindSubtitleDownloadLink(pageHtml, "pt-BR")
                       ?? SubtitleCatParser.FindSubtitleDownloadLink(pageHtml, "pt");

            if (link is null)
            {
                lastAvailableLangs = SubtitleCatParser.ListAvailableLanguages(pageHtml);
                Console.WriteLine($"  (sem pt-BR pronta em \"{candidate.Title}\", tentando o proximo mais parecido...)");
                continue;
            }

            var srtUrl = link.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? link
                : $"{BaseUrl}/{link.TrimStart('/')}";

            yielded++;
            yield return new ReadySubtitle(SourceName, candidate.Title, srtUrl, DownloadTextAsync(srtUrl));
        }

        if (yielded == 0 && lastAvailableLangs.Count > 0)
            Console.WriteLine("  Idiomas disponiveis no ultimo resultado verificado: " + string.Join(", ", lastAvailableLangs));
    }

    private static Func<HttpClient, Task<string>> DownloadTextAsync(string srtUrl) => async http =>
    {
        var srtBytes = await http.GetByteArrayAsync(srtUrl);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(srtBytes);
    };

    private static async Task<string?> TryGetStringAsync(HttpClient http, string url, string title)
    {
        try
        {
            return await http.GetStringAsync(url);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [aviso] falha ao abrir {title}: {ex.Message}");
            return null;
        }
    }
}
