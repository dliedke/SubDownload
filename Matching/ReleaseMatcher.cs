using System.Text.RegularExpressions;

namespace SubDownload;

/// <summary>
/// Escolhe, dentre os resultados de pesquisa, o titulo mais parecido com o nome do
/// arquivo local — levando em conta não só o titulo/ano, mas também tags de release
/// (CAM, TS, WEB-DL, 1080p, 2160p, x265, HEVC, nome do grupo, etc).
/// </summary>
internal static partial class ReleaseMatcher
{
    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex TokenSplitRegex();

    // Tags de release que carregam mais "sinal" sobre a fonte/qualidade do rip do que
    // palavras comuns do titulo — recebem peso maior no calculo de similaridade, para
    // que "procurar CAM" realmente prefira um resultado CAM, "2160p" prefira 2160p, etc.
    private static readonly HashSet<string> ReleaseTags = new(StringComparer.OrdinalIgnoreCase)
    {
        // fonte / qualidade de captura
        "cam", "hdcam", "camrip", "ts", "hdts", "tc", "hdtc", "r5", "scr", "dvdscr",
        "screener", "workprint", "pdvd", "dvdrip", "hdrip", "webrip", "web", "webdl",
        "web-dl", "webcap", "bluray", "blu-ray", "bdrip", "brrip", "bdrem", "remux",
        "hdtv", "pdtv", "dcp", "dcprip",
        // resolucao
        "480p", "576p", "720p", "1080p", "1440p", "2160p", "4320p", "4k", "8k", "uhd", "sd", "hd",
        // codec de video
        "x264", "x265", "h264", "h265", "hevc", "avc", "xvid", "divx", "av1", "vp9",
        // audio
        "aac", "ac3", "eac3", "dts", "dtshd", "ddp", "ddp5", "dd5", "atmos", "truehd", "flac",
        // extras comuns
        "hdr", "hdr10", "dv", "10bit", "8bit", "multi", "dual", "extended", "unrated",
        "proper", "repack", "internal", "limited", "imax",
    };

    public readonly record struct RankedCandidate(SubtitleCandidate Candidate, double Score, int IntersectionCount);

    /// <summary>
    /// Ordena todos os resultados do mais parecido para o menos parecido com o arquivo
    /// local. Usado tanto para escolher o "melhor match" quanto para, se ele não tiver
    /// legenda pt-BR pronta, tentar o próximo mais parecido em vez de desistir.
    /// </summary>
    public static List<RankedCandidate> RankBySimilarity(string sourceFileNameNoExt, IReadOnlyList<SubtitleCandidate> candidates)
    {
        var sourceTokens = Tokenize(sourceFileNameNoExt);

        return candidates
            .Select(c =>
            {
                var score = WeightedJaccard(sourceTokens, Tokenize(c.Title), out var intersectionCount);
                return new RankedCandidate(c, score, intersectionCount);
            })
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.IntersectionCount)
            .ToList();
    }

    private static double WeightedJaccard(HashSet<string> a, HashSet<string> b, out int intersectionCount)
    {
        var intersection = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        intersection.IntersectWith(b);
        intersectionCount = intersection.Count;

        var union = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        union.UnionWith(b);

        if (union.Count == 0)
            return 0;

        double intersectionWeight = intersection.Sum(Weight);
        double unionWeight = union.Sum(Weight);

        return unionWeight == 0 ? 0 : intersectionWeight / unionWeight;
    }

    private static double Weight(string token) => ReleaseTags.Contains(token) ? 3.0 : 1.0;

    private static HashSet<string> Tokenize(string s) =>
        TokenSplitRegex().Split(s)
            .Where(t => t.Length > 0)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();
}
