using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using VSTerrainDiffusion.Api;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.WorldGen;

namespace VSTerrainDiffusion.Commands;

/// <summary>
/// The <c>/tdiff</c> subcommands that read back what the model and the game say about one column:
/// <c>here</c>, <c>climate</c>, <c>season</c> and <c>column</c>.
/// </summary>
public sealed class ClimateReadouts
{
    /// <summary>Season names for the quarters <c>/tdiff season</c> samples, from the start of the year.</summary>
    private static readonly string[] QuarterNames = { "midwinter", "spring", "midsummer", "autumn" };

    private readonly ICoreServerAPI _api;
    private readonly TerrainDiffusionModSystem _mod;

    /// <summary>Creates the readouts for one server.</summary>
    /// <param name="api">The server API.</param>
    /// <param name="mod">The mod system whose current world state the readouts use.</param>
    public ClimateReadouts(ICoreServerAPI api, TerrainDiffusionModSystem mod)
    {
        _api = api;
        _mod = mod;
    }

    /// <summary><c>/tdiff here</c>: the model's elevation and climate at the caller's position.</summary>
    public TextCommandResult OnHere(TextCommandCallingArgs args)
    {
        TerrainDiffusionProvider provider = _mod.Provider;
        if (provider == null) return TextCommandResult.Error("Terrain Diffusion is not running in this world.");

        BlockPos position = args.Caller.Entity.Pos.AsBlockPos;
        TerrainTile tile = provider.GetTileAt(position.X, position.Z);
        int index = tile.Index(position.X - tile.BlockX, position.Z - tile.BlockZ);
        Bioclim climate = tile.ClimateAt(index);
        RainfallScale rainfall = RainfallScale.FromConfig(DiffusionConfig.Instance.WorldGen);

        var lines = new List<string> { $"Terrain Diffusion at ({position.X}, {position.Z})", "" };
        lines.AddRange(PlaceLines(position));
        lines.Add("");
        lines.Add($"Elevation: {tile.ElevationMeters[index]:0} m");
        lines.Add($"Surface block: Y {tile.SurfaceY[index]}");
        lines.Add($"Slope: {tile.Slope[index] * 100f:0.#}% (bare rock above {climate.BareSlopeThreshold * 100f:0}%)");
        lines.Add("");
        lines.Add($"Mean temperature: {climate.MeanTemperatureC:0.#} C");
        lines.AddRange(TemperatureLines(position));
        lines.Add($"Coldest month: {climate.ColdestMonthC:0.#} C");
        lines.Add($"Warmest month: {climate.WarmestMonthC:0.#} C");
        lines.Add($"Temperature seasonality: {climate.TemperatureSigmaC:0.#} C sigma");
        lines.Add("");
        lines.Add($"Precipitation: {climate.PrecipitationMm:0} mm/year");
        lines.AddRange(PrecipitationLines(position, climate));
        lines.Add($"Precipitation seasonality: {climate.PrecipitationCv:0}%");
        lines.Add("");
        lines.Add($"Aridity index: {climate.AridityIndex:0.00}");
        lines.Add($"Potential evapotranspiration: {climate.PotentialEvapotranspirationMm:0} mm");
        lines.Add($"Tree moisture: {climate.TreeMoisture:0.00}");
        lines.Add($"Growing season: {climate.GrowingSeasonDays:0} days");
        lines.Add("");
        lines.Add($"Game rainfall: {rainfall.ToRainfall(climate)} / 255");
        lines.Add($"Forest cover: {climate.ForestDensity:0.00}");
        lines.Add($"Shrub cover: {climate.ShrubDensity:0.00}");

        return TextCommandResult.Success(string.Join("\n", lines));
    }

