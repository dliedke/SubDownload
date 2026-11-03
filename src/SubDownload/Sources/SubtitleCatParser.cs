using System.Net;
using System.Text.RegularExpressions;

namespace SubDownload;

internal sealed record SubtitleCandidate(string Title, string Href);

/// <summary>
/// Parsing "manual" (regex) do HTML do subtitlecat.com. Evita depender de uma
/// biblioteca externa de parsing de HTML; o site tem uma estrutura simples e estavel
/// o suficiente para isso.
/// </summary>
internal static partial class SubtitleCatParser
{
    // <a href="subs/1663/Coyote.vs.Acme.2026.1080p.HEVC.x265.RMTeam.html">Coyote...RMTeam</a>
    [GeneratedRegex(@"<a\s+href=""(subs/[^""]+\.html)""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex ResultRowRegex();

    // id="download_pt-BR" ... href="/subs/1663/....srt"
    private static Regex DownloadLinkRegex(string langCode) =>
        new($@"id=""download_{Regex.Escape(langCode)}""[^>]*?href=""([^""]+)""", RegexOptions.Singleline);

    // <img src="/assets/flags/br.png" alt="pt-BR" class="flag">
    [GeneratedRegex(@"alt=""([A-Za-z]{2}(?:-[A-Za-z]{2})?)""\s+class=""flag""")]
    private static partial Regex LanguageFlagRegex();

    public static List<SubtitleCandidate> ParseSearchResults(string html)
    {
        var results = new List<SubtitleCandidate>();
        foreach (Match m in ResultRowRegex().Matches(html))
        {
            var href = m.Groups[1].Value;
            var title = WebUtility.HtmlDecode(StripTags(m.Groups[2].Value)).Trim();
            if (title.Length > 0)
                results.Add(new SubtitleCandidate(title, href));
        }
        return results;
    }

    public static string? FindSubtitleDownloadLink(string pageHtml, string langCode)
    {
        var m = DownloadLinkRegex(langCode).Match(pageHtml);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    public static List<string> ListAvailableLanguages(string pageHtml)
    {
        return LanguageFlagRegex().Matches(pageHtml)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
    }

    private static string StripTags(string s) => Regex.Replace(s, "<.*?>", string.Empty, RegexOptions.Singleline);
}
