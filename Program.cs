// SubDownload
// Baixa a legenda em Portugues (Brasil) de um filme — tenta o OpenSubtitles, depois o
// SubDL e, se nao achar, o subtitlecat.com — pesquisando pelo arquivo de video passado como
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
            Console.WriteLine("=== SubDownload - legenda PT-BR (OpenSubtitles / SubDL / subtitlecat.com) ===\n");
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
        Console.WriteLine("=== SubDownload - legenda PT-BR (OpenSubtitles / SubDL / subtitlecat.com) ===\n");

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
        // OpenSubtitles, depois do SubDL e, se nenhum tiver pt-BR/pt, do subtitlecat.com (sem acionar
        // nenhuma traducao — so usa o que ja existe pronto). As demais so sao baixadas sob
        // demanda, pelo menu "Baixar Legendas Alternativas".
        var found = await CollectReadySubtitlesAsync(http, videoPath, fileNameNoExt, skip: 0, take: 1);

        if (found.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("[ERRO] Nenhuma legenda em Portugues (Brasil) pronta para download em nenhuma das fontes (OpenSubtitles, SubDL, subtitlecat.com).");
            return Finish(1);
        }

        Console.WriteLine();
        var destPath = Path.Combine(folder, fileNameNoExt + ".srt");
        await SaveAsync(found[0], destPath, "Salvando legenda");

        Console.WriteLine();
        PrintSaved(destPath, found[0].Info);
        Console.WriteLine();
        Console.WriteLine("Se ela estiver fora de sincronia, use 'Baixar Legendas Alternativas' no menu do Explorer.");

        PlayVideo(videoPath);
        return Finish(0);
    }

    private static async Task<int> RunDownloadAltsAsync(string videoPath)
    {
        Console.WriteLine("=== SubDownload - legendas alternativas (OpenSubtitles / SubDL / subtitlecat.com) ===\n");

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
        // e baixa as proximas, na mesma ordem: OpenSubtitles, SubDL e por fim subtitlecat.com.
        var found = await CollectReadySubtitlesAsync(http, videoPath, fileNameNoExt, skip: 1, take: MaxAlternates);

        if (found.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("[ERRO] Nenhuma legenda alternativa em Portugues (Brasil) pronta para download alem da principal.");
            return Finish(1);
        }

        Console.WriteLine();
        var destPaths = new List<string>();

        for (var i = 0; i < found.Count; i++)
        {
            // Alternativas ganham sufixo .1, .2 etc — o usuario troca manualmente pra .srt
            // se a principal estiver fora de sincronia.
            var destPath = Path.Combine(folder, fileNameNoExt + $".{i + 1}.srt");
            await SaveAsync(found[i], destPath, $"Salvando legenda alternativa ({i + 1}/{found.Count})");
            destPaths.Add(destPath);
        }

        for (var i = 0; i < found.Count; i++)
        {
            Console.WriteLine();
            PrintSaved(destPaths[i], found[i].Info);
        }

        Console.WriteLine();
        Console.WriteLine("Troque manualmente o nome pra .srt se a principal estiver fora de sincronia.");

        return Finish(0);
    }

    /// <summary>
    /// Percorre as legendas pt-BR/pt JA PRONTAS, na ordem de preferencia (OpenSubtitles,
    /// SubDL e subtitlecat.com so se ainda faltar), pulando as primeiras
    /// <paramref name="skip"/> (ex: a que ja foi baixada como principal) e parando depois
    /// de coletar <paramref name="take"/> novas.
    /// </summary>
    private static async Task<List<DownloadedSubtitle>> CollectReadySubtitlesAsync(
        HttpClient http, string videoPath, string fileNameNoExt, int skip, int take)
    {
        var found = new List<DownloadedSubtitle>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matched = 0;

        Console.WriteLine($"Pesquisa: {MovieNameParser.BuildSearchQuery(fileNameNoExt)}");

        await foreach (var subtitle in EnumerateAllSourcesAsync(http, videoPath, fileNameNoExt))
        {
            // Evita contar/salvar a mesma legenda duas vezes (candidatos diferentes
            // podem apontar pro mesmo arquivo .srt).
            if (!seenUrls.Add(subtitle.Url))
                continue;

            // Baixa ja aqui pra conferir o idioma: as fontes as vezes rotulam como pt-BR
            // um arquivo em ingles. Se nao for portugues, descarta e segue pro proximo
            // candidato (e, esgotada a fonte, pra proxima). O skip so conta as aprovadas,
            // entao a ordenacao continua deterministica entre as execucoes.
            string text;
            try
            {
                text = await subtitle.DownloadTextAsync(http);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [aviso] falha ao baixar \"{subtitle.Title}\" ({subtitle.Source}): {ex.Message}");
                continue;
            }

            if (!LanguageDetector.IsPortuguese(text))
            {
                Console.WriteLine($"  (descartando \"{subtitle.Title}\" ({subtitle.Source}) - o arquivo nao esta em portugues)");
                continue;
            }

            matched++;
            if (matched <= skip)
            {
                Console.WriteLine($"  (pulando \"{subtitle.Title}\" - ja usada como legenda principal)");
                continue;
            }

            found.Add(new DownloadedSubtitle(subtitle, text));
            Console.WriteLine($"  -> pt-BR/pt pronta ({subtitle.Source}) em: {subtitle.Title}");
            if (found.Count >= take)
                break;
        }

        return found;
    }

    private static async IAsyncEnumerable<ReadySubtitle> EnumerateAllSourcesAsync(HttpClient http, string videoPath, string fileNameNoExt)
    {
        PrintSourceHeader(OpenSubtitlesClient.SourceName);
        foreach (var subtitle in await TrySearchAsync(OpenSubtitlesClient.SourceName,
                     () => OpenSubtitlesClient.FindReadySubtitlesAsync(http, videoPath, fileNameNoExt)))
            yield return subtitle;

        PrintSourceHeader(SubDlClient.SourceName);
        var subDlApiKey = GetOrAskSubDlApiKey();
        if (subDlApiKey is not null)
        {
            foreach (var subtitle in await TrySearchAsync(SubDlClient.SourceName,
                         () => SubDlClient.FindReadySubtitlesAsync(http, subDlApiKey, fileNameNoExt)))
                yield return subtitle;
        }

        PrintSourceHeader(SubtitleCatClient.SourceName);
        await foreach (var subtitle in SubtitleCatClient.FindReadySubtitlesAsync(http, fileNameNoExt))
            yield return subtitle;
    }

    // Separador bem visivel entre as fontes, com o nome centralizado.
    private static void PrintSourceHeader(string sourceName)
    {
        const int Width = 60;
        var line = new string('_', Width);
        Console.WriteLine();
        Console.WriteLine(line);
        Console.WriteLine(sourceName.PadLeft((Width + sourceName.Length) / 2));
        Console.WriteLine(line);
        Console.WriteLine();
    }

    /// <summary>
    /// Chave do SubDL do config. Se nao houver, explica como gerar e pergunta no console;
    /// a chave colada e salva no config. Devolve null se o SubDL deve ser pulado.
    /// </summary>
    private static string? GetOrAskSubDlApiKey()
    {
        var config = AppConfig.Load();
        if (config.SubDlApiKey is not null)
            return config.SubDlApiKey;

        if (config.SubDlDisabled)
        {
            Console.WriteLine($"  (pulando: SubDL desativado - para ativar, tire o \"subdlDisabled\" do {AppConfig.FilePath})");
            return null;
        }

        if (Console.IsInputRedirected)
        {
            Console.WriteLine($"  (pulando: sem chave de API - gere uma gratis em {SubDlClient.ApiKeyUrl} e coloque em \"subdlApiKey\" no {AppConfig.FilePath})");
            return null;
        }

        Console.WriteLine("  O SubDL precisa de uma chave de API gratuita.");
        Console.WriteLine($"  Crie uma conta e gere a chave em {SubDlClient.ApiKeyUrl}");
        Console.Write("  Cole a chave (ou Enter para pular desta vez, \"n\" para nunca mais perguntar): ");
        var answer = Console.ReadLine()?.Trim().Trim('"', '\'').Trim() ?? "";

        if (answer.Length == 0)
        {
            Console.WriteLine("  (pulando o SubDL desta vez)");
            return null;
        }

        var disable = answer.Equals("n", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (disable)
                AppConfig.DisableSubDl();
            else
                AppConfig.SaveSubDlApiKey(answer);
            Console.WriteLine(disable
                ? $"  SubDL desativado. Para ativar depois, tire o \"subdlDisabled\" do {AppConfig.FilePath}"
                : $"  Chave salva em {AppConfig.FilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [aviso] nao foi possivel salvar {AppConfig.FilePath}: {ex.Message}");
        }

        return disable ? null : answer;
    }

    // Falha numa fonte com API (fora do ar, limite de requisicoes, chave invalida etc.)
    // so vira aviso e nao impede de tentar as fontes seguintes.
    private static async Task<List<ReadySubtitle>> TrySearchAsync(string sourceName, Func<Task<List<ReadySubtitle>>> search)
    {
        try
        {
            return await search();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [aviso] falha ao pesquisar no {sourceName}: {ex.Message}");
            return new List<ReadySubtitle>();
        }
    }

    private static async Task SaveAsync(DownloadedSubtitle subtitle, string destPath, string label)
    {
        Console.WriteLine($"{label} ({subtitle.Info.Source}): {subtitle.Info.Url}");

        var cleanResult = SdhCleaner.RemoveSdh(subtitle.Text);
        if (cleanResult.BlocksModified > 0 || cleanResult.BlocksRemoved > 0)
        {
            Console.WriteLine($"  Removendo SDH: {cleanResult.BlocksModified} fala(s) limpa(s), " +
                               $"{cleanResult.BlocksRemoved} bloco(s) 100% SDH removido(s).");
        }

        await File.WriteAllTextAsync(destPath, cleanResult.Srt, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // Resumo de onde a legenda veio, para o usuario conferir (ou baixar outra na mao).
    private static void PrintSaved(string destPath, ReadySubtitle subtitle)
    {
        Console.WriteLine($"[OK] Legenda salva em: {destPath}");
        Console.WriteLine($"     Fonte:   {subtitle.Source}");
        Console.WriteLine($"     Legenda: {subtitle.Title}");
        if (subtitle.PageUrl.Length > 0)
            Console.WriteLine($"     Pagina:  {subtitle.PageUrl}");
        Console.WriteLine($"     Arquivo: {subtitle.Url}");
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

    // Segura a janela aberta (o menu do Explorer abre um console que fecharia sozinho)
    // para o usuario ler de onde veio a legenda ou o erro.
    private static int Finish(int code)
    {
        if (Console.IsInputRedirected)
            return code;

        Console.WriteLine("\nPressione qualquer tecla para fechar...");
        try { Console.ReadKey(true); } catch (InvalidOperationException) { /* sem console interativo */ }
        return code;
    }

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
