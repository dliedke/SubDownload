using System.Text.RegularExpressions;

namespace SubDownload;

/// <summary>Temporada e episodio de uma serie (ex: S01E03).</summary>
internal readonly record struct EpisodeNumber(int Season, int Episode)
{
    public string Tag => $"S{Season:00}E{Episode:00}";
}

/// <summary>
/// Reconhece a marcacao de episodio em nomes de arquivo/legenda ("S01E03", "s1e3",
/// "1x03" ou, em anime, a numeracao absoluta "Serie - 03") e compara com o episodio
/// do video, para nao aceitar legenda de outro episodio/temporada.
/// </summary>
internal static partial class EpisodeParser
{
    // S01E03, s1e3, S01.E03, S01 E03
    [GeneratedRegex(@"(?<![A-Za-z0-9])[Ss](\d{1,2})[ ._-]?[Ee](\d{1,3})(?![0-9])")]
    private static partial Regex SeasonEpisodeRegex();

    // 1x03, 01x03 (o limite de digitos evita casar com resolucao, ex: 1920x1080)
    [GeneratedRegex(@"(?<![A-Za-z0-9])(\d{1,2})[xX](\d{2,3})(?![A-Za-z0-9])")]
    private static partial Regex CrossRegex();

    // E04, Ep04, Ep.04, EP 04, Episode 4, Episodio 4
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:E|Ep|Episode|Episodio)[ ._-]?(\d{1,3})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex OnlyEpisodeRegex();

    // Numero solto de ate 3 digitos (numeracao absoluta de anime: "Serie - 03 [720p]")
    [GeneratedRegex(@"(?<![A-Za-z0-9])(\d{1,3})(?![A-Za-z0-9])")]
    private static partial Regex AbsoluteNumberRegex();

    public enum Comparison { Unknown, Same, Different }

    /// <summary>A marcacao SxxEyy (so esse formato) e onde ela esta no nome.</summary>
    public static bool TryParse(string name, out EpisodeNumber episode, out int index, out int length)
    {
        var m = SeasonEpisodeRegex().Match(name);
        episode = m.Success ? new EpisodeNumber(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : default;
        index = m.Success ? m.Index : -1;
        length = m.Success ? m.Length : 0;
        return m.Success;
    }

    public static EpisodeNumber? TryParse(string name) =>
        TryParse(name, out var episode, out _, out _) ? episode : null;

    /// <summary>
    /// Diz se o titulo e do mesmo episodio. <paramref name="markerIndex"/> e onde a
    /// marcacao comeca: o que vem antes dela costuma ser o nome da serie.
    /// </summary>
    public static Comparison Compare(string title, EpisodeNumber episode, out int markerIndex)
    {
        foreach (var regex in new[] { SeasonEpisodeRegex(), CrossRegex() })
        {
            var m = regex.Match(title);
            if (!m.Success)
                continue;

            markerIndex = m.Index;
            return int.Parse(m.Groups[1].Value) == episode.Season && int.Parse(m.Groups[2].Value) == episode.Episode
                ? Comparison.Same
                : Comparison.Different;
        }

        // So o episodio, sem temporada: "E04", "Ep04", "Episode 4" (compara so o numero).
        var onlyEpisode = OnlyEpisodeRegex().Match(title);
        if (onlyEpisode.Success)
        {
            markerIndex = onlyEpisode.Index;
            return int.Parse(onlyEpisode.Groups[1].Value) == episode.Episode ? Comparison.Same : Comparison.Different;
        }

        // Sem marcacao nenhuma: usa o primeiro numero solto como episodio (anime), desde que venha
        // logo depois do nome (antes de ano/tag de release) e nao seja decimal ("AAC5.1").
        var abs = AbsoluteNumberRegex().Match(title);
        markerIndex = abs.Success ? abs.Index : -1;
        if (!abs.Success || !LooksLikeAbsoluteEpisode(title, abs))
            return Comparison.Unknown;
        return int.Parse(abs.Groups[1].Value) == episode.Episode ? Comparison.Same : Comparison.Different;
    }

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex TokenSplitRegex();

    [GeneratedRegex(@"^(19|20)\d{2}$")]
    private static partial Regex YearTokenRegex();

    private static bool LooksLikeAbsoluteEpisode(string title, Match number)
    {
        var end = number.Index + number.Length;
        var decimalPart = (number.Index >= 2 && title[number.Index - 1] == '.' && char.IsAsciiDigit(title[number.Index - 2]))
                          || (end + 1 < title.Length && title[end] == '.' && char.IsAsciiDigit(title[end + 1]));
        if (decimalPart)
            return false;

        return !TokenSplitRegex().Split(title[..number.Index])
            .Any(t => YearTokenRegex().IsMatch(t) || ReleaseMatcher.IsReleaseTag(t));
    }
}