    /// <summary>
    /// <c>/tdiff climate [x z]</c>: <see cref="TerrainClimate"/>'s answer for one column, the same one
    /// other mods get, the caller's by default.
    /// </summary>
    public TextCommandResult OnClimate(TextCommandCallingArgs args)
    {
        TerrainClimate terrainClimate = _mod.Climate;
        if (terrainClimate == null) return TextCommandResult.Error("Terrain Diffusion is not generating this world.");

        if (!TerrainDiffusionCommands.TryPositionOrCaller(args, 0, out int x, out int z))
            return TextCommandResult.Error("Give a position: /tdiff climate <x> <z>");

        if (!terrainClimate.TryGetClimate(x, z, out ClimateRecord c))
            return TextCommandResult.Error("The climate could not be computed there.");

        CultureInfo culture = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            $"Climate at ({x}, {z}), {terrainClimate.ClimateModel} model" + (c.HasSeasonalCycle ? "" : " (seasonal cycle approximated)"),
            string.Format(culture, "Ground {0:0} m; annual mean {1:0.0} C, seasonality {2:0} (sd {3:0.0} C); " +
                "rain {4:0} mm, CV {5:0}%; diurnal range {6}",
                c.ElevationMeters, c.TemperatureC, c.TemperatureSeasonality, c.TemperatureSeasonality / 100f,
                c.PrecipitationMm, c.PrecipitationCv, float.IsNaN(c.DiurnalRangeC) ? "n/a" : $"{c.DiurnalRangeC:0.0} C")
        };

        if (c.IsWater)
        {
            lines.Add("Under the sea: no Köppen class.");
            return TextCommandResult.Success(string.Join("\n", lines));
        }

        MonthlyClimate months = c.Months();
        lines.Add("Months from midsummer:  " + string.Join(" ", months.TemperatureC.Select(t => t.ToString("0", culture).PadLeft(4))) + " C");
        lines.Add("                        " + string.Join(" ", months.PrecipitationMm.Select(p => p.ToString("0", culture).PadLeft(4))) + " mm");

