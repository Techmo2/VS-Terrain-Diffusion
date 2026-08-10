using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Native;
using VSTerrainDiffusion.Pipeline;
using VSTerrainDiffusion.Tensors;

namespace VSTerrainDiffusion.Tools;

/// <summary>
/// Runs the diffusion pipeline outside of Vintage Story: downloads the models and the matching
/// ONNX Runtime, samples a region, prints statistics and writes a shaded relief PNG.
///
/// Usage: dotnet run -- [--x 0] [--z 0] [--size 96] [--seed 1] [--device auto] [--out relief.png]
/// </summary>
public static class Program
{
    private sealed class ConsoleLogger : LoggerBase
    {
        protected override void LogImpl(EnumLogType logType, string format, params object[] args)
        {
            string message;
            try { message = args is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, format, args) : format; }
            catch (FormatException) { message = format; }
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {logType,-12} {message}");
        }
    }

    public static int Main(string[] rawArgs)
    {
        Dictionary<string, string> args = ParseArgs(rawArgs);

        GamePaths.DataPath = args.GetValueOrDefault("data",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "VintagestoryData"));

        int originX = int.Parse(args.GetValueOrDefault("x", "0"));
        int originZ = int.Parse(args.GetValueOrDefault("z", "0"));
        int size = int.Parse(args.GetValueOrDefault("size", "96"));
        ulong seed = ulong.Parse(args.GetValueOrDefault("seed", "1"));
        string outputPath = args.GetValueOrDefault("out", "relief.png");
        string device = args.GetValueOrDefault("device", "auto");
        bool dump = args.ContainsKey("dump");

        DiffusionConfig.Instance.InferenceDevice = device;
        DiffusionConfig.Instance.OffloadModels = args.GetValueOrDefault("offload", "true") == "true";

        var logger = new ConsoleLogger();
        Console.WriteLine($"Data path : {GamePaths.DataPath}");
        Console.WriteLine($"Models    : {DiffusionPaths.ModelDirectory}");
        Console.WriteLine($"Region    : ({originX}, {originZ}) size {size}, seed {seed}, device {device}");

        try
        {
            ModelAssetManager.EnsureAssetsReady(logger);
            OnnxRuntimeBootstrap.Initialize(logger);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Setup failed: " + e);
            return 1;
        }

        PipelineModels.BeginLoad(logger);
        PipelineModels models = PipelineModels.Await();

        var pipeline = new WorldPipeline(seed, models);
        Console.WriteLine($"Native resolution: {pipeline.NativeResolution} m/pixel");

        if (args.ContainsKey("findland"))
        {
            (int X, int Z)? land = FindLand(pipeline, int.Parse(args.GetValueOrDefault("findland", "48")));
            if (land == null)
            {
                Console.WriteLine("No land found in the scanned area.");
                return 2;
            }
            Console.WriteLine($"Land found at native pixel ({land.Value.X}, {land.Value.Z}); centring the sample there.");
            originX = land.Value.X - size / 2;
            originZ = land.Value.Z - size / 2;
        }

        if (args.ContainsKey("calibrate"))
        {
            WorldGenConfig shaping = DiffusionConfig.Instance.WorldGen;
            shaping.CalibrationRadiusBlocks = int.Parse(args.GetValueOrDefault("radius",
                shaping.CalibrationRadiusBlocks.ToString(CultureInfo.InvariantCulture)));
            shaping.CalibrationProbes = int.Parse(args.GetValueOrDefault("probes",
                shaping.CalibrationProbes.ToString(CultureInfo.InvariantCulture)));

            return ReportCalibration(
                seed, models, logger, pipeline.NativeResolution,
                int.Parse(args.GetValueOrDefault("scale", "4")),
                int.Parse(args.GetValueOrDefault("mapsizey", "512")),
                int.Parse(args.GetValueOrDefault("sealevel", "220")),
                originX, originZ);
        }

        if (args.ContainsKey("climatestats"))
        {
            ReportClimateDistribution(pipeline, int.Parse(args.GetValueOrDefault("climatestats", "96")));
            models.Dispose();
            return 0;
        }

        var stopwatch = Stopwatch.StartNew();
        WorldPipeline.Sample sample = pipeline.Get(originZ, originX, originZ + size, originX + size, true);
        stopwatch.Stop();

        int plane = size * size;
        float[] elevation = sample.Elevation;
        float[] climate = sample.Climate;

        Console.WriteLine($"Sampled {size}x{size} native pixels in {stopwatch.ElapsedMilliseconds} ms " +
                          $"({pipeline.TotalComputedWindowCount} model windows)");
        Report("elevation (m)", elevation);
        Report("temperature (C)", climate.Take(plane).ToArray());
        Report("precipitation (mm)", climate.Skip(2 * plane).Take(plane).ToArray());
        Console.WriteLine($"land fraction: {elevation.Count(e => e > 0) / (float)plane:P1}");

        WriteReliefPng(outputPath, elevation, size, size);
        Console.WriteLine("Wrote " + Path.GetFullPath(outputPath));

        if (dump)
        {
            string dumpPath = Path.ChangeExtension(outputPath, ".txt");
            using var writer = new StreamWriter(dumpPath);
            writer.WriteLine("elev " + string.Join(" ", elevation.Select(v => v.ToString("G9", CultureInfo.InvariantCulture))));
            for (int ch = 0; ch < 5; ch++)
            {
                writer.WriteLine($"clim{ch} " + string.Join(" ",
                    climate.Skip(ch * plane).Take(plane).Select(v => v.ToString("G9", CultureInfo.InvariantCulture))));
            }
            Console.WriteLine("Wrote " + Path.GetFullPath(dumpPath));
        }

        models.Dispose();
        return 0;
    }

    /// <summary>
    /// Samples the coarse map over a wide box and reports the distribution of land precipitation
    /// and temperature. Precipitation has no fine detail — the pipeline bilinearly interpolates it
    /// straight from the coarse map — so this is the whole distribution, and it is what the
    /// millimetres-to-rainfall curve has to be fitted against.
    /// </summary>
    private static void ReportClimateDistribution(WorldPipeline pipeline, int boxCoarsePixels)
    {
        int half = boxCoarsePixels / 2;
        FloatTensor coarse = pipeline.GetCoarseSlice(-half, -half, half, half);

        int size = 2 * half;
        int plane = size * size;
        var precipitation = new List<float>(plane);
        var temperature = new List<float>(plane);
        int oceanCells = 0;

        for (int px = 0; px < plane; px++)
        {
            float weight = coarse.Data[6 * plane + px];
            if (weight <= 1e-6f) continue;
            if (coarse.Data[px] / weight <= 0f) { oceanCells++; continue; }

            precipitation.Add(coarse.Data[4 * plane + px] / weight);
            temperature.Add(coarse.Data[2 * plane + px] / weight);
        }

        Console.WriteLine($"Coarse box {size}x{size} cells ({size * 256 * 30 / 1000} km across): " +
                          $"{precipitation.Count} land, {oceanCells} ocean");
        PrintQuantiles("precipitation (mm)", precipitation);
        PrintQuantiles("temperature (C)", temperature);

        var logs = precipitation.Where(p => p > 1f).Select(p => Math.Log(p)).ToList();
        if (logs.Count > 1)
        {
            double mu = logs.Average();
            double sigma = Math.Sqrt(logs.Sum(v => (v - mu) * (v - mu)) / (logs.Count - 1));
            Console.WriteLine($"log-normal fit over land: mu {mu:0.####} (median {Math.Exp(mu):0} mm), sigma {sigma:0.####}");
        }
    }

    private static void PrintQuantiles(string name, List<float> values)
    {
        if (values.Count == 0) { Console.WriteLine($"{name}: no samples"); return; }
        values.Sort();
        string At(double q) => values[Math.Clamp((int)(q * values.Count), 0, values.Count - 1)].ToString("0.#");
        Console.WriteLine($"{name,-20} p1 {At(0.01),7} p5 {At(0.05),7} p10 {At(0.10),7} p25 {At(0.25),7} " +
                          $"p50 {At(0.50),7} p75 {At(0.75),7} p90 {At(0.90),7} p95 {At(0.95),7} p99 {At(0.99),7}");
    }

    /// <summary>
    /// Runs the per-world terrain height calibration and prints the mapping it produces, so the
    /// metre-to-block curve can be checked without starting a server.
    /// </summary>
    private static int ReportCalibration(ulong seed, PipelineModels models, ILogger logger,
                                         float nativeResolution, int scale, int mapSizeY, int seaLevel,
                                         int centerNativeX, int centerNativeZ)
    {
        DiffusionWorldSettings settings =
            DiffusionWorldSettings.ForOfflineUse(nativeResolution, scale, mapSizeY, seaLevel);

        Console.WriteLine($"Before      : {settings.Describe()}");

        using var provider = new WorldGen.TerrainDiffusionProvider(seed, models, settings, logger);
        var stopwatch = Stopwatch.StartNew();
        float? peak = provider.MeasurePeakElevation(centerNativeX * scale, centerNativeZ * scale);
        stopwatch.Stop();

        if (peak == null)
        {
            Console.WriteLine("Calibration failed.");
            return 2;
        }

        settings.ApplyCalibration(peak.Value);
        Console.WriteLine($"Calibrated  : {settings.Describe()}  ({stopwatch.ElapsedMilliseconds} ms)");
        Console.WriteLine();
        Console.WriteLine("  elevation      block Y   height above sea");
        foreach (int meters in new[] { -4000, -1000, -200, -50, 0, 100, 250, 500, 1000, 1500, 2000, 3000, 5000, 8000 })
        {
            int y = settings.ElevationToBlockY(meters);
            Console.WriteLine($"  {meters,7} m      {y,6}   {y - seaLevel,6:+#;-#;0}");
        }
        return 0;
    }

    /// <summary>
    /// Scans the cheap coarse stage for the land pixel nearest the origin. One coarse pixel spans
    /// 256 native pixels (7.68 km), so a 48x48 box covers roughly 370 km on a side.
    /// </summary>
    private static (int X, int Z)? FindLand(WorldPipeline pipeline, int boxSize)
    {
        int half = boxSize / 2;
        const int coarseToNative = 32 * 8;

        var coarse = pipeline.GetCoarseSlice(-half, -half, half, half);
        int plane = boxSize * boxSize;
        (int Row, int Col)? best = null;
        int bestDistance = int.MaxValue;

        for (int r = 1; r < boxSize - 1; r++)
        {
            for (int c = 1; c < boxSize - 1; c++)
            {
                bool allLand = true;
                for (int dr = -1; dr <= 1 && allLand; dr++)
                {
                    for (int dc = -1; dc <= 1 && allLand; dc++)
                    {
                        int index = (r + dr) * boxSize + (c + dc);
                        float weight = coarse.Data[6 * plane + index];
                        allLand = weight > 1e-6f && coarse.Data[index] / weight > 0f;
                    }
                }
                if (!allLand) continue;

                int distance = (r - half) * (r - half) + (c - half) * (c - half);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = (r, c);
            }
        }

        if (best == null) return null;
        return ((best.Value.Col - half) * coarseToNative + coarseToNative / 2,
                (best.Value.Row - half) * coarseToNative + coarseToNative / 2);
    }

    private static void Report(string name, float[] values)
    {
        float min = values.Min(), max = values.Max();
        double mean = values.Average(v => (double)v);
        Console.WriteLine($"{name,-22} min {min,10:0.##}  max {max,10:0.##}  mean {mean,10:0.##}");
    }

    private static Dictionary<string, string> ParseArgs(string[] rawArgs)
    {
        var args = new Dictionary<string, string>();
        for (int i = 0; i < rawArgs.Length; i++)
        {
            if (!rawArgs[i].StartsWith("--")) continue;
            string key = rawArgs[i][2..];
            string value = i + 1 < rawArgs.Length && !rawArgs[i + 1].StartsWith("--") ? rawArgs[++i] : "true";
            args[key] = value;
        }
        return args;
    }

    /// <summary>Writes a hillshaded relief image so the terrain can be eyeballed.</summary>
    private static void WriteReliefPng(string path, float[] elevation, int width, int height)
    {
        var pixels = new byte[width * height * 3];
        float maxLand = 1f, minSea = -1f;
        foreach (float e in elevation)
        {
            if (e > maxLand) maxLand = e;
            if (e < minSea) minSea = e;
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float e = elevation[y * width + x];

                // Central-difference hillshade with the light coming from the north-west.
                float ex = elevation[y * width + Math.Min(width - 1, x + 1)] - elevation[y * width + Math.Max(0, x - 1)];
                float ey = elevation[Math.Min(height - 1, y + 1) * width + x] - elevation[Math.Max(0, y - 1) * width + x];
                float shade = Math.Clamp(0.75f + (-ex - ey) / 600f, 0.35f, 1.4f);

                byte r, g, b;
                if (e < 0)
                {
                    float t = Math.Clamp(-e / Math.Max(1f, -minSea), 0f, 1f);
                    r = (byte)(20 * (1 - t));
                    g = (byte)(90 - 60 * t);
                    b = (byte)(170 - 70 * t);
                }
                else
                {
                    float t = Math.Clamp(e / Math.Max(1f, maxLand), 0f, 1f);
                    // green lowlands -> brown uplands -> white peaks
                    float rr = t < 0.5f ? 60 + 300 * t : 210 + 90 * (t - 0.5f) * 2;
                    float gg = t < 0.5f ? 120 + 130 * t : 185 + 140 * (t - 0.5f) * 2;
                    float bb = t < 0.5f ? 50 + 60 * t : 140 + 230 * (t - 0.5f) * 2;
                    r = (byte)Math.Clamp(rr * shade, 0, 255);
                    g = (byte)Math.Clamp(gg * shade, 0, 255);
                    b = (byte)Math.Clamp(bb * shade, 0, 255);
                    pixels[(y * width + x) * 3] = r;
                    pixels[(y * width + x) * 3 + 1] = g;
                    pixels[(y * width + x) * 3 + 2] = b;
                    continue;
                }

                pixels[(y * width + x) * 3] = r;
                pixels[(y * width + x) * 3 + 1] = g;
                pixels[(y * width + x) * 3 + 2] = b;
            }
        }

        MinimalPng.Write(path, pixels, width, height);
    }
}

/// <summary>Bare-bones RGB PNG writer so the tool needs no imaging dependency.</summary>
internal static class MinimalPng
{
    public static void Write(string path, byte[] rgb, int width, int height)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        WriteBigEndian(header, 0, width);
        WriteBigEndian(header, 4, height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type: truecolour
        WriteChunk(stream, "IHDR", header);

        var raw = new byte[height * (width * 3 + 1)];
        for (int y = 0; y < height; y++)
        {
            raw[y * (width * 3 + 1)] = 0; // filter: none
            Array.Copy(rgb, y * width * 3, raw, y * (width * 3 + 1) + 1, width * 3);
        }
        WriteChunk(stream, "IDAT", ZlibCompress(raw));
        WriteChunk(stream, "IEND", Array.Empty<byte>());
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x01);
        using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true))
        {
            deflate.Write(data, 0, data.Length);
        }

        uint a = 1, b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        uint adler = (b << 16) | a;
        output.WriteByte((byte)(adler >> 24));
        output.WriteByte((byte)(adler >> 16));
        output.WriteByte((byte)(adler >> 8));
        output.WriteByte((byte)adler);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, (int)crc);
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
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

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in a) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (byte value in b) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
