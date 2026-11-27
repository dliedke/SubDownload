using System.Text;
using System.Text.RegularExpressions;

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
        var episode = EpisodeParser.TryParse(fileNameNoExt);

        // Episodio: se a pesquisa "Serie S01E04" nao bastar, pesquisa so pelo nome da serie.
        // O site corta em 100 resultados e a pesquisa com SxxEyy traz muito resultado de
        // outras series; so com o nome, a lista vem mais focada (ex: "Serie.E04.WEBRIP").
        var queries = new List<string> { MovieNameParser.BuildSearchQuery(fileNameNoExt) };
        if (episode is not null && MovieNameParser.BuildSeriesTitle(fileNameNoExt) is { Length: > 0 } seriesTitle)
            queries.Add(seriesTitle);

        var checkedHrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var yielded = 0;
        List<string> lastAvailableLangs = new();

        foreach (var searchQuery in queries)
        {
            var searchUrl = $"{BaseUrl}/index.php?search={Uri.EscapeDataString(searchQuery)}";
            Console.WriteLine($"Buscando em: {searchUrl}");
            var searchHtml = await http.GetStringAsync(searchUrl);

            var candidates = SubtitleCatParser.ParseSearchResults(searchHtml)
                .Where(c => !checkedHrefs.Contains(c.Href))
                .ToList();
            if (candidates.Count == 0)
            {
                Console.WriteLine("  (nenhum resultado novo no subtitlecat.com para essa pesquisa)");
                continue;
            }

            Console.WriteLine($"\n{candidates.Count} resultado(s) encontrado(s):");
            foreach (var c in candidates)
                Console.WriteLine($"  - {c.Title}");

            var ranked = ReleaseMatcher.RankBySimilarity(fileNameNoExt, candidates);

            if (episode is { } ep)
            {
                ranked = FilterSameEpisode(ranked, ep, MovieNameParser.SeriesTitleWords(fileNameNoExt));
                if (ranked.Count == 0)
                {
                    Console.WriteLine($"  (nenhum resultado do subtitlecat.com e do {ep.Tag} desta serie)");
                    continue;
                }
            }

            Console.WriteLine($"\n>> Melhor correspondencia: {ranked[0].Candidate.Title}");

            foreach (var rankedCandidate in ranked.Take(MaxCandidatesToCheck))
            {
                var candidate = rankedCandidate.Candidate;
                checkedHrefs.Add(candidate.Href);
                var pageUrl = $"{BaseUrl}/{candidate.Href}";
                var pageHtml = await TryGetStringAsync(http, pageUrl, candidate.Title);
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
                yield return new ReadySubtitle(SourceName, candidate.Title, srtUrl, pageUrl, DownloadTextAsync(srtUrl));
            }
        }

        if (yielded == 0 && lastAvailableLangs.Count > 0)
            Console.WriteLine("  Idiomas disponiveis no ultimo resultado verificado: " + string.Join(", ", lastAvailableLangs));
    }

    /// <summary>
    /// Para episodio, so fica com candidatos do mesmo episodio (SxxEyy, NxNN ou numero
    /// absoluto de anime) cujo nome antes da marcacao tenha alguma palavra do nome da
    /// serie (pode estar em outra lingua: "Domestic Girlfriend" x "Domestic na Kanojo").
    /// Quem tem mais palavras da serie vem antes; no empate, mantem a ordem do ranking.
    /// </summary>
    private static List<ReleaseMatcher.RankedCandidate> FilterSameEpisode(
        List<ReleaseMatcher.RankedCandidate> ranked, EpisodeNumber episode, List<string> seriesWords)
    {
        var kept = new List<(ReleaseMatcher.RankedCandidate Candidate, int SeriesWords)>();
        foreach (var r in ranked)
        {
            if (EpisodeParser.Compare(r.Candidate.Title, episode, out var markerIndex) != EpisodeParser.Comparison.Same)
                continue;

            var before = Regex.Split(r.Candidate.Title[..markerIndex], "[^A-Za-z0-9]+")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var common = seriesWords.Count(before.Contains);
            if (seriesWords.Count > 0 && common == 0)
                continue;

            kept.Add((r, common));
        }

        var dropped = ranked.Count - kept.Count;
        if (dropped > 0)
            Console.WriteLine($"  ({dropped} resultado(s) de outra serie/episodio ignorado(s))");

        return kept.OrderByDescending(k => k.SeriesWords).Select(k => k.Candidate).ToList();
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
