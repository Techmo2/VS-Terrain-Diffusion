using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// Pulls individual files out of a remote ZIP archive using HTTP range requests, so that a single
/// 20 MB shared library can be fetched from a 200 MB NuGet package without downloading the rest.
/// </summary>
internal static class RemoteZipExtractor
{
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064b50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
    private const uint CentralFileHeaderSignature = 0x02014b50;

    internal sealed class Entry
    {
        public string Name;
        public ushort CompressionMethod;
        public long CompressedSize;
        public long UncompressedSize;
        public long LocalHeaderOffset;
        public uint Crc32;
    }

    /// <summary>Reads the archive's central directory.</summary>
    public static List<Entry> ReadCentralDirectory(HttpClient client, string url, CancellationToken cancellation)
    {
        long length = GetContentLength(client, url, cancellation);

        int tailSize = (int)Math.Min(length, 66000);
        byte[] tail = GetRange(client, url, length - tailSize, length - 1, cancellation);

        int eocd = FindLast(tail, EndOfCentralDirectorySignature);
        if (eocd < 0) throw new InvalidDataException("Not a ZIP archive (no end-of-central-directory record): " + url);

        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));

        if (cdOffset == uint.MaxValue || cdSize == uint.MaxValue)
        {
            int locator = FindLast(tail, Zip64EndOfCentralDirectoryLocatorSignature);
            if (locator < 0) throw new InvalidDataException("ZIP64 archive without a locator record: " + url);
            long zip64Offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(locator + 8));
            byte[] zip64 = GetRange(client, url, zip64Offset, zip64Offset + 55, cancellation);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != Zip64EndOfCentralDirectorySignature)
                throw new InvalidDataException("Malformed ZIP64 end-of-central-directory record: " + url);
            cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40));
            cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48));
        }

        byte[] cd = GetRange(client, url, cdOffset, cdOffset + cdSize - 1, cancellation);
        var entries = new List<Entry>();
        int p = 0;
        while (p + 46 <= cd.Length && BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p)) == CentralFileHeaderSignature)
        {
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 10));
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 16));
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 20));
            long uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 24));
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 28));
            ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 30));
            ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 32));
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 42));
            string name = Encoding.UTF8.GetString(cd, p + 46, nameLength);

            if (compressed == uint.MaxValue || uncompressed == uint.MaxValue || localOffset == uint.MaxValue)
            {
                ReadZip64Extra(cd, p + 46 + nameLength, extraLength,
                    ref uncompressed, ref compressed, ref localOffset);
            }

            entries.Add(new Entry
            {
                Name = name,
                CompressionMethod = method,
                CompressedSize = compressed,
                UncompressedSize = uncompressed,
                LocalHeaderOffset = localOffset,
                Crc32 = crc
            });

            p += 46 + nameLength + extraLength + commentLength;
        }

        if (entries.Count == 0) throw new InvalidDataException("ZIP central directory is empty: " + url);
        return entries;
    }

    /// <summary>Downloads and inflates a single entry to <paramref name="destinationPath"/>.</summary>
    public static void ExtractEntry(HttpClient client, string url, Entry entry, string destinationPath,
                                    CancellationToken cancellation)
    {
        // The central directory's name/extra lengths may differ from the local header's, so read it.
        byte[] localHeader = GetRange(client, url, entry.LocalHeaderOffset, entry.LocalHeaderOffset + 29, cancellation);
        ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader.AsSpan(26));
        ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader.AsSpan(28));
        long dataStart = entry.LocalHeaderOffset + 30 + nameLength + extraLength;

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string tempPath = destinationPath + ".tmp";

        uint crc;
        using (Stream network = GetRangeStream(client, url, dataStart, dataStart + entry.CompressedSize - 1, cancellation))
        using (Stream decoded = entry.CompressionMethod == 0
                   ? network
                   : new DeflateStream(network, CompressionMode.Decompress))
        using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            crc = CopyAndHash(decoded, file, cancellation);
        }

        if (crc != entry.Crc32)
        {
            File.Delete(tempPath);
            throw new InvalidDataException(
                $"CRC mismatch extracting {entry.Name} from {url} (expected {entry.Crc32:x8}, got {crc:x8})");
        }

        File.Move(tempPath, destinationPath, overwrite: true);
    }

    private static void ReadZip64Extra(byte[] buffer, int offset, int length,
                                       ref long uncompressed, ref long compressed, ref long localOffset)
    {
        int end = offset + length;
        while (offset + 4 <= end)
        {
            ushort headerId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset));
            ushort dataSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 2));
            int data = offset + 4;
            if (headerId == 0x0001)
            {
                int cursor = data;
                if (uncompressed == uint.MaxValue && cursor + 8 <= data + dataSize)
                {
                    uncompressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(cursor));
                    cursor += 8;
                }
                if (compressed == uint.MaxValue && cursor + 8 <= data + dataSize)
                {
                    compressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(cursor));
                    cursor += 8;
                }
                if (localOffset == uint.MaxValue && cursor + 8 <= data + dataSize)
                {
                    localOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(cursor));
                }
                return;
            }
            offset = data + dataSize;
        }
    }

    private static long GetContentLength(HttpClient client, string url, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using HttpResponseMessage response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        long? length = response.Content.Headers.ContentLength;
        if (length == null || length <= 0)
            throw new InvalidDataException("Server did not report a size for " + url);
        return length.Value;
    }

    private static byte[] GetRange(HttpClient client, string url, long from, long to, CancellationToken cancellation)
    {
        using Stream stream = GetRangeStream(client, url, from, to, cancellation);
        using var buffer = new MemoryStream((int)(to - from + 1));
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Stream GetRangeStream(HttpClient client, string url, long from, long to, CancellationToken cancellation)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        HttpResponseMessage response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.Dispose();
            throw new InvalidDataException(
                $"Server does not support range requests for {url} (HTTP {(int)response.StatusCode})");
        }
        return response.Content.ReadAsStream(cancellation);
    }

    private static uint CopyAndHash(Stream source, Stream destination, CancellationToken cancellation)
    {
        var buffer = new byte[1 << 20];
        uint crc = 0xFFFFFFFF;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
            for (int i = 0; i < read; i++)
                crc = Crc32Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFF;
    }

    private static int FindLast(byte[] buffer, uint signature)
    {
        Span<byte> needle = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(needle, signature);
        for (int i = buffer.Length - 4; i >= 0; i--)
        {
            if (buffer[i] == needle[0] && buffer[i + 1] == needle[1] &&
                buffer[i + 2] == needle[2] && buffer[i + 3] == needle[3])
            {
                return i;
            }
        }
        return -1;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }
}
