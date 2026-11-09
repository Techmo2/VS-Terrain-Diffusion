using System;
using System.IO;
using System.IO.Compression;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// A terrain tile, or a coarse window, as bytes, losslessly. Each square plane of values is stored as
/// the bit patterns of its values, each minus its neighbour's (the one before, or above at the start
/// of a row), split into byte planes and compressed. Neighbouring values differ little, so the high
/// bytes of those differences are almost all zero and compress to nearly nothing.
/// </summary>
public static class TileCodec
{
    /// <summary>
    /// Planes in a tile: elevation, surface Y, slope, then the four climate fields (see
    /// <see cref="Floats"/>).
    /// </summary>
    private const int Arrays = 7;

    /// <summary>Leads a tile blob, "TDT".</summary>
    private static readonly byte[] Magic = { (byte)'T', (byte)'D', (byte)'T' };

    /// <summary>Leads a coarse window blob, "TDW".</summary>
    private static readonly byte[] WindowMagic = { (byte)'T', (byte)'D', (byte)'W' };

    /// <summary>
    /// Brotli effort, 0 to 11. Measured on 140 stored 256-block tiles: 4 stores 506 KB of 1792 in
    /// 15 ms; 1 is 10% larger for 6 ms, and 8 saves 1% for 51 ms. Decoding is 7-10 ms at any of them.
    /// </summary>
    public static int Quality { get; set; } = 4;

    /// <summary>Encodes a tile.</summary>
    /// <param name="tile">The tile.</param>
    /// <returns>The blob: magic, format, edge length, then the compressed byte planes.</returns>
    public static byte[] Encode(TerrainTile tile)
    {
        int size = tile.Size, plane = size * size;
        var raw = new byte[Arrays * plane * 4];
        var bits = new int[plane];
        for (int a = 0; a < Arrays; a++)
        {
            ReadBits(tile, a, bits);
            Split(bits, size, raw, a * plane * 4);
        }
        return Compress(Magic, size, raw);
    }

    /// <summary>Decodes a tile written by <see cref="Encode"/>.</summary>
    /// <param name="blob">The blob.</param>
    /// <param name="blockX">The tile's west edge, in blocks.</param>
    /// <param name="blockZ">The tile's north edge, in blocks.</param>
    /// <param name="expectedSize">The edge length the caller's tiles have.</param>
    /// <returns>The tile, bit for bit as encoded.</returns>
    /// <exception cref="InvalidDataException">Another format, or another size.</exception>
    public static TerrainTile Decode(byte[] blob, int blockX, int blockZ, int expectedSize)
    {
        int size = Header(blob, Magic);
        if (size != expectedSize) throw new InvalidDataException($"tile of {size} blocks where {expectedSize} were expected");

        int plane = size * size;
        byte[] raw = Decompress(blob, Arrays * plane * 4);
        var tile = new TerrainTile(blockX, blockZ, size);
        var bits = new int[plane];
        for (int a = 0; a < Arrays; a++)
        {
            Join(raw, a * plane * 4, size, bits);
            WriteBits(tile, a, bits);
        }
        return tile;
    }

    /// <summary>A coarse window: whole square planes of floats, channel after channel.</summary>
    public static byte[] EncodeWindow(float[] data, int planes)
    {
        int size = (int)Math.Round(Math.Sqrt(data.Length / planes));
        if (planes * size * size != data.Length) throw new ArgumentException($"not {planes} square planes", nameof(data));

        int plane = size * size;
        var raw = new byte[data.Length * 4];
        var bits = new int[plane];
        for (int p = 0; p < planes; p++)
        {
            for (int i = 0; i < plane; i++) bits[i] = BitConverter.SingleToInt32Bits(data[p * plane + i]);
            Split(bits, size, raw, p * plane * 4);
        }
        return Compress(WindowMagic, size, raw);
    }

    /// <summary>Decodes a coarse window written by <see cref="EncodeWindow"/>.</summary>
    /// <param name="blob">The blob.</param>
    /// <param name="expectedLength">The number of values the caller expects.</param>
    /// <returns>The values.</returns>
    /// <exception cref="InvalidDataException">Another format, or a size that does not fit.</exception>
    public static float[] DecodeWindow(byte[] blob, int expectedLength)
    {
        int size = Header(blob, WindowMagic);
        int plane = size * size;
        int planes = expectedLength / plane;
        if (planes * plane != expectedLength)
            throw new InvalidDataException($"coarse window of {size}x{size} planes where {expectedLength} values were expected");

        byte[] raw = Decompress(blob, expectedLength * 4);
        var data = new float[expectedLength];
        var bits = new int[plane];
        for (int p = 0; p < planes; p++)
        {
            Join(raw, p * plane * 4, size, bits);
            for (int i = 0; i < plane; i++) data[p * plane + i] = BitConverter.Int32BitsToSingle(bits[i]);
        }
        return data;
    }

