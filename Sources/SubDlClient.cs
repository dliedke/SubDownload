using System.IO.Compression;
using System.Net;
using System.Text.Json;

namespace SubDownload;

/// <summary>
/// Busca legendas pt-BR (com fallback para pt) no SubDL (acervo herdado do Subscene),
/// pela API REST oficial (api.subdl.com). Exige uma chave de API gratuita
/// (https://subdl.com/panel/api), configurada em SubDownload.config.json.
/// </summary>
internal static class SubDlClient
{
    public const string SourceName = "SubDL";
    public const string ApiKeyUrl = "https://subdl.com/panel/api";
    private const string ApiUrl = "https://api.subdl.com/api/v1/subtitles";
    private const string DownloadBaseUrl = "https://dl.subdl.com";

    // O Cloudflare do dl.subdl.com responde 403 (desafio JS) para User-Agent de
    // navegador; com um User-Agent de cliente (como faz o Bazarr) o download passa.
    private const string UserAgent = "SubDownload/1.0";

    private const string SiteUrl = "https://subdl.com";

    private sealed record Result(string ReleaseName, string Url, string PageUrl, List<string> Releases);

    /// <summary>
    /// Retorna as legendas prontas, da mais parecida com o nome do arquivo para a menos
    /// parecida. So cai para "PT" (Portugal) se nao houver nenhuma "BR_PT" (Brasil).
    /// </summary>
    public static async Task<List<ReadySubtitle>> FindReadySubtitlesAsync(HttpClient http, string apiKey, string fileNameNoExt)
    {
        var year = MovieNameParser.TryGetYear(fileNameNoExt);

        // Com ano: pesquisa pelo nome do filme + ano. Sem ano (ex: serie "Nome.S01E02"),
        // deixa o SubDL interpretar o nome do arquivo inteiro.
        var query = year is null
            ? $"file_name={Uri.EscapeDataString(fileNameNoExt)}"
            : $"film_name={Uri.EscapeDataString(MovieNameParser.BuildTitle(fileNameNoExt))}&year={year}&type=movie";

        foreach (var lang in new[] { "BR_PT", "PT" })
        {
            var results = await SearchAsync(http, apiKey, $"{query}&languages={lang}&releases=1&subs_per_page=30", fileNameNoExt, year);
            if (results.Count == 0)
            {
                Console.WriteLine($"  (nenhuma legenda \"{lang}\" encontrada no SubDL)");
                continue;
            }

            // Cada legenda pode servir para varias releases; ranqueia todas e fica com a
            // posicao da release mais parecida de cada legenda.
            var candidates = results
                .SelectMany(r => r.Releases.Prepend(r.ReleaseName)
                    .Where(name => name.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(name => new SubtitleCandidate(name, r.Url)))
                .ToList();

            var pageByUrl = results.ToDictionary(r => r.Url, r => r.PageUrl);

            return ReleaseMatcher.RankBySimilarity(fileNameNoExt, candidates)
                .DistinctBy(r => r.Candidate.Href)
                .Select(r =>
                {
                    var url = DownloadBaseUrl + r.Candidate.Href;
                    return new ReadySubtitle(SourceName, r.Candidate.Title, url, pageByUrl[r.Candidate.Href],
                        client => DownloadTextAsync(client, url));
                })
                .ToList();
        }

        return new List<ReadySubtitle>();
    }

    private static async Task<List<Result>> SearchAsync(HttpClient http, string apiKey, string query, string fileNameNoExt, int? year)
    {
        // A chave vai so na requisicao, nunca no log.
        var url = $"{ApiUrl}?{query}";
        Console.WriteLine($"Buscando em: {url}");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}&api_key={Uri.EscapeDataString(apiKey)}");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await http.SendAsync(request);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        if (!(root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.True))
        {
            // "Nenhum resultado" tambem vem como status=false, mas com HTTP 200; chave
            // invalida, limite estourado etc. vem com HTTP de erro.
            var message = root.TryGetProperty("error", out var error) ? error.ToString() : "erro desconhecido";
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new HttpRequestException($"chave de API recusada ({message}) - corrija \"subdlApiKey\" no {AppConfig.FilePath}");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"SubDL respondeu {(int)response.StatusCode}: {message}");
            return new List<Result>();
        }

        // As legendas devolvidas sao todas do primeiro titulo de "results"; confere se
        // ele e mesmo o filme do arquivo (a pesquisa por nome e aproximada).
        if (root.TryGetProperty("results", out var titles) && titles.ValueKind == JsonValueKind.Array && titles.GetArrayLength() > 0)
        {
            var title = titles[0];
            var name = Str(title, "name");
            var titleYear = title.TryGetProperty("year", out var y) ? y.ToString() : "";
            if (!MovieNameParser.TitleMatches(fileNameNoExt, name) || (year is not null && titleYear != year.ToString()))
            {
                Console.WriteLine($"  (o SubDL achou outro titulo: \"{name}\" ({titleYear}), ignorando)");
                return new List<Result>();
            }
        }

        if (!root.TryGetProperty("subtitles", out var subtitles) || subtitles.ValueKind != JsonValueKind.Array)
            return new List<Result>();

        return subtitles.EnumerateArray()
            // Traducao automatica (IA) do proprio SubDL nao conta como legenda pronta.
            .Where(e => !(e.TryGetProperty("ai_translated", out var ai) && ai.ValueKind == JsonValueKind.True))
            .Select(e => new Result(
                Str(e, "release_name").Trim(),
                // A API devolve o link ja com "?api_key=<chave>"; tira a query para a chave
                // nao aparecer no console (o download anonimo funciona sem ela).
                Str(e, "url").Split('?')[0],
                SiteUrl + Str(e, "subtitlePage"),
                e.TryGetProperty("releases", out var rel) && rel.ValueKind == JsonValueKind.Array
                    ? rel.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).ToList()
                    : new List<string>()))
            .Where(r => r.Url.Length > 0)
            .DistinctBy(r => r.Url)
            .ToList();
    }

    private static async Task<string> DownloadTextAsync(HttpClient http, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();

        if (bytes is not [(byte)'P', (byte)'K', ..])
            throw new InvalidDataException("o SubDL devolveu um arquivo que nao e .zip (provavelmente .rar, nao suportado).");

        using var zip = new ZipArchive(new MemoryStream(bytes));
        var entry = zip.Entries
            .Where(e => e.FullName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            .Where(e => !e.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal) && !e.Name.StartsWith("._", StringComparison.Ordinal))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? throw new InvalidDataException("o .zip do SubDL nao contem nenhum arquivo .srt.");

        using var srt = entry.Open();
        using var raw = new MemoryStream();
        await srt.CopyToAsync(raw);
        return SubtitleDecoder.Decode(raw.ToArray());
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
