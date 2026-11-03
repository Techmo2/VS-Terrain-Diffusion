using System;
using VSTerrainDiffusion.Pipeline;
using VSTerrainDiffusion.WorldGen;

namespace VSTerrainDiffusion.Api;

/// <summary>
/// The climate Terrain Diffusion generated, for other mods. Server side only, from
/// <c>api.ModLoader.GetModSystem&lt;TerrainDiffusionModSystem&gt;().Climate</c>, which is null on a world
/// the mod is not generating.
///
/// Queries are answered from the same tiles and coarse map the terrain is built from, so they
/// agree with what the world holds. A column whose tile has not been generated yet is generated on
/// the spot (model inference, or a read from the world's tile database), so call from world
/// generation or another background thread, never the main thread.
/// </summary>
public sealed class TerrainClimate
{
    /// <summary>
    /// Names of the coarse model's channels, in order, for <see cref="TryGetCoarseChannels"/>. The
    /// annual model has the first six.
    /// </summary>
    public static readonly string[] ChannelNames =
    {
        "elevation_m", "elevation_p5_m", "temperature_c", "temperature_seasonality", "precipitation_mm",
        "precipitation_cv", "temperature_shape_cos", "temperature_shape_sin", "wet_season_cos", "wet_season_sin",
        "second_rainy_cos", "second_rainy_sin", "diurnal_range_c"
    };

    private readonly TerrainDiffusionProvider _provider;

    /// <summary>
    /// The coarse channels of the last chunk asked about, per thread. They vary over the model's
    /// 7.68 km cells, so one read serves a whole chunk, and world generation asks about the same
    /// chunk hundreds of times in a row.
    /// </summary>
    [ThreadStatic] private static CoarseCache _coarseCache;

    private sealed class CoarseCache
    {
        /// <summary>Weak, so a thread that outlives a world does not keep that world's provider alive.</summary>
        public readonly WeakReference<TerrainDiffusionProvider> Provider = new(null);
        public long Chunk = long.MinValue;
        public readonly float[] Channels = new float[CoarseChannel.SeasonalCount];
    }

    internal TerrainClimate(TerrainDiffusionProvider provider)
    {
        _provider = provider;
    }

    /// <summary>"seasonal" or "annual": the coarse model this world was generated with.</summary>
    public string ClimateModel => _provider.CoarseSpec.Code;

    /// <summary>Whether the model predicts the seasonal cycle and diurnal range (see <see cref="ClimateRecord.HasSeasonalCycle"/>).</summary>
    public bool HasSeasonalCycle => _provider.CoarseSpec.HasSeasonalCycle;

    /// <summary>How many channels <see cref="TryGetCoarseChannels"/> fills: 13 for the seasonal model, 6 for the annual one.</summary>
    public int ChannelCount => _provider.CoarseSpec.Channels;

    /// <summary>The climate of one block column. False if it could not be computed (the pipeline failed).</summary>
    public bool TryGetClimate(int blockX, int blockZ, out ClimateRecord record)
    {
        record = default;
        TerrainTile tile;
        try
        {
            tile = _provider.GetTileAt(blockX, blockZ);
        }
        catch (Exception)
        {
            return false;
        }
        if (tile == null) return false;

        int index = tile.Index(Mod(blockX - tile.BlockX, tile.Size), Mod(blockZ - tile.BlockZ, tile.Size));
        float precipitationMm = tile.PrecipitationMm[index], precipitationCv = tile.PrecipitationCv[index];

        Span<float> cycle = stackalloc float[6];
        float diurnalRange = float.NaN;
        if (HasSeasonalCycle)
        {
            float[] channels = ChunkCoarseChannels(blockX, blockZ);
            if (channels == null) return false;
            channels.AsSpan(CoarseChannel.TemperatureShapeCos, 6).CopyTo(cycle);
            diurnalRange = channels[CoarseChannel.DiurnalRange];
        }
        else
        {
            ClimateRecord.ApproximateCycle(precipitationMm, precipitationCv, cycle);
        }

        record = new ClimateRecord(tile.TemperatureC[index], tile.TemperatureSeasonality[index], precipitationMm,
            precipitationCv, tile.ElevationMeters[index], HasSeasonalCycle, cycle, diurnalRange);
        return true;
    }

    /// <summary>
    /// The coarse model's raw output at a block column, one value per <see cref="ChannelNames"/>
    /// entry up to <see cref="ChannelCount"/>: smooth at the model's 7.68 km cells, and without the
    /// world's climate settings. <paramref name="channels"/> must hold at least
    /// <see cref="ChannelCount"/> values.
    /// </summary>
    public bool TryGetCoarseChannels(double blockX, double blockZ, Span<float> channels)
    {
        if (channels.Length < ChannelCount) throw new ArgumentException($"needs room for {ChannelCount} channels", nameof(channels));
        return _provider.CoarseChannelsAt(blockX, blockZ, channels);
    }

    /// <summary>The coarse channels at the centre of the chunk holding this column, or null on failure.</summary>
    private float[] ChunkCoarseChannels(int blockX, int blockZ)
    {
        CoarseCache cache = _coarseCache ??= new CoarseCache();
        int chunkX = blockX >> 5, chunkZ = blockZ >> 5;
        long chunk = ((long)chunkX << 32) | (uint)chunkZ;
        if (cache.Chunk == chunk && cache.Provider.TryGetTarget(out TerrainDiffusionProvider cached) && cached == _provider)
            return cache.Channels;

        cache.Chunk = long.MinValue;
        if (!_provider.CoarseChannelsAt(chunkX * 32 + 16, chunkZ * 32 + 16, cache.Channels)) return null;
        cache.Chunk = chunk;
        cache.Provider.SetTarget(_provider);
        return cache.Channels;
    }

    private static int Mod(int a, int b)
    {
        int m = a % b;
        return m < 0 ? m + b : m;
    }
}
