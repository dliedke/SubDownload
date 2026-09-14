namespace SubDownload;

/// <summary>
/// Uma legenda pt-BR/pt ja pronta para download, vinda de qualquer uma das fontes
/// (OpenSubtitles ou subtitlecat.com). Cada fonte sabe como baixar/decodificar o
/// proprio arquivo, por isso o download fica num delegate.
/// </summary>
internal sealed record ReadySubtitle(
    string Source,
    string Title,
    string Url,
    Func<HttpClient, Task<string>> DownloadTextAsync);
