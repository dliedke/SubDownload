using System.Text;

namespace SubDownload;

/// <summary>
/// Converte os bytes de uma legenda baixada em texto. As legendas vem no encoding
/// original de quem enviou (muitas vezes CP1252), entao tenta UTF-8 estrito primeiro e,
/// se nao for UTF-8 valido, usa o encoding informado pela fonte (ou CP1252 como padrao).
/// </summary>
internal static class SubtitleDecoder
{
    public static string Decode(byte[] bytes, string? encodingName = null)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding encoding;
            try
            {
                encoding = Encoding.GetEncoding(encodingName ?? "");
            }
            catch (ArgumentException)
            {
                encoding = Encoding.GetEncoding(1252);
            }
            return encoding.GetString(bytes);
        }
    }
}
