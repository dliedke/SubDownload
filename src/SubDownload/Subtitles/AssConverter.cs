using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SubDownload;

/// <summary>
/// Converte legenda ASS/SSA (Aegisub) para SRT. O subtitlecat.com as vezes serve um .ass
/// com extensao .srt — e traduzido por maquina por inteiro, inclusive os nomes dos campos
/// ("Diálogo:", "padrão") e, em algumas linhas, a pontuacao dos tempos
/// ("Diálogo: 0.0: 00: 07.16.0: 00: 09.56, padrão ,, 0,0,0 ,, texto"). Por isso o
/// parsing e tolerante: acha os dois primeiros tempos da linha e pega o texto depois
/// dos campos de estilo/margem.
/// </summary>
internal static partial class AssConverter
{
    // "Dialogue:", "Diálogo:", "Diálogo :" — rotulo da linha de evento.
    [GeneratedRegex(@"^\s*(\p{L}+)\s*:")]
    private static partial Regex EventLabelRegex();

    // h:mm:ss.cc (tolerando espacos que a traducao enfia depois dos ":"/".").
    [GeneratedRegex(@"(\d{1,2})\s*:\s*(\d{2})\s*:\s*(\d{2})\s*[.,]\s*(\d{2,3})")]
    private static partial Regex TimeRegex();

    // Tags de formatacao: {\an8}, {\i1}, "{\ an8}" (com espaco da traducao).
    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex OverrideTagRegex();

    [GeneratedRegex(@"\\\s*[Nn]")]
    private static partial Regex LineBreakRegex();

    // {\p1}: desenho vetorial, nao e fala.
    [GeneratedRegex(@"\\p[1-9]")]
    private static partial Regex DrawingRegex();

    private readonly record struct Cue(TimeSpan Start, TimeSpan End, string Text);

    /// <summary>
    /// Se o texto parece ASS/SSA (e nao SRT), devolve o SRT convertido; senao, null.
    /// </summary>
    public static string? TryConvertToSrt(string text)
    {
        if (text.Contains("-->"))
            return null; // ja e SRT

        var cues = new List<Cue>();
        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var label = EventLabelRegex().Match(rawLine);
            // "Comment:"/"Comentário:" sao eventos desativados.
            if (!label.Success || label.Groups[1].Value.StartsWith("Coment", StringComparison.OrdinalIgnoreCase))
                continue;

            var times = TimeRegex().Matches(rawLine, label.Length);
            if (times.Count < 2)
                continue;

            // Depois do tempo final: ",Estilo,Nome,MargemL,MargemR,MargemV,Efeito,Texto". Se a
            // traducao comeu alguma virgula, fica com o ultimo campo.
            var rest = rawLine[(times[1].Index + times[1].Length)..];
            var fields = rest.Split(',', 8);
            var rawText = fields.Length == 8 ? fields[7] : fields[^1];

            if (DrawingRegex().IsMatch(rawText))
                continue;

            var cueText = LineBreakRegex().Replace(OverrideTagRegex().Replace(rawText, ""), "\n")
                .Replace("\\h", " ");
            cueText = string.Join('\n', cueText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            if (cueText.Length == 0)
                continue;

            cues.Add(new Cue(ParseTime(times[0]), ParseTime(times[1]), cueText));
        }

        if (cues.Count == 0)
            return null;

        var sb = new StringBuilder();
        var index = 1;
        // ASS costuma repetir a mesma fala em varias camadas (efeitos): tira as repetidas.
        foreach (var cue in cues.Distinct().OrderBy(c => c.Start))
        {
            sb.Append(index++).Append('\n')
              .Append(FormatTime(cue.Start)).Append(" --> ").Append(FormatTime(cue.End)).Append('\n')
              .Append(cue.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    private static TimeSpan ParseTime(Match m)
    {
        var fraction = m.Groups[4].Value;
        var ms = fraction.Length == 2 ? int.Parse(fraction) * 10 : int.Parse(fraction);
        return new TimeSpan(0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), ms);
    }

    private static string FormatTime(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");
}
