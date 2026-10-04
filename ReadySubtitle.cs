namespace SubDownload;

/// <summary>
/// Uma legenda pt-BR/pt ja pronta para download, vinda de qualquer uma das fontes
/// (OpenSubtitles, SubDL ou subtitlecat.com). Cada fonte sabe como baixar/decodificar o
/// proprio arquivo, por isso o download fica num delegate.
/// </summary>
/// <param name="Url">Link do arquivo baixado (tambem usado no dedupe).</param>
/// <param name="PageUrl">Pagina da legenda no site, para o usuario conferir de onde veio.</param>
internal sealed record ReadySubtitle(
    string Source,
    string Title,
    string Url,
    string PageUrl,
    Func<HttpClient, Task<string>> DownloadTextAsync);
