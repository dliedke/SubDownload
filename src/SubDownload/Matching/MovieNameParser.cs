using System.Text.RegularExpressions;

namespace SubDownload;

/// <summary>
/// Extrai o "nome do filme + ano" a partir do nome de arquivo de video, no mesmo
/// formato usado pela pesquisa do subtitlecat.com (ex: "Coyote.vs.Acme.2026").
/// </summary>
internal static partial class MovieNameParser
{
    // Ano com 4 digitos entre 1900 e 2099, cercado por separadores nao-alfanumericos
    // (ou inicio/fim de string), para nao casar com coisas como "x264" ou "2160p".
    [GeneratedRegex(@"(?<![A-Za-z0-9])(19\d{2}|20\d{2})(?![0-9])")]
    private static partial Regex YearRegex();

    public static string BuildSearchQuery(string fileNameNoExt)
    {
        var match = YearRegex().Match(fileNameNoExt);
        if (!match.Success)
        {
            // Sem ano identificavel: usa o nome inteiro como fallback (melhor esforco).
            return fileNameNoExt;
        }

        var endOfYear = match.Index + match.Length;
        return fileNameNoExt[..endOfYear];
    }

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex TokenSplitRegex();

    public static int? TryGetYear(string fileNameNoExt)
    {
        var match = YearRegex().Match(fileNameNoExt);
        return match.Success ? int.Parse(match.Value) : null;
    }

    /// <summary>
    /// So o titulo do filme, sem o ano, com espacos no lugar dos separadores
    /// (ex: "Coyote.vs.Acme.2026.1080p" -> "Coyote vs Acme"). Sem ano, devolve o nome inteiro.
    /// </summary>
    public static string BuildTitle(string fileNameNoExt)
    {
        var match = YearRegex().Match(fileNameNoExt);
        var title = match.Success ? fileNameNoExt[..match.Index] : fileNameNoExt;
        return string.Join(' ', Tokenize(title));
    }

    // Aceita o resultado so se o nome do arquivo comeca com o nome do filme (evita que a
    // busca "fulltext" traga outro filme que so contem a mesma palavra no titulo).
    public static bool TitleMatches(string fileNameNoExt, string movieName)
    {
        var fileTokens = Tokenize(fileNameNoExt);
        var movieTokens = Tokenize(movieName);
        return movieTokens.Count > 0
               && movieTokens.Count <= fileTokens.Count
               && movieTokens.Select((t, i) => t.Equals(fileTokens[i], StringComparison.OrdinalIgnoreCase)).All(eq => eq);
    }

    private static List<string> Tokenize(string s) =>
        TokenSplitRegex().Split(s).Where(t => t.Length > 0).ToList();
}
