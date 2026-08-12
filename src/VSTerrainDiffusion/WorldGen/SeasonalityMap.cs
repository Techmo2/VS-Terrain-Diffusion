using System;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// A per-region map of how much the climate swings through the year.
///
/// The packed climate integer Vintage Story already keeps has room for temperature, rainfall and
/// geologic activity and nothing else — its interpolator only touches the low three bytes — so the
/// model's two seasonality channels need somewhere of their own. Map region mod data is the right
/// home: it is saved with the region and, unlike the region's other maps, it is sent to clients
/// along with everything else, so a client that has this mod installed swings its weather in step
/// with the server instead of falling back to vanilla's latitude bands.
///
/// Stored as (sigma * 8) &lt;&lt; 16 | precipitationCv &lt;&lt; 8, which survives the per-byte
/// bilinear interpolation the game uses on region maps.
/// </summary>
public static class SeasonalityMap
{
    public const string ModDataKey = "vsterraindiffusion:seasonality";

    /// <summary>Fixed-point steps per degree in the stored temperature sigma.</summary>
    private const float SigmaScale = 8f;

    /// <summary>Decoded maps, keyed by the region they came from and dropped when it is.</summary>
    private static readonly ConditionalWeakTable<IMapRegion, IntDataMap2D> Decoded = new();

    /// <summary>Seasonality at one position, or null where no map has been generated.</summary>
    public readonly struct Sample
    {
        /// <summary>Standard deviation of monthly mean temperature, degrees Celsius.</summary>
        public readonly float TemperatureSigmaC;

        /// <summary>Precipitation coefficient of variation across the year, percent.</summary>
        public readonly float PrecipitationCv;

        public Sample(float temperatureSigmaC, float precipitationCv)
        {
            TemperatureSigmaC = temperatureSigmaC;
            PrecipitationCv = precipitationCv;
        }
    }

    /// <summary>
    /// Builds the map for one region from the model, at the same resolution as the climate map.
    /// </summary>
    public static void Generate(IMapRegion region, int regionX, int regionZ, int regionSize,
                                TerrainDiffusionProvider provider)
    {
        int inner = Math.Max(1, regionSize / TerraGenConfig.climateMapScale);
        int size = inner + 1;

        var map = new IntDataMap2D
        {
            Size = size,
            BottomRightPadding = 1,
            Data = new int[size * size]
        };

        TerrainTile tile = null;
        for (int z = 0; z < size; z++)
        {
            int blockZ = regionZ * regionSize + z * TerraGenConfig.climateMapScale;
            for (int x = 0; x < size; x++)
            {
                int blockX = regionX * regionSize + x * TerraGenConfig.climateMapScale;

                provider.GetTileAt(blockX, blockZ, ref tile);
                int index = tile.Index(
                    Mod(blockX - tile.BlockX, tile.Size),
                    Mod(blockZ - tile.BlockZ, tile.Size));

                int sigma = GameMath.Clamp(
                    (int)Math.Round(tile.TemperatureSeasonality[index] / 100f * SigmaScale), 0, 255);
                int cv = GameMath.Clamp((int)Math.Round(tile.PrecipitationCv[index]), 0, 255);

                map.Data[z * size + x] = (sigma << 16) | (cv << 8);
            }
        }

        region.SetModdata(ModDataKey, SerializerUtil.Serialize(map));
    }

    /// <summary>
    /// Reads seasonality at a world position, or null if this world has no seasonality map — an
    /// unmodified world, a region generated before the mod was added, or a client whose server
    /// does not run it.
    /// </summary>
    public static Sample? At(IBlockAccessor blockAccessor, BlockPos pos)
    {
        int regionSize = blockAccessor.RegionSize;
        IMapRegion region = blockAccessor.GetMapRegion(pos.X / regionSize, pos.Z / regionSize);
        if (region == null) return null;

        IntDataMap2D map = MapFor(region);
        if (map == null || map.Size == 0) return null;

        float x = (float)((double)pos.X / regionSize % 1.0);
        float z = (float)((double)pos.Z / regionSize % 1.0);
        int packed = map.GetUnpaddedColorLerpedForNormalizedPos(x, z);

        return new Sample(((packed >> 16) & 0xFF) / SigmaScale, (packed >> 8) & 0xFF);
    }

    private static IntDataMap2D MapFor(IMapRegion region)
    {
        if (Decoded.TryGetValue(region, out IntDataMap2D cached)) return cached;

        IntDataMap2D map = null;
        try
        {
            byte[] raw = region.GetModdata(ModDataKey);
            if (raw != null) map = SerializerUtil.Deserialize<IntDataMap2D>(raw);
        }
        catch (Exception)
        {
            // A region written by an older or newer version is not worth killing a climate lookup
            // over; fall back to vanilla seasons for it.
            map = null;
        }

        // Cache the miss too, so a vanilla region is not deserialised on every temperature read.
        Decoded.AddOrUpdate(region, map ?? IntDataMap2D.CreateEmpty());
        return map;
    }

    private static int Mod(int a, int b)
    {
        int m = a % b;
        return m < 0 ? m + b : m;
    }
}