        int hard = c.KoppenClass;
        float[] soft = c.KoppenMemberships();
        string blend = string.Join(", ", Enumerable.Range(0, Koppen.Count)
            .Where(i => soft[i] >= 0.01f)
            .OrderByDescending(i => soft[i])
            .Take(4)
            .Select(i => $"{Koppen.Classes[i].Code} {soft[i] * 100f:0}%"));
        lines.Add($"Köppen {Koppen.Classes[hard].Code} ({Koppen.Classes[hard].Name}); blend: {blend}");
        return TextCommandResult.Success(string.Join("\n", lines));
    }

    /// <summary>
    /// <c>/tdiff season x z</c>: walks a year at one position and prints what the climate does, the
    /// only practical way to see whether the model's seasonality survived the trip through the region map.
    /// </summary>
    public TextCommandResult OnSeason(TextCommandCallingArgs args)
    {
        int x = (int)args[0];
        int z = (int)args[1];

        IMapChunk mapChunk = _api.WorldManager.GetMapChunk(x / 32, z / 32);
        if (mapChunk == null) return RequestLoad(x / 32, z / 32);

        int y = mapChunk.RainHeightMap[ColumnIndex(x, z)] + 1;
        var pos = new BlockPos(x, y, z);

        SeasonalityMap.Sample? seasonality = SeasonalityMap.At(_api.World.BlockAccessor, pos);
        var lines = new List<string> { $"Terrain Diffusion at ({x}, {y}, {z})", "" };
        lines.AddRange(PlaceLines(pos));
        lines.Add("");
        lines.AddRange(TemperatureLines(pos));

        if (seasonality == null)
        {
            lines.Add("Seasonality: none mapped here, so vanilla's latitude seasons apply.");
        }
        else
        {
            lines.Add($"Temperature seasonality: {seasonality.Value.TemperatureSigmaC:0.0} C sigma");
            lines.Add($"Precipitation seasonality: {seasonality.Value.PrecipitationCv:0}%");
        }
        lines.Add("");
        lines.Add("Through the year, at midday:");

        // Midday on the first day of each season, so the numbers are comparable to each other.
        // Rainfall is reported as a share of the place's annual average rather than as the
        // instantaneous value, which only says whether it happens to be raining at that moment.
        var seasons = _api.ModLoader.GetModSystem<DiffusionSeasons>();
        double daysPerYear = _api.World.Calendar.DaysPerYear;

        for (int quarter = 0; quarter < QuarterNames.Length; quarter++)
        {
            double totalDays = daysPerYear * quarter / 4.0 + 0.5;
            ClimateCondition conditions = _api.World.BlockAccessor.GetClimateAt(
                pos, EnumGetClimateMode.ForSuppliedDate_TemperatureRainfallOnly, totalDays);
            if (conditions == null) continue;

            float share = seasons != null && seasonality != null
                ? seasons.RainfallFactorAt(pos, totalDays, seasonality.Value.PrecipitationCv)
                : 1f;
            lines.Add($"  {QuarterNames[quarter],-10} {conditions.Temperature,6:0.0} C, {share * 100f,3:0}% of average rainfall");
        }

        return TextCommandResult.Success(string.Join("\n", lines));
    }

    /// <summary>
    /// <c>/tdiff column x z</c>: what actually ended up in the world at a column, beside what the model
    /// predicted and what the game reads there.
    /// </summary>
    public TextCommandResult OnColumn(TextCommandCallingArgs args)
    {
        int x = (int)args[0];
        int z = (int)args[1];

        int chunkX = x / 32, chunkZ = z / 32;
        IMapChunk mapChunk = _api.WorldManager.GetMapChunk(chunkX, chunkZ);
        if (mapChunk == null) return RequestLoad(chunkX, chunkZ);

        int index2d = ColumnIndex(x, z);
        int terrainHeight = mapChunk.WorldGenTerrainHeightMap[index2d];
        int rainHeight = mapChunk.RainHeightMap[index2d];

        var lines = new List<string>
        {
            $"Terrain Diffusion column ({x}, {z})",
            "",
            $"Terrain height: Y {terrainHeight}",
            $"Rain height: Y {rainHeight}",
            $"Sea level: Y {_mod.Settings.SeaLevel}",
            "",
            "Blocks:"
        };
        lines.AddRange(BlockLines(x, z, rainHeight, terrainHeight));

        TerrainDiffusionProvider provider = _mod.Provider;
        if (provider != null)
        {
            TerrainTile tile = provider.GetTileAt(x, z);
            int index = tile.Index(x - tile.BlockX, z - tile.BlockZ);
            Bioclim climate = tile.ClimateAt(index);
            lines.Add("");
            lines.Add("What the model said:");
            lines.Add($"  Elevation: {tile.ElevationMeters[index]:0} m (Y {tile.SurfaceY[index]})");
            lines.Add($"  Temperature: {climate.MeanTemperatureC:0.#} C");
            lines.Add($"  Precipitation: {climate.PrecipitationMm:0} mm/year");
            lines.Add($"  Tree moisture: {climate.TreeMoisture:0.00}");
        }

        // What the game actually reads at the surface, after blending and its own altitude
        // corrections - this is the number that decides block layers and vegetation.
        ClimateCondition surface = _api.World.BlockAccessor.GetClimateAt(
            new BlockPos(x, rainHeight, z), EnumGetClimateMode.WorldGenValues);
        if (surface != null)
        {
            lines.Add("");
            lines.Add("What the game reads at the surface:");
            lines.Add($"  Temperature: {surface.Temperature:0.#} C");
            lines.Add($"  Rainfall: {surface.Rainfall:0.##}");
            lines.Add($"  Fertility: {surface.Fertility:0.##}");
            lines.Add($"  Forest: {surface.ForestDensity:0.##}");
            lines.Add($"  Shrubs: {surface.ShrubDensity:0.##}");
        }

        return TextCommandResult.Success(string.Join("\n", lines));
    }

    /// <summary>The solid and fluid blocks at the heights worth looking at in one column.</summary>
    private IEnumerable<string> BlockLines(int x, int z, int rainHeight, int terrainHeight)
    {
        IBlockAccessor accessor = _api.World.BlockAccessor;
        foreach (int y in new[] { rainHeight + 1, rainHeight, terrainHeight, terrainHeight - 1, terrainHeight - 4, 1 })
        {
            if (y < 0 || y >= _api.WorldManager.MapSizeY) continue;
            var pos = new BlockPos(x, y, z);
            Block block = accessor.GetBlock(pos);
            Block fluid = accessor.GetBlock(pos, BlockLayersAccess.Fluid);
            yield return $"  y={y,4}  {block?.Code?.ToShortString() ?? "air"}" +
                         (fluid != null && fluid.BlockId != 0 ? $" + fluid {fluid.Code?.ToShortString()}" : "");
        }
    }

    /// <summary>
    /// Asks for a chunk column that is not loaded, and pins it: chunks can only be force-loaded during
    /// startup, and an unvisited chunk would otherwise be dropped before the command runs again.
    /// </summary>
    private TextCommandResult RequestLoad(int chunkX, int chunkZ)
    {
        _api.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ, new ChunkLoadOptions { KeepLoaded = true });
        return TextCommandResult.Success(
            "That chunk is not loaded yet; a load was requested. Run the command again in a moment.");
    }

    /// <summary>A block column's index in its map chunk's 32x32 heightmaps, negative coordinates included.</summary>
    private static int ColumnIndex(int x, int z) => (z % 32 + 32) % 32 * 32 + (x % 32 + 32) % 32;

    /// <summary>
    /// Where a column sits between equator and pole, and which way round its year runs there. The
    /// hemisphere is the game's own, so the season named here is the one every other system will
    /// think it is.
    /// </summary>
    private IEnumerable<string> PlaceLines(BlockPos pos)
    {
        double latitude = _api.World.Calendar.OnGetLatitude(pos.Z);
        yield return $"Latitude: {Math.Abs(latitude) * 90.0:0.0} deg {(latitude > 0.0 ? "north" : "south")}";
        yield return $"Hemisphere: {(latitude > 0.0 ? "northern" : "southern")}, " +
                     $"currently {_api.World.Calendar.GetSeason(pos)}";
        yield return $"Latitude bands: {_mod.Settings?.Latitude.Status ?? "unknown"}";
    }

    /// <summary>
    /// The workings behind one column's temperature: the same reading with the altitude taken back
    /// out, and what the latitude band was aiming for. Empty when the pipeline cannot be re-queried.
    /// </summary>
    /// <remarks>
    /// The two answer different questions. The band is a median over all the land in its belt,
    /// relief included, so the like-for-like figure is the surface reading - and one column scatters
    /// either side of it by several degrees, which is the point of having a model rather than a
    /// gradient. Sea level is the number to compare two places by, because it has the mountain taken
    /// out of it.
    /// </remarks>
    private IEnumerable<string> TemperatureLines(BlockPos pos)
    {
        TerrainDiffusionProvider provider = _mod.Provider;
        DiffusionWorldSettings settings = _mod.Settings;
        if (provider == null || settings == null) yield break;

        TerrainDiffusionProvider.ColumnDetail? detail = provider.GetColumnDetail(pos.X, pos.Z);
        if (detail != null)
        {
            // Through the same corrections the surface value went through, so the two compare. The
            // elevation behind it is the model pixel's own, which differs from the column's by the
            // upsampling and the slope noise, so it is not repeated here.
            float seaLevel = settings.WorldTemperature(detail.Value.SeaLevelTemperatureC, pos.Z);
            yield return $"Sea-level temperature: {seaLevel:0.#} C " +
                         $"(lapse {detail.Value.LapseRateKPerKm:0.0} C/km)";
        }

        if (settings.Latitude.IsNeutral) yield break;

        TerrainTile tile = provider.GetTileAt(pos.X, pos.Z);
        float surface = tile.TemperatureC[tile.Index(pos.X - tile.BlockX, pos.Z - tile.BlockZ)];
        float band = settings.Latitude.BandTemperatureC(pos.Z);
        float offset = settings.Latitude.TemperatureOffsetC(pos.Z);

        yield return $"Band temperature: {band:0.#} C (median over this belt's land; " +
                     $"this column is {surface - band:+0.#;-0.#} C)";
        if (Math.Abs(offset) >= 0.05f)
        {
            yield return $"Band offset applied: {offset:+0.#;-0.#} C, added after the model ran " +
                         "because the conditioning could not reach it";
        }
    }

    /// <summary>What the latitude band asked for in rainfall, against what this column reads.</summary>
    private IEnumerable<string> PrecipitationLines(BlockPos pos, Bioclim climate)
    {
        DiffusionWorldSettings settings = _mod.Settings;
        if (settings == null || settings.Latitude.IsNeutral) yield break;

        float band = settings.Latitude.BandPrecipitationMm(pos.Z);
        yield return $"Band precipitation: {band:0} mm (median over this belt's land; " +
                     $"this column is {climate.PrecipitationMm / Math.Max(1f, band):0.00}x)";
    }
}
