using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Native;
using VSTerrainDiffusion.Pipeline;
using VSTerrainDiffusion.WorldGen;

namespace VSTerrainDiffusion;

/// <summary>
/// Entry point. Swaps vanilla's terrain generator for the diffusion heightmap generator and points
/// the climate, forest, shrub and ocean maps at the same model, leaving every other world
/// generation pass alone.
/// </summary>
public class TerrainDiffusionModSystem : ModSystem
{
    private ICoreServerAPI _api;
    private DiffusionWorldSettings _settings;
    private TerrainDiffusionProvider _provider;
    private GenDiffusionTerra _generator;
    private DiffusionSurface _surface;
    private ChunkColumnGenerationDelegate _installedHandler;

    /// <summary>
    /// Runs after every vanilla world generation system has registered (GenTerra is 0.0,
    /// GenMaps and GenRockStrataNew are 0.1), so the handler lists are complete when we edit them.
    /// </summary>
    public override double ExecuteOrder() => 0.5;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        _api = api;
        DiffusionConfig.Load(api);

        api.Event.InitWorldGenerator(OnInitWorldGenerator, "standard");
        api.Event.MapRegionGeneration(OnMapRegionGeneration, "standard");

        // After GenBlockLayers, which is registered on the same pass at execute order 0.4.
        api.Event.ChunkColumnGeneration(OnSurfacePass, EnumWorldGenPass.TerrainFeatures, "standard");

        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, ApplyPendingSpawn);
        api.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, OnShutdown);

        RegisterCommands(api);

        // Model download and session creation are slow, so start them the moment the server boots
        // rather than when the first chunk is requested. The HTTP API needs none of this.
        if (DiffusionConfig.Instance.Terrain.Source == "local") PipelineModels.BeginLoad(api.Logger);
    }

    private void OnInitWorldGenerator()
    {
        DiffusionConfig config = DiffusionConfig.Instance;

        try
        {
            _settings = DiffusionWorldSettings.FromWorld(_api, config.Terrain.NativeResolutionMeters);
        }
        catch (Exception e)
        {
            _api.Logger.Error("[{0}] Could not read world settings: {1}", DiffusionPaths.ModId, e);
            return;
        }

        if (!_settings.Enabled)
        {
            _api.Logger.Notification("[{0}] Disabled for this world; vanilla terrain generation is unchanged.",
                DiffusionPaths.ModId);
            return;
        }

        ITerrainSource source = CreateSource(config);
        if (source == null) return;

        // The native resolution is only known for certain once the source exists, and the whole
        // metre-to-block mapping hangs off it.
        _settings = DiffusionWorldSettings.FromWorld(_api, source.NativeResolutionMeters);

        _provider?.Dispose();
        _provider = new TerrainDiffusionProvider(source, _settings, _api.Logger);

        // The spawn search can run before the height mapping is settled - and it should, because
        // the survey wants to be centred on where people will actually play.
        (int X, int Z)? spawn = FindSpawn();

        // Must happen before anything asks for a tile: it decides the metre-to-block mapping every
        // surface height is computed from.
        CalibrateTerrainHeight(spawn);

        _generator = new GenDiffusionTerra(_api, _provider, _settings);
        _surface = new DiffusionSurface(_api, _provider);

        InstallTerrainHandler();
        InstallMapLayers();

        if (config.WorldGen.RescaleBlockLayerAltitudes && !_settings.IsIsotropic)
        {
            try
            {
                BlockLayerAltitude.Apply(_api, _settings);
            }
            catch (Exception e)
            {
                _api.Logger.Warning("[{0}] Could not adjust the surface block layer altitudes: {1}",
                    DiffusionPaths.ModId, e.Message);
            }
        }

        RecordSpawn(spawn);

        _api.Logger.Notification("[{0}] Active: {1}", DiffusionPaths.ModId, _settings.Describe());
        _api.Logger.Notification("[{0}] Terrain source: {1}", DiffusionPaths.ModId, source.Describe());
        WarnAboutWorldHeight();
    }

    /// <summary>Builds the configured terrain source, or null if it is not usable.</summary>
    private ITerrainSource CreateSource(DiffusionConfig config)
    {
        if (config.Terrain.Source == "local")
        {
            try
            {
                if (!PipelineModels.IsReady && !ModelAssetManager.AllPresent())
                {
                    _api.Logger.Notification(
                        "[{0}] Downloading the Terrain Diffusion models ({1}). This happens once; the server will finish starting when it completes.",
                        DiffusionPaths.ModId, ModelAssetManager.HumanBytes(ModelAssetManager.TotalBytes));
                }
                return new LocalTerrainSource(WorldSeed(), PipelineModels.Await());
            }
            catch (Exception e)
            {
                _api.Logger.Error(
                    "[{0}] Terrain Diffusion is enabled for this world but the models could not be loaded, so vanilla terrain will be generated instead. {1}",
                    DiffusionPaths.ModId, e.InnerException?.Message ?? e.Message);
                return null;
            }
        }

        var api = new TerrainApiSource(config.Terrain, _api.Logger);
        string problem = api.CheckHealth();
        if (problem == null) return api;

        api.Dispose();
        _api.Logger.Error(
            "[{0}] No Terrain Diffusion API at {1} ({2}), so vanilla terrain will be generated instead. " +
            "Start one with 'python -m terrain_diffusion api', or set terrain.source to \"local\" in the mod " +
            "config to use the models bundled with this mod.",
            DiffusionPaths.ModId, config.Terrain.Url, problem);
        return null;
    }

    private void WarnAboutWorldHeight()
    {
        // A temperate 10 C place is the useful yardstick: colder ground has scale to spare and
        // hotter ground is rarely high.
        float temperatureCeiling = _settings.TemperatureCeilingMeters(10f);
        if (temperatureCeiling < 2500f)
        {
            _api.Logger.Warning(
                "[{0}] At {1:0.##} m per block of height, Vintage Story's one-byte climate map runs out of scale " +
                "above about {2:0} m, and warmer ground above that will read colder than the model intended. " +
                "A coarser diffusion resolution avoids it.",
                DiffusionPaths.ModId, _settings.MetersPerBlockVertical, temperatureCeiling);
        }

        if (_settings.CalibrationClamped)
        {
            _api.Logger.Warning(
                "[{0}] Terrain height was capped at {1:0.##}x real scale by maxAutoExaggeration, so this region's " +
                "peaks will stop short of the world ceiling. Raise worldGen.maxAutoExaggeration in the mod config " +
                "if you want them taller.",
                DiffusionPaths.ModId, _settings.EffectiveExaggeration);
        }
        else if (_settings.IsWorldTooShort)
        {
            _api.Logger.Warning(
                "[{0}] This world is {1} blocks tall, leaving {2} blocks above sea level, so terrain above about " +
                "{3} m gets compressed. At {4:0.##} m per block a world height of {5} would hold this landscape at " +
                "true scale; alternatively set worldGen.heightMode to \"auto\" to stretch the terrain to fit " +
                "instead.",
                DiffusionPaths.ModId, _settings.MapSizeY, _settings.HeadroomBlocks,
                (int)_settings.LinearRangeMeters, _settings.MetersPerBlock, _settings.RecommendedMapSizeY);
        }
    }

    /// <summary>
    /// Records the model's temperature and precipitation seasonality for the region, which the
    /// climate map has no room for and the seasons need at runtime.
    /// </summary>
    private void OnMapRegionGeneration(IMapRegion mapRegion, int regionX, int regionZ, ITreeAttribute chunkGenParams)
    {
        if (_provider == null || _settings is not { Enabled: true }) return;
        if (_settings.ClimateMode == DiffusionClimateMode.Off) return;

        try
        {
            SeasonalityMap.Generate(mapRegion, regionX, regionZ, _api.WorldManager.RegionSize, _provider);
        }
        catch (Exception e)
        {
            _api.Logger.Warning("[{0}] Could not write the seasonality map for region ({1}, {2}): {3}",
                DiffusionPaths.ModId, regionX, regionZ, e.Message);
        }
    }

    private void OnSurfacePass(IChunkColumnGenerateRequest request)
    {
        if (_surface is not { Enabled: true }) return;

        try
        {
            _surface.OnChunkColumnGeneration(request);
        }
        catch (Exception e)
        {
            _api.Logger.Warning("[{0}] Surface pass failed for chunk ({1}, {2}): {3}",
                DiffusionPaths.ModId, request.ChunkX, request.ChunkZ, e.Message);
        }
    }

    /// <summary>Save game key holding the measured peak elevation, in metres.</summary>
    private const string CalibrationSaveKey = "vsterraindiffusion:peakelevation";

    /// <summary>
    /// Fits the metre-to-block mapping to the terrain this seed actually produces around spawn, for
    /// worlds using <c>heightMode: "auto"</c>. Not the default: at the shipped isotropic scale the
    /// mapping is fixed and nothing needs measuring.
    ///
    /// The measurement costs a few model invocations, so it is kept in the save game: the answer
    /// only depends on the seed, but paying for it on every server start would be wasteful, and a
    /// stored value also keeps old chunks and new chunks agreeing if the survey settings change.
    /// </summary>
    private void CalibrateTerrainHeight((int X, int Z)? center)
    {
        if (!_settings.WantsCalibration) return;

        byte[] stored = null;
        try
        {
            stored = _api.WorldManager.SaveGame.GetData(CalibrationSaveKey);
        }
        catch (Exception e)
        {
            _api.Logger.VerboseDebug("[{0}] Could not read the stored terrain height calibration: {1}",
                DiffusionPaths.ModId, e.Message);
        }

        if (stored is { Length: sizeof(float) })
        {
            float saved = BitConverter.ToSingle(stored, 0);
            _settings.ApplyCalibration(saved);
            _api.Logger.VerboseDebug("[{0}] Reusing the stored terrain height calibration ({1:0} m peak).",
                DiffusionPaths.ModId, saved);
            return;
        }

        _api.Logger.Notification(
            "[{0}] Measuring how tall the terrain gets around spawn so the world height can be used fully. " +
            "This runs the model a few times and only happens once for this world.",
            DiffusionPaths.ModId);

        float? peak;
        try
        {
            peak = _provider.MeasurePeakElevation(
                center?.X ?? _settings.OriginBlockX, center?.Z ?? _settings.OriginBlockZ);
        }
        catch (Exception e)
        {
            _api.Logger.Warning("[{0}] Terrain height calibration failed, falling back to true real-world scale: {1}",
                DiffusionPaths.ModId, e.Message);
            return;
        }

        if (peak == null) return;

        _settings.ApplyCalibration(peak.Value);

        try
        {
            _api.WorldManager.SaveGame.StoreData(CalibrationSaveKey, BitConverter.GetBytes(peak.Value));
        }
        catch (Exception e)
        {
            _api.Logger.Warning("[{0}] Could not store the terrain height calibration; it will be measured again next start: {1}",
                DiffusionPaths.ModId, e.Message);
        }
    }

    private ulong WorldSeed() => (ulong)(uint)_api.WorldManager.Seed;

    /// <summary>
    /// Replaces vanilla GenTerra's Terrain-pass delegate in place, so the ordering relative to
    /// rock strata, caves and block layers is exactly what those systems expect.
    /// </summary>
    private void InstallTerrainHandler()
    {
        IWorldGenHandler handlers = _api.Event.GetRegisteredWorldGenHandlers("standard");
        List<ChunkColumnGenerationDelegate> terrainPass = handlers.OnChunkColumnGen[(int)EnumWorldGenPass.Terrain];

        ChunkColumnGenerationDelegate replacement = _generator.OnChunkColumnGen;

        // Re-initialisation (for example /wgen regen) hits this a second time.
        if (_installedHandler != null)
        {
            int existing = terrainPass.IndexOf(_installedHandler);
            if (existing >= 0)
            {
                terrainPass[existing] = replacement;
                _installedHandler = replacement;
                return;
            }
        }

        int vanillaIndex = terrainPass.FindIndex(d => d.Target is GenTerra);
        if (vanillaIndex >= 0)
        {
            terrainPass[vanillaIndex] = replacement;
            _api.Logger.VerboseDebug("[{0}] Replaced vanilla GenTerra at terrain handler index {1}",
                DiffusionPaths.ModId, vanillaIndex);
        }
        else
        {
            // Vanilla GenTerra is missing (another mod may already have removed it); run first.
            terrainPass.Insert(0, replacement);
            _api.Logger.Warning("[{0}] Vanilla GenTerra was not found in the terrain pass; " +
                                "inserting the diffusion generator first. Another terrain mod may conflict.",
                DiffusionPaths.ModId);
        }

        _installedHandler = replacement;
    }

    /// <summary>
    /// Points GenMaps at model-backed climate, vegetation and ocean layers. All are plain map
    /// layers, so the rest of world generation keeps reading them the way it always has.
    /// </summary>
    private void InstallMapLayers()
    {
        var genMaps = _api.ModLoader.GetModSystem<GenMaps>();
        if (genMaps == null)
        {
            _api.Logger.Warning("[{0}] GenMaps is not loaded; climate and ocean maps stay vanilla.", DiffusionPaths.ModId);
            return;
        }

        // The ocean map always follows the model: otherwise systems that avoid the sea would
        // disagree with the coastline that actually got generated.
        genMaps.oceanGen = new DiffusionOceanMapLayer(_api.WorldManager.Seed + 1873, _provider);

        if (_settings.ClimateMode == DiffusionClimateMode.Off)
        {
            _api.Logger.Notification("[{0}] Model climate disabled for this world; using vanilla climate maps.",
                DiffusionPaths.ModId);
            return;
        }

        // Wrap the vanilla climate layer rather than replace it: its geologic activity byte has
        // nothing to do with climate and is still wanted. On re-initialisation GenMaps rebuilds
        // climateGen, but unwrap defensively anyway.
        MapLayerBase vanillaClimate = genMaps.climateGen is DiffusionClimateMapLayer alreadyWrapped
            ? alreadyWrapped.Baseline
            : genMaps.climateGen;

        if (vanillaClimate == null)
        {
            _api.Logger.Warning("[{0}] Vanilla climate layer is missing; leaving the climate map alone.",
                DiffusionPaths.ModId);
            return;
        }

        ITreeAttribute worldConfig = _api.WorldManager.SaveGame.WorldConfiguration;
        float temperatureMultiplier = worldConfig.GetString("globalTemperature", "1").ToFloat(1f);
        float rainfallMultiplier = worldConfig.GetString("globalPrecipitation", "1").ToFloat(1f);

        genMaps.climateGen = new DiffusionClimateMapLayer(
            _api.WorldManager.Seed + 1, vanillaClimate, _provider,
            _api.World.SeaLevel, temperatureMultiplier, rainfallMultiplier);

        WorldGenConfig worldGen = DiffusionConfig.Instance.WorldGen;
        genMaps.forestGen = new DiffusionForestMapLayer(
            _api.WorldManager.Seed + 2, _provider, TerraGenConfig.forestMapScale, false,
            worldGen.ForestDensityMultiplier);
        genMaps.bushGen = new DiffusionForestMapLayer(
            _api.WorldManager.Seed + 3, _provider, TerraGenConfig.shrubMapScale, true,
            worldGen.ShrubDensityMultiplier);
    }

    /// <summary>
    /// Vanilla guarantees land at the map centre by forcing the ocean map; since the model decides
    /// where continents are, that guarantee is gone and the spawn has to be found instead.
    ///
    /// It runs on every start, not just new saves, so that an existing world's height survey stays
    /// centred where it always was.
    /// </summary>
    private (int X, int Z)? FindSpawn()
    {
        try
        {
            (int X, int Z)? land = _provider.FindLandNearOrigin();
            if (land == null)
            {
                _api.Logger.Warning(
                    "[{0}] No land found near the world centre; the spawn point was left where it was. " +
                    "Try a different seed if you start in the ocean.", DiffusionPaths.ModId);
            }
            return land;
        }
        catch (Exception e)
        {
            _api.Logger.Warning("[{0}] Spawn search failed, keeping the default spawn: {1}",
                DiffusionPaths.ModId, e.Message);
            return null;
        }
    }

    /// <summary>
    /// Turns the spawn column found earlier into a position to apply once the save game's spawn
    /// record exists. Only for brand new saves, so an existing world never has its spawn moved.
    /// </summary>
    private void RecordSpawn((int X, int Z)? land)
    {
        if (land == null || !_api.WorldManager.SaveGame.IsNew) return;

        try
        {
            TerrainTile tile = _provider.GetTileAt(land.Value.X, land.Value.Z);
            int index = tile.Index(land.Value.X - tile.BlockX, land.Value.Z - tile.BlockZ);
            int y = Math.Min(_settings.MapSizeY - 2, tile.SurfaceY[index] + 1);

            _pendingSpawn = (land.Value.X, y, land.Value.Z);
            _api.Logger.Notification("[{0}] Found a land spawn at ({1}, {2}, {3}), {4} m above sea level.",
                DiffusionPaths.ModId, land.Value.X, y, land.Value.Z, (int)tile.ElevationMeters[index]);
        }
        catch (Exception e)
        {
            _api.Logger.Warning("[{0}] Could not place the spawn on land: {1}", DiffusionPaths.ModId, e.Message);
        }
    }

    private (int X, int Y, int Z)? _pendingSpawn;

    private void ApplyPendingSpawn()
    {
        if (_pendingSpawn == null) return;
        (int x, int y, int z) = _pendingSpawn.Value;
        _pendingSpawn = null;

        try
        {
            // A brand new save has no spawn record at all, and SetDefaultSpawnPosition assumes one
            // exists. The serverconfig command creates it, so go through that instead.
            _api.InjectConsole($"/serverconfig setspawn {x} {y} {z}");
            _api.Logger.Notification("[{0}] World spawn set to ({1}, {2}, {3}).", DiffusionPaths.ModId, x, y, z);
        }
        catch (Exception e)
        {
            _api.Logger.Warning("[{0}] Could not move the world spawn to land: {1}", DiffusionPaths.ModId, e.Message);
        }
    }

    private void RegisterCommands(ICoreServerAPI api)
    {
        api.ChatCommands
            .Create("terraindiffusion")
            .WithAlias("tdiff")
            .WithDescription("Inspect the Terrain Diffusion generator")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("status")
                .WithDescription("Show the model, device and world scaling in use")
                .HandleWith(OnStatusCommand)
            .EndSubCommand()
            .BeginSubCommand("here")
                .WithDescription("Show the model's elevation and climate at your position")
                .RequiresPlayer()
                .HandleWith(OnHereCommand)
            .EndSubCommand()
            .BeginSubCommand("season")
                .WithDescription("Show the seasonal temperature and rainfall cycle at a position")
                .WithArgs(api.ChatCommands.Parsers.Int("x"), api.ChatCommands.Parsers.Int("z"))
                .HandleWith(OnSeasonCommand)
            .EndSubCommand()
            .BeginSubCommand("column")
                .WithDescription("Read back the generated block column at a position, for diagnosing world generation")
                .WithArgs(api.ChatCommands.Parsers.Int("x"), api.ChatCommands.Parsers.Int("z"))
                .HandleWith(OnColumnCommand)
            .EndSubCommand();
    }

    private Vintagestory.API.Common.TextCommandResult OnStatusCommand(Vintagestory.API.Common.TextCommandCallingArgs args)
    {
        if (_settings == null)
        {
            return Vintagestory.API.Common.TextCommandResult.Success("Terrain Diffusion has not initialised yet.");
        }
        if (!_settings.Enabled)
        {
            return Vintagestory.API.Common.TextCommandResult.Success("Terrain Diffusion is disabled for this world.");
        }
        if (_provider == null)
        {
            string reason = PipelineModels.LoadFailure?.Message ?? "the terrain source could not be reached";
            return Vintagestory.API.Common.TextCommandResult.Success("Terrain Diffusion is not running: " + reason);
        }

        string device = DiffusionConfig.Instance.Terrain.Source == "local"
            ? $"\nDevice: {OnnxRuntimeBootstrap.Provider} (ONNX Runtime {OnnxRuntimeBootstrap.OnnxRuntimeVersion})"
            : "";

        return Vintagestory.API.Common.TextCommandResult.Success(
            $"Terrain Diffusion active.\n" +
            $"Source: {_provider.Source.Describe()}{device}\n" +
            $"World: {_settings.Describe()}\n" +
            $"Tiles generated: {_provider.TilesGenerated} ({_provider.TileSize}x{_provider.TileSize} blocks, " +
            $"{_provider.AverageTileMillis} ms average)");
    }

    private Vintagestory.API.Common.TextCommandResult OnHereCommand(Vintagestory.API.Common.TextCommandCallingArgs args)
    {
        if (_provider == null)
        {
            return Vintagestory.API.Common.TextCommandResult.Error("Terrain Diffusion is not running in this world.");
        }

        var position = args.Caller.Entity.Pos.AsBlockPos;
        TerrainTile tile = _provider.GetTileAt(position.X, position.Z);
        int index = tile.Index(position.X - tile.BlockX, position.Z - tile.BlockZ);
        Bioclim climate = tile.ClimateAt(index);
        RainfallScale rainfall = RainfallScale.FromConfig(DiffusionConfig.Instance.WorldGen);

        return Vintagestory.API.Common.TextCommandResult.Success(
            $"At ({position.X}, {position.Z}):\n" +
            $"Elevation: {tile.ElevationMeters[index]:0} m -> block Y {tile.SurfaceY[index]}, " +
            $"slope {tile.Slope[index] * 100f:0.#}% (bare above {climate.BareSlopeThreshold * 100f:0}%)\n" +
            $"Temperature: {climate.MeanTemperatureC:0.#} C mean, " +
            $"{climate.ColdestMonthC:0.#} to {climate.WarmestMonthC:0.#} C through the year\n" +
            $"Precipitation: {climate.PrecipitationMm:0} mm/year, {climate.PrecipitationCv:0}% seasonal variation\n" +
            $"Aridity: {climate.AridityIndex:0.00} (PET {climate.PotentialEvapotranspirationMm:0} mm), " +
            $"tree moisture {climate.TreeMoisture:0.00}, growing season {climate.GrowingSeasonDays:0} days\n" +
            $"Game rainfall {rainfall.ToRainfall(climate)}/255, forest {climate.ForestDensity:0.00}, " +
            $"shrubs {climate.ShrubDensity:0.00}");
    }

    /// <summary>
    /// Walks a year at one position and prints what the climate does, which is the only practical
    /// way to see whether the model's seasonality survived the trip through the region map.
    /// </summary>
    private Vintagestory.API.Common.TextCommandResult OnSeasonCommand(Vintagestory.API.Common.TextCommandCallingArgs args)
    {
        int x = (int)args[0];
        int z = (int)args[1];

        Vintagestory.API.Common.IMapChunk mapChunk = _api.WorldManager.GetMapChunk(x / 32, z / 32);
        if (mapChunk == null)
        {
            _api.WorldManager.LoadChunkColumnPriority(x / 32, z / 32, new ChunkLoadOptions { KeepLoaded = true });
            return Vintagestory.API.Common.TextCommandResult.Success(
                "That chunk is not loaded yet; a load was requested. Run the command again in a moment.");
        }

        int y = mapChunk.RainHeightMap[(z % 32 + 32) % 32 * 32 + (x % 32 + 32) % 32] + 1;
        var pos = new Vintagestory.API.MathTools.BlockPos(x, y, z);

        SeasonalityMap.Sample? seasonality = SeasonalityMap.At(_api.World.BlockAccessor, pos);
        var lines = new List<string>
        {
            seasonality == null
                ? $"({x}, {y}, {z}): no seasonality map here, so vanilla's latitude seasons apply."
                : $"({x}, {y}, {z}): temperature seasonality {seasonality.Value.TemperatureSigmaC:0.0} C sigma, " +
                  $"precipitation seasonality {seasonality.Value.PrecipitationCv:0}%"
        };

        // Midday on the first day of each season, so the numbers are comparable to each other.
        // Rainfall is reported as a share of the place's annual average rather than as the
        // instantaneous value, which only says whether it happens to be raining at that moment.
        var seasons = _api.ModLoader.GetModSystem<DiffusionSeasons>();
        double daysPerYear = _api.World.Calendar.DaysPerYear;
        string[] names = { "midwinter", "spring", "midsummer", "autumn" };

        for (int quarter = 0; quarter < 4; quarter++)
        {
            double totalDays = daysPerYear * quarter / 4.0 + 0.5;
            Vintagestory.API.Common.ClimateCondition conditions = _api.World.BlockAccessor.GetClimateAt(
                pos, EnumGetClimateMode.ForSuppliedDate_TemperatureRainfallOnly, totalDays);
            if (conditions == null) continue;

            float share = seasons != null && seasonality != null
                ? seasons.RainfallFactorAt(pos, totalDays, seasonality.Value.PrecipitationCv)
                : 1f;
            lines.Add($"  {names[quarter],-10} {conditions.Temperature,6:0.0} C, {share * 100f,3:0}% of average rainfall");
        }

        return Vintagestory.API.Common.TextCommandResult.Success(string.Join("\n", lines));
    }

    /// <summary>
    /// Loads the chunk column at (x, z) and reports what actually ended up in the world, so the
    /// generated result can be compared against what the model predicted.
    /// </summary>
    private Vintagestory.API.Common.TextCommandResult OnColumnCommand(Vintagestory.API.Common.TextCommandCallingArgs args)
    {
        int x = (int)args[0];
        int z = (int)args[1];

        int chunkX = x / 32, chunkZ = z / 32;
        Vintagestory.API.Common.IMapChunk mapChunk = _api.WorldManager.GetMapChunk(chunkX, chunkZ);
        if (mapChunk == null)
        {
            // Chunks can only be force-loaded during startup, so ask for it (and pin it, otherwise
            // an unvisited chunk is dropped again before the next command runs) and let the caller retry.
            _api.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ, new ChunkLoadOptions { KeepLoaded = true });
            return Vintagestory.API.Common.TextCommandResult.Success(
                "That chunk is not loaded yet; a load was requested. Run the command again in a moment.");
        }

        int index2d = (z % 32 + 32) % 32 * 32 + (x % 32 + 32) % 32;
        int terrainHeight = mapChunk.WorldGenTerrainHeightMap[index2d];
        int rainHeight = mapChunk.RainHeightMap[index2d];

        var accessor = _api.World.BlockAccessor;
        var lines = new List<string>
        {
            $"Column ({x}, {z}): terrain height {terrainHeight}, rain height {rainHeight}, sea level {_api.World.SeaLevel}"
        };

        foreach (int y in new[] { rainHeight + 1, rainHeight, terrainHeight, terrainHeight - 1, terrainHeight - 4, 1 })
        {
            if (y < 0 || y >= _api.WorldManager.MapSizeY) continue;
            var pos = new Vintagestory.API.MathTools.BlockPos(x, y, z);
            Block block = accessor.GetBlock(pos);
            Block fluid = accessor.GetBlock(pos, Vintagestory.API.Common.BlockLayersAccess.Fluid);
            lines.Add($"  y={y,4}  {block?.Code?.ToShortString() ?? "air"}" +
                      (fluid != null && fluid.BlockId != 0 ? $" + fluid {fluid.Code?.ToShortString()}" : ""));
        }

        if (_provider != null)
        {
            TerrainTile tile = _provider.GetTileAt(x, z);
            int index = tile.Index(x - tile.BlockX, z - tile.BlockZ);
            Bioclim climate = tile.ClimateAt(index);
            lines.Add($"  model: {tile.ElevationMeters[index]:0} m -> Y {tile.SurfaceY[index]}, " +
                      $"{climate.MeanTemperatureC:0.#} C, {climate.PrecipitationMm:0} mm, " +
                      $"tree moisture {climate.TreeMoisture:0.00}");
        }

        // What the game actually reads at the surface, after blending and its own altitude
        // corrections - this is the number that decides block layers and vegetation.
        var surfacePos = new Vintagestory.API.MathTools.BlockPos(x, rainHeight, z);
        Vintagestory.API.Common.ClimateCondition climate2 =
            _api.World.BlockAccessor.GetClimateAt(surfacePos, EnumGetClimateMode.WorldGenValues);
        if (climate2 != null)
        {
            lines.Add($"  in game: {climate2.Temperature:0.#} C, rainfall {climate2.Rainfall:0.##}, " +
                      $"fertility {climate2.Fertility:0.##}, forest {climate2.ForestDensity:0.##}, " +
                      $"shrubs {climate2.ShrubDensity:0.##}");
        }

        return Vintagestory.API.Common.TextCommandResult.Success(string.Join("\n", lines));
    }

    private void OnShutdown()
    {
        _provider?.Dispose();
        _provider = null;
        PipelineModels.Shutdown();
    }

    public override void Dispose()
    {
        _provider?.Dispose();
        _provider = null;
    }
}
