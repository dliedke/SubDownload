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
}
