using System.Text.RegularExpressions;

namespace SubDownload;

/// <summary>
/// Episodio cujo arquivo comeca direto pela marcacao (ex: "S01E03-Is It True.mkv") nao
/// tem o nome da serie, e a pesquisa por "S01E03" traz legenda de qualquer serie. Nesse
/// caso o nome da serie (e as tags de release) sai do nome da pasta, ex:
/// "Domestic Girlfriend S01 1080p BDRip x265-EMBER\S01E03-Is It True.mkv" vira
/// "Domestic Girlfriend S01E03 Is It True 1080p BDRip x265 EMBER".
/// </summary>
internal static partial class EpisodeNameResolver
{
    // Arquivo que comeca pela marcacao do episodio (pode ter "[", "-", "." antes).
    [GeneratedRegex(@"^[\s._\-\[]*[Ss]\d{1,2}[ ._-]?[Ee]\d{1,3}(?![0-9])")]
    private static partial Regex LeadingEpisodeRegex();

    // Grupo entre colchetes no inicio da pasta, ex: "[Erai-raws] Serie".
    [GeneratedRegex(@"^\s*\[[^\]]*\]")]
    private static partial Regex LeadingBracketRegex();

    [GeneratedRegex(@"^[Ss]\d{1,2}([Ee]\d{1,3})?$")]
    private static partial Regex SeasonTokenRegex();

    [GeneratedRegex(@"^(19|20)\d{2}$")]
    private static partial Regex YearTokenRegex();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex TokenSplitRegex();

    private static readonly HashSet<string> SeasonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "season", "temporada", "temp",
    };

    private static readonly HashSet<string> PackWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "complete", "completa", "completo",
    };

    // Quantas pastas acima procurar o nome da serie (ex: "Serie\Season 1\S01E03.mkv").
    private const int MaxFolderLevels = 2;

    /// <summary>
    /// Nome a usar na pesquisa: o proprio nome do arquivo ou, se ele comecar pelo
    /// episodio, o nome montado com a pasta. Sem pasta aproveitavel, devolve o original.
    /// </summary>
    public static string Resolve(string videoPath, string fileNameNoExt)
    {
        var lead = LeadingEpisodeRegex().Match(fileNameNoExt);
        if (!lead.Success || !EpisodeParser.TryParse(lead.Value, out var episode, out _, out _))
            return fileNameNoExt;

        var episodeTitle = Tokenize(fileNameNoExt[lead.Length..]);

        var dir = new FileInfo(videoPath).Directory;
        for (var level = 0; level < MaxFolderLevels && dir?.Parent is not null; level++, dir = dir.Parent)
        {
            var (title, tags) = SplitFolderName(dir.Name);
            if (title.Count == 0)
                continue; // pasta so da temporada ("Season 1", "S01"): tenta a de cima

            return string.Join(' ', title.Append(episode.Tag).Concat(episodeTitle).Concat(tags));
        }

        return fileNameNoExt;
    }

    /// <summary>
    /// Separa o nome da pasta em nome da serie (tudo antes da temporada, ano ou primeira
    /// tag de release) e tags de release (o resto, sem temporada/ano).
    /// </summary>
    private static (List<string> Title, List<string> Tags) SplitFolderName(string folderName)
    {
        var tokens = Tokenize(LeadingBracketRegex().Replace(folderName, ""));

        var titleEnd = tokens.FindIndex(t =>
            SeasonTokenRegex().IsMatch(t) || YearTokenRegex().IsMatch(t) || SeasonWords.Contains(t)
            || PackWords.Contains(t) || ReleaseMatcher.IsReleaseTag(t));
        if (titleEnd < 0)
            titleEnd = tokens.Count;

        var tags = new List<string>();
        for (var i = titleEnd; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (SeasonWords.Contains(t) && i + 1 < tokens.Count && tokens[i + 1].All(char.IsAsciiDigit))
            {
                i++; // "Season 1": pula a palavra e o numero
                continue;
            }
            if (!SeasonTokenRegex().IsMatch(t) && !YearTokenRegex().IsMatch(t) && !PackWords.Contains(t))
                tags.Add(t);
        }

        return (tokens.Take(titleEnd).ToList(), tags);
    }

    private static List<string> Tokenize(string s) =>
        TokenSplitRegex().Split(s).Where(t => t.Length > 0).ToList();
}