    /// <summary>One plane's differences, as four byte planes at <paramref name="offset"/>.</summary>
    private static void Split(int[] bits, int size, byte[] raw, int offset)
    {
        int plane = bits.Length;
        for (int i = 0; i < plane; i++)
        {
            int previous = i == 0 ? 0 : i % size == 0 ? bits[i - size] : bits[i - 1];
            int delta = unchecked(bits[i] - previous);
            raw[offset + i] = (byte)delta;
            raw[offset + plane + i] = (byte)(delta >> 8);
            raw[offset + 2 * plane + i] = (byte)(delta >> 16);
            raw[offset + 3 * plane + i] = (byte)(delta >> 24);
        }
    }

    /// <summary>The inverse of <see cref="Split"/>.</summary>
    private static void Join(byte[] raw, int offset, int size, int[] bits)
    {
        int plane = bits.Length;
        for (int i = 0; i < plane; i++)
        {
            int delta = raw[offset + i] | raw[offset + plane + i] << 8 |
                        raw[offset + 2 * plane + i] << 16 | raw[offset + 3 * plane + i] << 24;
            int previous = i == 0 ? 0 : i % size == 0 ? bits[i - size] : bits[i - 1];
            bits[i] = unchecked(previous + delta);
        }
    }

    /// <summary>Writes the header and the Brotli-compressed byte planes.</summary>
    /// <param name="magic">Which kind of blob.</param>
    /// <param name="size">Edge length of each plane.</param>
    /// <param name="raw">The byte planes.</param>
    /// <returns>The blob.</returns>
    private static byte[] Compress(byte[] magic, int size, byte[] raw)
    {
        using var output = new MemoryStream(raw.Length / 4);
        output.Write(magic);
        output.WriteByte((byte)TerrainTileStore.FormatVersion);
        output.Write(BitConverter.GetBytes(size));
        using (var brotli = new BrotliStream(output, new BrotliCompressionOptions { Quality = Quality }, leaveOpen: true))
        {
            brotli.Write(raw);
        }
        return output.ToArray();
    }

    /// <summary>The edge length a blob records, once its magic and format are checked.</summary>
    private static int Header(byte[] blob, byte[] magic)
    {
        if (blob.Length < 8 || blob[0] != magic[0] || blob[1] != magic[1] || blob[2] != magic[2] ||
            blob[3] != TerrainTileStore.FormatVersion)
        {
            throw new InvalidDataException("not stored terrain of this format");
        }
        return BitConverter.ToInt32(blob, 4);
    }

    /// <summary>Inflates a blob's byte planes.</summary>
    /// <param name="blob">The blob, header checked.</param>
    /// <param name="length">The expected number of bytes.</param>
    /// <returns>The byte planes.</returns>
    private static byte[] Decompress(byte[] blob, int length)
    {
        var raw = new byte[length];
        using var brotli = new BrotliStream(new MemoryStream(blob, 8, blob.Length - 8), CompressionMode.Decompress);
        brotli.ReadExactly(raw);
        return raw;
    }

    /// <summary>One of a tile's planes as raw 32-bit patterns.</summary>
    /// <param name="tile">The tile.</param>
    /// <param name="array">Which plane, 0 to <see cref="Arrays"/> - 1.</param>
    /// <param name="bits">Receives the plane.</param>
    private static void ReadBits(TerrainTile tile, int array, int[] bits)
    {
        if (array == 1)
        {
            Array.Copy(tile.SurfaceY, bits, bits.Length);
            return;
        }
        float[] values = Floats(tile, array);
        for (int i = 0; i < bits.Length; i++) bits[i] = BitConverter.SingleToInt32Bits(values[i]);
    }

    /// <summary>The inverse of <see cref="ReadBits"/>.</summary>
    /// <param name="tile">The tile being filled.</param>
    /// <param name="array">Which plane.</param>
    /// <param name="bits">The plane.</param>
    private static void WriteBits(TerrainTile tile, int array, int[] bits)
    {
        if (array == 1)
        {
            Array.Copy(bits, tile.SurfaceY, bits.Length);
            return;
        }
        float[] values = Floats(tile, array);
        for (int i = 0; i < bits.Length; i++) values[i] = BitConverter.Int32BitsToSingle(bits[i]);
    }

    /// <summary>
    /// A tile's float plane by index; 1 is <see cref="TerrainTile.SurfaceY"/>, which is integer and handled
    /// by the callers.
    /// </summary>
    /// <param name="tile">The tile.</param>
    /// <param name="array">Which plane.</param>
    /// <returns>The plane.</returns>
    private static float[] Floats(TerrainTile tile, int array) => array switch
    {
        0 => tile.ElevationMeters,
        2 => tile.Slope,
        3 => tile.TemperatureC,
        4 => tile.TemperatureSeasonality,
        5 => tile.PrecipitationMm,
        6 => tile.PrecipitationCv,
        _ => throw new ArgumentOutOfRangeException(nameof(array))
    };
}
