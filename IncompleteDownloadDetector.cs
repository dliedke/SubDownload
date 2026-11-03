// Detecta video que ainda nao terminou de baixar (torrent) ou de copiar, para o modo
// principal nao abrir o player a toa. E tudo heuristico e "na duvida, esta completo":
// qualquer erro de leitura ou formato desconhecido devolve null (= abre o player).

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SubDownload;

internal static class IncompleteDownloadDetector
{
    private const int ZeroSampleCount = 32;      // amostras espalhadas pelo arquivo (+1 no fim)
    private const int ZeroSampleSize = 64 * 1024; // video comprimido nunca tem 64 KB seguidos de zeros
    private const int MaxMp4Boxes = 64;           // MP4 fragmentado tem milhares; nao vale varrer tudo

    /// <summary>
    /// Devolve o motivo (para mostrar ao usuario) se o video parece incompleto, ou null.
    /// </summary>
    public static string? Check(string videoPath)
    {
        try
        {
            // FileShare.ReadWrite: nao atrapalha o cliente de torrent/copia que estiver escrevendo.
            using var fs = new FileStream(videoPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 0, FileOptions.RandomAccess);
            var length = fs.Length;
            if (length == 0)
                return "o arquivo esta vazio";

            return CheckTruncated(fs, length, Path.GetExtension(videoPath))
                ?? CheckSparseHoles(fs, videoPath, length)
                ?? CheckZeroBlocks(fs, length);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- 1) cabecalho declara um tamanho maior que o arquivo (download sequencial) ----

    private static string? CheckTruncated(FileStream fs, long length, string ext)
    {
        long? expected = ext.ToLowerInvariant() switch
        {
            ".mkv" => MkvExpectedLength(fs, length),
            ".mp4" => Mp4ExpectedMinLength(fs, length),
            _ => null
        };

        return expected is { } e && e > length
            ? $"o arquivo tem {FormatMb(length)}, mas o cabecalho indica pelo menos {FormatMb(e)}"
            : null;
    }

    // MKV: EBML header (1A 45 DF A3) seguido do Segment (18 53 80 67), cujo tamanho declarado
    // diz onde o arquivo termina. Gravadores ao vivo usam tamanho "desconhecido" (tudo 1): ignora.
    private static long? MkvExpectedLength(FileStream fs, long length)
    {
        var head = new byte[(int)Math.Min(512, length)];
        fs.Position = 0;
        fs.ReadExactly(head);
        ReadOnlySpan<byte> data = head;

        if (!data.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]))
            return null;
        var pos = 4;
        if (!TryReadVint(data, ref pos, out var ebmlSize, out _) || ebmlSize > data.Length)
            return null;
        pos += (int)ebmlSize;

        if (pos + 4 > data.Length || !data[pos..].StartsWith((ReadOnlySpan<byte>)[0x18, 0x53, 0x80, 0x67]))
            return null;
        pos += 4;
        if (!TryReadVint(data, ref pos, out var segmentSize, out var unknown) || unknown)
            return null;

        return pos + segmentSize;
    }

    // Inteiro de tamanho variavel do EBML: o numero de zeros a esquerda do 1o byte da o
    // comprimento (1..8); o bit marcador e descartado.
    private static bool TryReadVint(ReadOnlySpan<byte> data, ref int pos, out long value, out bool unknown)
    {
        value = 0;
        unknown = false;
        if (pos >= data.Length || data[pos] == 0)
            return false;

        var len = BitOperations.LeadingZeroCount((uint)data[pos]) - 23;
        if (pos + len > data.Length)
            return false;

        var mask = 0xFF >> len;
        long v = data[pos] & mask;
        var allOnes = v == mask;
        for (var i = 1; i < len; i++)
        {
            v = (v << 8) | data[pos + i];
            allOnes &= data[pos + i] == 0xFF;
        }

        pos += len;
        value = v;
        unknown = allOnes;
        return true;
    }

    // MP4: percorre as caixas do nivel de cima (ftyp, moov, mdat...); se alguma declara um
    // tamanho que passa do fim do arquivo, ele foi cortado. Devolve o fim dessa caixa.
    private static long? Mp4ExpectedMinLength(FileStream fs, long length)
    {
        var header = new byte[16];
        long offset = 0;
        for (var i = 0; i < MaxMp4Boxes && offset < length; i++)
        {
            if (length - offset < 8)
                return null;

            var n = (int)Math.Min(header.Length, length - offset);
            fs.Position = offset;
            fs.ReadExactly(header, 0, n);

            foreach (var c in header.AsSpan(4, 4))
                if (c < 0x20 || c > 0x7E)
                    return null; // nao parece caixa MP4 (ou cabecalho zerado: fica pro teste de zeros)

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (size == 0)
                return null; // "vai ate o fim do arquivo"
            if (size == 1)
            {
                if (n < 16)
                    return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
            }
            if (size < 8)
                return null;

            if (offset + size > length)
                return offset + size;
            offset += size;
        }

        return null;
    }

    // ---- 2) arquivo esparso com buracos (qBittorrent/libtorrent no modo padrao) ----

    private const uint FsctlQueryAllocatedRanges = 0x000940CF;
    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct AllocatedRange
    {
        public long Offset;
        public long Length;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
        ref AllocatedRange inBuffer, int inBufferSize,
        out AllocatedRange outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);

    private static string? CheckSparseHoles(FileStream fs, string videoPath, long length)
    {
        if (!File.GetAttributes(videoPath).HasFlag(FileAttributes.SparseFile))
            return null;

        // Arquivo completo = um unico trecho alocado cobrindo tudo. Pede so 1 trecho: se a
        // API responder "mais dados", ha varios trechos, ou seja, ha buracos entre eles.
        var query = new AllocatedRange { Offset = 0, Length = length };
        var size = Marshal.SizeOf<AllocatedRange>();
        if (DeviceIoControl(fs.SafeFileHandle, FsctlQueryAllocatedRanges, ref query, size,
                out var range, size, out var returned, IntPtr.Zero))
        {
            if (returned == 0)
                return "nenhum trecho do arquivo foi gravado ainda";
            return range.Offset == 0 && range.Offset + range.Length >= length
                ? null
                : "o arquivo tem trechos ainda nao gravados";
        }

        return Marshal.GetLastWin32Error() == ErrorMoreData
            ? "o arquivo tem trechos ainda nao gravados"
            : null;
    }

    // ---- 3) trechos zerados (torrent com pre-alocacao, copia do Explorer em andamento) ----

    private static string? CheckZeroBlocks(FileStream fs, long length)
    {
        if (length < ZeroSampleSize * 4L)
            return null;

        var buffer = new byte[ZeroSampleSize];
        for (var i = 0; i <= ZeroSampleCount; i++)
        {
            var offset = (length - ZeroSampleSize) * i / ZeroSampleCount;
            fs.Position = offset;
            fs.ReadExactly(buffer);
            if (buffer.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                return $"trecho zerado perto de {offset * 100 / length}% do arquivo";
        }

        return null;
    }

    private static string FormatMb(long bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";
}
