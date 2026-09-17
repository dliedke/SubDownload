// SubDownload
// Baixa a legenda em Portugues (Brasil) de um filme — primeiro tenta o OpenSubtitles e,
// se nao achar, o subtitlecat.com — pesquisando pelo arquivo de video passado como
// argumento (integracao com o menu de contexto do Windows Explorer). Salva o .srt na
// mesma pasta do video, com o mesmo nome do arquivo.

using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SubDownload;

internal static class Program
{
    private const string CleanAltsFlag = "--clean-alts";
    private const string DownloadAltsFlag = "--download-alts";
    private static readonly string[] SupportedExtensions = { ".mkv", ".mp4" };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string? mode = args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0]
            : null;

        var validMode = mode is null
            || string.Equals(mode, CleanAltsFlag, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, DownloadAltsFlag, StringComparison.OrdinalIgnoreCase);

        var pathArgIndex = mode is null ? 0 : 1;

        if (!validMode || args.Length <= pathArgIndex || string.IsNullOrWhiteSpace(args[pathArgIndex]))
        {
            Console.WriteLine("=== SubDownload - legenda PT-BR (OpenSubtitles / subtitlecat.com) ===\n");
            Console.WriteLine("Uso: SubDownload.exe \"caminho\\para\\filme.mkv\"");
            Console.WriteLine($"     SubDownload.exe {DownloadAltsFlag} \"caminho\\para\\filme.mkv\"");
            Console.WriteLine($"     SubDownload.exe {CleanAltsFlag} \"caminho\\para\\filme.mkv\"");
            return Finish(1);
        }

        // O Explorer, ao chamar o menu de contexto, substitui o caminho pelo nome curto
        // 8.3 (ex: STARTR~1.MKV) quando o caminho completo e muito longo — a substituicao
        // de %1 no shell32 usa um buffer de tamanho fixo, e isso acontece mesmo com o app
        // marcado como longPathAware no manifest. Aqui reconstruimos o nome real do arquivo.
        var videoPath = ExpandLongPath(args[pathArgIndex].Trim('"'));

        try
        {
            if (string.Equals(mode, CleanAltsFlag, StringComparison.OrdinalIgnoreCase))
                return RunCleanAlts(videoPath);
            if (string.Equals(mode, DownloadAltsFlag, StringComparison.OrdinalIgnoreCase))
                return await RunDownloadAltsAsync(videoPath);
            return await RunAsync(videoPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"[ERRO] {ex.Message}");
            return Finish(1);
        }
    }

    private const int MaxAlternates = 2; // quantas alternativas o "--download-alts" traz no maximo

    private static async Task<int> RunAsync(string videoPath)
    {
        Console.WriteLine("=== SubDownload - legenda PT-BR (OpenSubtitles / subtitlecat.com) ===\n");

        if (!File.Exists(videoPath))
        {
            Console.WriteLine($"[ERRO] Arquivo nao encontrado: {videoPath}");
            return Finish(1);
        }

        var ext = Path.GetExtension(videoPath);
        if (!SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[ERRO] Extensao nao suportada: {ext} (use .mkv ou .mp4)");
            return Finish(1);
        }

        var fileNameNoExt = Path.GetFileNameWithoutExtension(videoPath);
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;

        Console.WriteLine($"Arquivo:  {fileNameNoExt}{ext}");

        using var http = CreateHttpClient();

        // Baixa so a legenda mais indicada (na pratica, quase sempre ja serve): primeiro do
        // OpenSubtitles e, se ele nao tiver pt-BR/pt, do subtitlecat.com (sem acionar
        // nenhuma traducao — so usa o que ja existe pronto). As demais so sao baixadas sob
        // demanda, pelo menu "Baixar Legendas Alternativas".
        var found = await CollectReadySubtitlesAsync(http, videoPath, fileNameNoExt, skip: 0, take: 1);

        if (found.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("[ERRO] Nenhuma legenda em Portugues (Brasil) pronta para download no OpenSubtitles nem no subtitlecat.com.");
            return Finish(1);
        }

        Console.WriteLine();
        var destPath = Path.Combine(folder, fileNameNoExt + ".srt");
        await DownloadAndSaveAsync(http, found[0], destPath, "Baixando legenda");

        Console.WriteLine();
        Console.WriteLine($"[OK] Legenda salva em: {destPath}");
        Console.WriteLine("Se ela estiver fora de sincronia, use 'Baixar Legendas Alternativas' no menu do Explorer.");

        PlayVideo(videoPath);
        return Finish(0);
    }

    private static async Task<int> RunDownloadAltsAsync(string videoPath)
    {
        Console.WriteLine("=== SubDownload - legendas alternativas (OpenSubtitles / subtitlecat.com) ===\n");

        if (!File.Exists(videoPath))
        {
            Console.WriteLine($"[ERRO] Arquivo nao encontrado: {videoPath}");
            return Finish(1);
        }

        var ext = Path.GetExtension(videoPath);
        if (!SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[ERRO] Extensao nao suportada: {ext} (use .mkv ou .mp4)");
            return Finish(1);
        }

        var fileNameNoExt = Path.GetFileNameWithoutExtension(videoPath);
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;

        Console.WriteLine($"Arquivo:  {fileNameNoExt}{ext}");

        using var http = CreateHttpClient();

        // Pula a mais indicada (essa ja foi baixada como legenda principal pelo outro menu)
        // e baixa as proximas, na mesma ordem: OpenSubtitles primeiro, depois subtitlecat.com.
        var found = await CollectReadySubtitlesAsync(http, videoPath, fileNameNoExt, skip: 1, take: MaxAlternates);

        if (found.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("[ERRO] Nenhuma legenda alternativa em Portugues (Brasil) pronta para download alem da principal.");
            return Finish(1);
        }

        Console.WriteLine();
        var savedPaths = new List<string>();

        for (var i = 0; i < found.Count; i++)
        {
            // Alternativas ganham sufixo .1, .2 etc — o usuario troca manualmente pra .srt
            // se a principal estiver fora de sincronia.
            var destPath = Path.Combine(folder, fileNameNoExt + $".{i + 1}.srt");
            await DownloadAndSaveAsync(http, found[i], destPath, $"Baixando legenda alternativa ({i + 1}/{found.Count})");
            savedPaths.Add(destPath);
        }

        Console.WriteLine();
        Console.WriteLine($"[OK] {savedPaths.Count} legenda(s) alternativa(s) salva(s):");
        foreach (var alt in savedPaths)
            Console.WriteLine($"  - {alt}");
        Console.WriteLine("Troque manualmente o nome pra .srt se a principal estiver fora de sincronia.");

        return Finish(0);
    }

    /// <summary>
    /// Percorre as legendas pt-BR/pt JA PRONTAS, na ordem de preferencia (OpenSubtitles
    /// primeiro, subtitlecat.com so se ainda faltar), pulando as primeiras
    /// <paramref name="skip"/> (ex: a que ja foi baixada como principal) e parando depois
    /// de coletar <paramref name="take"/> novas.
    /// </summary>
    private static async Task<List<ReadySubtitle>> CollectReadySubtitlesAsync(
        HttpClient http, string videoPath, string fileNameNoExt, int skip, int take)
    {
        var found = new List<ReadySubtitle>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matched = 0;

        Console.WriteLine($"Pesquisa: {MovieNameParser.BuildSearchQuery(fileNameNoExt)}");

        await foreach (var subtitle in EnumerateAllSourcesAsync(http, videoPath, fileNameNoExt))
        {
            // Evita contar/salvar a mesma legenda duas vezes (candidatos diferentes
            // podem apontar pro mesmo arquivo .srt).
            if (!seenUrls.Add(subtitle.Url))
                continue;

            matched++;
            if (matched <= skip)
            {
                Console.WriteLine($"  (pulando \"{subtitle.Title}\" - ja usada como legenda principal)");
                continue;
            }

            found.Add(subtitle);
            Console.WriteLine($"  -> pt-BR/pt pronta ({subtitle.Source}) em: {subtitle.Title}");
            if (found.Count >= take)
                break;
        }

        return found;
    }

    private static async IAsyncEnumerable<ReadySubtitle> EnumerateAllSourcesAsync(HttpClient http, string videoPath, string fileNameNoExt)
    {
        Console.WriteLine($"\n--- {OpenSubtitlesClient.SourceName} ---");
        List<ReadySubtitle> openSubtitles;
        try
        {
            openSubtitles = await OpenSubtitlesClient.FindReadySubtitlesAsync(http, videoPath, fileNameNoExt);
        }
        catch (Exception ex)
        {
            // Falha no OpenSubtitles (fora do ar, limite de requisicoes etc.) nao impede
            // de tentar o subtitlecat.com.
            Console.WriteLine($"  [aviso] falha ao pesquisar no OpenSubtitles: {ex.Message}");
            openSubtitles = new List<ReadySubtitle>();
        }

        foreach (var subtitle in openSubtitles)
            yield return subtitle;

        Console.WriteLine($"\n--- {SubtitleCatClient.SourceName} ---");
        await foreach (var subtitle in SubtitleCatClient.FindReadySubtitlesAsync(http, fileNameNoExt))
            yield return subtitle;
    }

    private static async Task DownloadAndSaveAsync(HttpClient http, ReadySubtitle subtitle, string destPath, string label)
    {
        Console.WriteLine($"{label} ({subtitle.Source}): {subtitle.Url}");
        var srtText = await subtitle.DownloadTextAsync(http);

        var cleanResult = SdhCleaner.RemoveSdh(srtText);
        if (cleanResult.BlocksModified > 0 || cleanResult.BlocksRemoved > 0)
        {
            Console.WriteLine($"  Removendo SDH: {cleanResult.BlocksModified} fala(s) limpa(s), " +
                               $"{cleanResult.BlocksRemoved} bloco(s) 100% SDH removido(s).");
        }

        await File.WriteAllTextAsync(destPath, cleanResult.Srt, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static int RunCleanAlts(string videoPath)
    {
        Console.WriteLine("=== SubDownload - limpar legendas alternativas (.1, .2 ...) ===\n");

        if (!File.Exists(videoPath))
        {
            Console.WriteLine($"[ERRO] Arquivo nao encontrado: {videoPath}");
            return Finish(1);
        }

        var ext = Path.GetExtension(videoPath);
        if (!SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[ERRO] Extensao nao suportada: {ext} (use .mkv ou .mp4)");
            return Finish(1);
        }

        var fileNameNoExt = Path.GetFileNameWithoutExtension(videoPath);
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;

        Console.WriteLine($"Arquivo: {fileNameNoExt}{ext}");

        // As legendas alternativas sao salvas como "NomeDoArquivo.1.srt", "NomeDoArquivo.2.srt"
        // etc. A legenda principal (sem sufixo numerico) nunca e removida por este comando.
        var altPattern = new Regex(
            "^" + Regex.Escape(fileNameNoExt) + @"\.\d+\.srt$",
            RegexOptions.IgnoreCase);

        var toDelete = Directory.EnumerateFiles(folder)
            .Where(f => altPattern.IsMatch(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (toDelete.Count == 0)
        {
            Console.WriteLine("\nNenhuma legenda alternativa (.1.srt, .2.srt ...) encontrada para este arquivo.");
            return Finish(0);
        }

        Console.WriteLine();
        var removed = 0;
        foreach (var path in toDelete)
        {
            try
            {
                File.Delete(path);
                Console.WriteLine($"  [removido] {Path.GetFileName(path)}");
                removed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [ERRO] Falha ao remover {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"[OK] {removed} legenda(s) alternativa(s) removida(s).");
        return Finish(0);
    }

    private static void PlayVideo(string videoPath)
    {
        try
        {
            Console.WriteLine();
            Console.WriteLine("Abrindo o video no player padrao...");
            Process.Start(new ProcessStartInfo(videoPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[aviso] Nao foi possivel abrir o video automaticamente: {ex.Message}");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        return http;
    }

    private static int Finish(int code) => code;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetLongPathName(string shortPath, StringBuilder longPathBuffer, int bufferLength);

    /// <summary>
    /// Troca um nome curto 8.3 (STARTR~1.MKV) pelo nome longo real do arquivo. Se o
    /// caminho ja for o nome longo, ou se o arquivo nao existir (ex: caminho digitado
    /// errado), devolve o valor original sem erro.
    /// </summary>
    private static string ExpandLongPath(string path)
    {
        try
        {
            var buffer = new StringBuilder(260);
            var length = GetLongPathName(path, buffer, buffer.Capacity);
            if (length > buffer.Capacity)
            {
                // Nome longo nao coube no buffer inicial (caminho > MAX_PATH); tenta de novo
                // com o tamanho exato que a API pediu.
                buffer = new StringBuilder(length);
                length = GetLongPathName(path, buffer, buffer.Capacity);
            }

            return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
        }
        catch (Exception)
        {
            // DllNotFoundException/EntryPointNotFoundException etc. nunca devem impedir
            // o programa de rodar com o caminho original.
            return path;
        }
    }
}
