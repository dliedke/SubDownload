using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SubDownload;

/// <summary>
/// Busca legendas pt-BR (com fallback para pt) no OpenSubtitles, usando a API REST
/// legada (rest.opensubtitles.org), que nao exige chave de API nem login. Pesquisa
/// pelo hash do arquivo de video (match exato de release, legenda sincronizada) e
/// pelo nome do filme + ano.
/// </summary>
internal static partial class OpenSubtitlesClient
{
    public const string SourceName = "OpenSubtitles";
    private const string BaseUrl = "https://rest.opensubtitles.org";

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex TokenSplitRegex();

    // O OpenSubtitles injeta blocos de propaganda nas legendas baixadas, ex:
    // "Watch Online Movies and Series for FREE www.osdb.link/lm" ou
    // "Support us and become VIP member to remove all ads from www.OpenSubtitles.org".
    [GeneratedRegex(@"opensubtitles|osdb\.link", RegexOptions.IgnoreCase)]
    private static partial Regex AdRegex();

    private sealed record Result(
        string MatchedBy, string MovieName, string MovieYear, string ReleaseName,
        string SubFileName, string SubSumCD, string SubFormat, string SubBad,
        string SubEncoding, int Downloads, string DownloadLink);

    /// <summary>
    /// Retorna as legendas prontas, da mais indicada para a menos indicada: primeiro as
    /// que casam pelo hash do video, depois as mais parecidas com o nome do arquivo.
    /// So cai para "pt" (Portugal) se nao houver nenhuma "pob" (Brasil).
    /// </summary>
    public static async Task<List<ReadySubtitle>> FindReadySubtitlesAsync(HttpClient http, string videoPath, string fileNameNoExt)
    {
        var searchQuery = MovieNameParser.BuildSearchQuery(fileNameNoExt);
        var year = MovieNameParser.TryGetYear(fileNameNoExt);
        var hash = TryComputeMovieHash(videoPath, out var fileSize);

        foreach (var lang in new[] { "pob", "por" })
        {
            var results = new List<Result>();

            if (hash is not null)
                results.AddRange(await SearchAsync(http, $"moviebytesize-{fileSize}/moviehash-{hash}/sublanguageid-{lang}"));

            var query = string.Join(' ', Tokenize(searchQuery)).ToLowerInvariant();
            results.AddRange(await SearchAsync(http, $"query-{Uri.EscapeDataString(query)}/sublanguageid-{lang}"));

            var fileTokens = Tokenize(fileNameNoExt);
            var accepted = results
                .Where(r => r.SubSumCD == "1" && r.SubBad != "1")
                .Where(r => r.SubFormat.Equals("srt", StringComparison.OrdinalIgnoreCase))
                .Where(r => year is null || r.MovieYear == year.ToString())
                .Where(r => r.MatchedBy == "moviehash" || TitleMatches(fileTokens, r.MovieName))
                .DistinctBy(r => r.DownloadLink)
                .ToList();

            if (accepted.Count == 0)
            {
                Console.WriteLine($"  (nenhuma legenda \"{lang}\" encontrada no OpenSubtitles)");
                continue;
            }

            var ranked = ReleaseMatcher.RankBySimilarity(
                fileNameNoExt,
                accepted.Select(r => new SubtitleCandidate(DisplayTitle(r), r.DownloadLink)).ToList());
            var scoreByLink = ranked.ToDictionary(r => r.Candidate.Href, r => r.Score);

            return accepted
                .OrderByDescending(r => r.MatchedBy == "moviehash")
                .ThenByDescending(r => scoreByLink[r.DownloadLink])
                .ThenByDescending(r => r.Downloads)
                .Select(r => new ReadySubtitle(
                    SourceName,
                    DisplayTitle(r) + (r.MatchedBy == "moviehash" ? " [hash do video]" : ""),
                    r.DownloadLink,
                    client => DownloadTextAsync(client, r)))
                .ToList();
        }

        return new List<ReadySubtitle>();
    }

