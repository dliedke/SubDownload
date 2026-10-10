using System.Text.RegularExpressions;

namespace SubDownload;

/// <summary>
/// Confere se o texto de uma legenda baixada esta mesmo em portugues. Algumas fontes
/// rotulam como pt-BR arquivos em ingles, entao conta palavras funcionais tipicas de
/// cada idioma (sem lib de deteccao) e so aceita se o portugues vencer.
/// </summary>
internal static partial class LanguageDetector
{
    private static readonly HashSet<string> PortugueseWords = new(StringComparer.Ordinal)
    {
        "que", "não", "nao", "você", "voce", "vocês", "uma", "para", "pra", "com", "está", "esta",
        "isso", "eu", "mas", "os", "as", "do", "da", "dos", "das", "em", "ele", "ela", "são",
        "tem", "meu", "minha", "aqui", "foi", "ser", "mais", "muito", "já", "seu", "sua", "vai",
        "só", "então", "estou", "tudo", "bem", "nós", "obrigado", "porque", "como", "quando",
    };

    private static readonly HashSet<string> EnglishWords = new(StringComparer.Ordinal)
    {
        "the", "and", "you", "that", "is", "to", "of", "it", "this", "what", "have", "for", "with",
        "not", "are", "was", "your", "they", "he", "she", "but", "we", "be", "i'm", "don't", "it's",
        "you're", "that's", "can't", "i'll", "there", "just", "know", "going", "would", "about",
    };

    public static bool IsPortuguese(string srtText)
    {
        var portuguese = 0;
        var english = 0;

        foreach (Match m in WordRegex().Matches(srtText.Replace('’', '\'')))
        {
            var word = m.Value.ToLowerInvariant();
            if (PortugueseWords.Contains(word))
                portuguese++;
            else if (EnglishWords.Contains(word))
                english++;
        }

        return portuguese > english;
    }

    [GeneratedRegex(@"\p{L}+(?:'\p{L}+)?")]
    private static partial Regex WordRegex();
}
