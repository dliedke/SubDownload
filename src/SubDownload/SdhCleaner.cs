using System.Text.RegularExpressions;

namespace SubDownload;

internal readonly record struct SdhCleanResult(string Srt, int BlocksModified, int BlocksRemoved);

/// <summary>
/// Remove marcações SDH (legenda para surdos/deficientes auditivos) de um .srt:
/// descrições de som entre colchetes/parênteses (ex: "[música tocando]",
/// "(risos)") e prefixos de identificação de quem fala em maiúsculas (ex:
/// "ANGIE:"/"[Angie]"). Cobre SDH parcial (só algumas falas marcadas, misturadas
/// com diálogo normal) e SDH total (a legenda inteira é feita assim), inclusive
/// quando a marcação entre colchetes é quebrada em mais de uma linha, ex:
///   [empresário
///   fala indistintamente]
/// </summary>
internal static partial class SdhCleaner
{
    // [^\]] / [^)] tambem casam com quebra de linha (character class), entao isso
    // cobre marcacoes que abrangem mais de uma linha do mesmo bloco.
    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex ParenRegex();

    // "NOME:" ou "- NOME:" no inicio da linha — convencao de SDH para indicar quem
    // esta falando. So casa se o trecho antes dos ":" for todo maiusculo, para nao
    // mexer em dialogo normal.
    [GeneratedRegex(@"^(?<prefix>-\s*|)[A-ZÀ-ÖØ-Þ][A-ZÀ-ÖØ-Þ0-9 .,'’-]{0,30}:\s*")]
    private static partial Regex SpeakerNameRegex();

    // Depois de remover uma marcacao no meio da linha (ex: "- [Tibu] Obrigado."),
    // sobra espaço duplo — colapsa em um só.
    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex ExtraSpaceRegex();

    public static SdhCleanResult RemoveSdh(string srtContent)
    {
        var normalized = srtContent.Replace("\r\n", "\n").Replace("\r", "\n");
        var blocks = normalized.Split("\n\n");

        var output = new List<string>();
        var newIndex = 1;
        var blocksModified = 0;
        var blocksRemoved = 0;

        foreach (var block in blocks)
        {
            if (block.Trim().Length == 0) continue;

            var lines = block.Split('\n');
            var timestampIdx = Array.FindIndex(lines, l => l.Contains("-->"));
            if (timestampIdx < 0) continue; // bloco malformado, nao deveria acontecer em .srt valido

            var timestampLine = lines[timestampIdx];
            var textLines = lines.Skip(timestampIdx + 1).ToArray();
            var originalJoined = string.Join('\n', textLines.Select(l => l.Trim()));

            // Remove descricoes de som — pode abranger varias linhas do bloco.
            var withoutTags = ParenRegex().Replace(BracketRegex().Replace(string.Join('\n', textLines), string.Empty), string.Empty);

            var cleanedTextLines = new List<string>();
            foreach (var rawLine in withoutTags.Split('\n'))
            {
                var line = SpeakerNameRegex().Replace(rawLine, "${prefix}");
                line = ExtraSpaceRegex().Replace(line, " ").Trim();

                // Sobrou so um traco de dialogo (sem nenhuma letra/numero) porque a
                // marcacao SDH que estava ali foi removida — descarta tambem.
                if (line.Length == 0 || !line.Any(char.IsLetterOrDigit))
                    continue;

                cleanedTextLines.Add(line);
            }

            if (cleanedTextLines.Count == 0)
            {
                blocksRemoved++;
                continue; // a fala inteira era SDH -> descarta o bloco inteiro
            }

            var cleanedJoined = string.Join('\n', cleanedTextLines);
            if (cleanedJoined != originalJoined)
                blocksModified++;

            output.Add($"{newIndex}\n{timestampLine}\n{cleanedJoined}");
            newIndex++;
        }

        var result = string.Join("\n\n", output).TrimEnd('\n') + "\n";
        return new SdhCleanResult(result, blocksModified, blocksRemoved);
    }
}
