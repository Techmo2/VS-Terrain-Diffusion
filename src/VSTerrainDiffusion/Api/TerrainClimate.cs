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

    /// <summary>The world's terrain, which owns the tiles and the coarse model output read here.</summary>
    private readonly TerrainDiffusionProvider _provider;

    /// <summary>
    /// The 2x2 coarse cells around the last column asked about, per thread, decoded. They span
    /// the model's 7.68 km cells, so one read serves many chunks, and each column blends them for
    /// itself. (Reading one value at each chunk's centre instead made the seasonal cycle and the
    /// diurnal range step at every chunk border, and with them anything chosen by Köppen class.)
    /// </summary>
    [ThreadStatic] private static CoarseCache _coarseCache;

    /// <summary>One thread's decoded coarse corners, with the provider and cells they came from.</summary>
    private sealed class CoarseCache
    {
        /// <summary>Weak, so a thread that outlives a world does not keep that world's provider alive.</summary>
        public readonly WeakReference<TerrainDiffusionProvider> Provider = new(null);

        /// <summary>
        /// The cell pair the corners belong to, packed (i0 in the high half); long.MinValue when empty.
        /// </summary>
        public long Cells = long.MinValue;

        /// <summary>The four corner cells' channels, corner-major.</summary>
        public readonly float[] Corners = new float[4 * CoarseChannel.SeasonalCount];

        /// <summary>
        /// The corners blended for the last column asked about; reused, so callers must copy.
        /// </summary>
        public readonly float[] Channels = new float[CoarseChannel.SeasonalCount];
    }

    /// <summary>Wraps a world's provider.</summary>
    /// <param name="provider">The world's terrain.</param>
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
        _provider.CoarseCellAt(blockX + 0.5, blockZ + 0.5, out int i0, out int j0, out float fi, out float fj);
        long cells = ((long)i0 << 32) | (uint)j0;
        if (cache.Cells != cells || !cache.Provider.TryGetTarget(out TerrainDiffusionProvider cached) || cached != _provider)
        {
            cache.Cells = long.MinValue;
            if (!_provider.CoarseCorners(i0, j0, cache.Corners)) return null;
            cache.Cells = cells;
            cache.Provider.SetTarget(_provider);
        }
        TerrainDiffusionProvider.InterpolateCorners(cache.Corners, _provider.CoarseSpec.Channels, fi, fj, cache.Channels);
        return cache.Channels;
    }

    /// <summary><paramref name="a"/> modulo <paramref name="b"/>, never negative.</summary>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor, positive.</param>
    /// <returns>A value in [0, b).</returns>
    private static int Mod(int a, int b)
    {
        int m = a % b;
        return m < 0 ? m + b : m;
    }
}