    private static async Task<List<Result>> SearchAsync(HttpClient http, string path)
    {
        var url = $"{BaseUrl}/search/{path}";
        Console.WriteLine($"Buscando em: {url}");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new List<Result>();

        return doc.RootElement.EnumerateArray()
            .Select(e => new Result(
                Str(e, "MatchedBy"), Str(e, "MovieName"), Str(e, "MovieYear"), Str(e, "MovieReleaseName").Trim(),
                Str(e, "SubFileName"), Str(e, "SubSumCD"), Str(e, "SubFormat"), Str(e, "SubBad"),
                Str(e, "SubEncoding"), int.TryParse(Str(e, "SubDownloadsCnt"), out var n) ? n : 0,
                Str(e, "SubDownloadLink")))
            .Where(r => r.DownloadLink.Length > 0)
            .ToList();
    }

    private static async Task<string> DownloadTextAsync(HttpClient http, Result r)
    {
        var bytes = await http.GetByteArrayAsync(r.DownloadLink);

        // O arquivo vem como .gz; so descompacta se ainda estiver compactado (caso o
        // HttpClient ja tenha descompactado via Content-Encoding).
        if (bytes is [0x1f, 0x8b, ..])
        {
            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var raw = new MemoryStream();
            await gz.CopyToAsync(raw);
            bytes = raw.ToArray();
        }

        // O arquivo vem no encoding original de quem enviou (muitas vezes CP1252).
        // Tenta UTF-8 estrito primeiro; se nao for UTF-8 valido, usa o encoding
        // informado pela API (ou CP1252 como padrao).
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding encoding;
            try
            {
                encoding = Encoding.GetEncoding(r.SubEncoding);
            }
            catch (ArgumentException)
            {
                encoding = Encoding.GetEncoding(1252);
            }
            text = encoding.GetString(bytes);
        }

        return RemoveAds(text);
    }

    // Descarta os blocos inteiros de propaganda; a renumeracao dos blocos fica por conta
    // do SdhCleaner, que roda logo depois.
    private static string RemoveAds(string srt)
    {
        var blocks = srt.Replace("\r\n", "\n").Replace("\r", "\n").Split("\n\n");
        var kept = blocks.Where(b => !AdRegex().IsMatch(b)).ToList();

        var removed = blocks.Length - kept.Count;
        if (removed > 0)
            Console.WriteLine($"  Removendo propaganda do OpenSubtitles: {removed} bloco(s).");

        return string.Join("\n\n", kept);
    }

    /// <summary>
    /// Hash do OpenSubtitles: tamanho do arquivo + soma (uint64, little-endian) dos
    /// primeiros e dos ultimos 64 KB.
    /// </summary>
    private static string? TryComputeMovieHash(string videoPath, out long fileSize)
    {
        const int ChunkSize = 64 * 1024;
        fileSize = 0;
        try
        {
            using var fs = File.OpenRead(videoPath);
            fileSize = fs.Length;
            if (fileSize < ChunkSize * 2)
                return null;

            ulong hash = (ulong)fileSize;
            var buffer = new byte[ChunkSize];
            foreach (var offset in new[] { 0L, fileSize - ChunkSize })
            {
                fs.Seek(offset, SeekOrigin.Begin);
                fs.ReadExactly(buffer);
                for (var i = 0; i < ChunkSize; i += 8)
                    hash += BitConverter.ToUInt64(buffer, i);
            }
            return hash.ToString("x16");
        }
        catch (IOException)
        {
            return null;
        }
    }

    // Aceita o resultado so se o nome do arquivo comeca com o nome do filme (evita que a
    // busca "fulltext" traga outro filme que so contem a mesma palavra no titulo).
    private static bool TitleMatches(List<string> fileTokens, string movieName)
    {
        var movieTokens = Tokenize(movieName);
        return movieTokens.Count > 0
               && movieTokens.Count <= fileTokens.Count
               && movieTokens.Select((t, i) => t.Equals(fileTokens[i], StringComparison.OrdinalIgnoreCase)).All(eq => eq);
    }

    private static string DisplayTitle(Result r) =>
        r.ReleaseName.Length > 0 ? r.ReleaseName : Path.GetFileNameWithoutExtension(r.SubFileName);

    private static List<string> Tokenize(string s) =>
        TokenSplitRegex().Split(s).Where(t => t.Length > 0).ToList();

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
