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
/// the climate and ocean maps at the same model, leaving every other world generation pass alone.
/// </summary>
public class TerrainDiffusionModSystem : ModSystem
{
    private ICoreServerAPI _api;
    private DiffusionWorldSettings _settings;
    private TerrainDiffusionProvider _provider;
    private GenDiffusionTerra _generator;
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
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, ApplyPendingSpawn);
        api.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, OnShutdown);

        RegisterCommands(api);

        // Model download and session creation are slow, so start them the moment the server boots
        // rather than when the first chunk is requested.
        PipelineModels.BeginLoad(api.Logger);
    }

    private void OnInitWorldGenerator()
    {
        try
        {
            _settings = DiffusionWorldSettings.FromWorld(_api, 30f);
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

        PipelineModels models;
        try
        {
            if (!PipelineModels.IsReady && !ModelAssetManager.AllPresent())
            {
                _api.Logger.Notification(
                    "[{0}] Downloading the Terrain Diffusion models ({1}). This happens once; the server will finish starting when it completes.",
                    DiffusionPaths.ModId, ModelAssetManager.HumanBytes(ModelAssetManager.TotalBytes));
            }
            models = PipelineModels.Await();
        }
        catch (Exception e)
        {
            _api.Logger.Error(
                "[{0}] Terrain Diffusion is enabled for this world but the models could not be loaded, so vanilla terrain will be generated instead. {1}",
                DiffusionPaths.ModId, e.InnerException?.Message ?? e.Message);
            return;
        }

        // Re-read the native resolution now that the model config is on disk.
        _settings = DiffusionWorldSettings.FromWorld(_api, WorldPipelineModelConfig.Instance.NativeResolution);

        _provider?.Dispose();
        _provider = new TerrainDiffusionProvider(WorldSeed(), models, _settings, _api.Logger);

        // The spawn search only reads the coarse map, so it can run before the height mapping is
        // settled - and it should, because the survey wants to be centred on where people play.
        (int X, int Z)? spawn = FindSpawn();

        // Must happen before anything asks for a tile: it decides the metre-to-block mapping every
        // surface height is computed from.
        CalibrateTerrainHeight(spawn);

        _generator = new GenDiffusionTerra(_api, _provider, _settings);

        InstallTerrainHandler();
        InstallMapLayers();

        if (DiffusionConfig.Instance.WorldGen.RescaleBlockLayerAltitudes)
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
                "[{0}] This world is {1} blocks tall, which only leaves {2} blocks above sea level. Terrain above about {3} m will be compressed. " +
                "For terrain at this scale, a world height of {4} would keep mountains to scale.",
                DiffusionPaths.ModId, _settings.MapSizeY, _settings.HeadroomBlocks,
                (int)_settings.LinearRangeMeters, _settings.RecommendedMapSizeY);
        }
    }

    /// <summary>Save game key holding the measured peak elevation, in metres.</summary>
    private const string CalibrationSaveKey = "vsterraindiffusion:peakelevation";

    /// <summary>
    /// Fits the metre-to-block mapping to the terrain this seed actually produces around spawn, so
    /// that mountains use the world's full height instead of being reproduced at a true scale that
    /// leaves them looking like hills.
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
    /// Points GenMaps at model-backed climate and ocean layers. Both are plain map layers, so the
    /// rest of world generation keeps reading them the way it always has.
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

        // Wrap the vanilla layer rather than replace it, so latitude, startingClimate,
        // polarEquatorDistance and the global multipliers all keep working underneath.
        // On re-initialisation GenMaps rebuilds climateGen, but unwrap defensively anyway.
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
            _api.WorldManager.Seed + 1, vanillaClimate, _provider, _settings.ClimateMode,
            _api.World.SeaLevel, temperatureMultiplier, rainfallMultiplier);
    }

    /// <summary>
    /// Vanilla guarantees land at the map centre by forcing the ocean map; since the model decides
    /// where continents are, that guarantee is gone and the spawn has to be found instead.
    ///
    /// This reads only the coarse map, so it is safe to call before the height mapping is settled,
    /// and the answer doubles as the centre for the terrain height survey. It runs on every start,
    /// not just new saves, so that an existing world's survey stays centred where it always was.
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
            string reason = PipelineModels.LoadFailure?.Message ?? "still loading";
            return Vintagestory.API.Common.TextCommandResult.Success("Terrain Diffusion is not running: " + reason);
        }

        return Vintagestory.API.Common.TextCommandResult.Success(
            $"Terrain Diffusion active.\n" +
            $"Device: {OnnxRuntimeBootstrap.Provider} (ONNX Runtime {OnnxRuntimeBootstrap.OnnxRuntimeVersion})\n" +
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

        return Vintagestory.API.Common.TextCommandResult.Success(
            $"At ({position.X}, {position.Z}):\n" +
            $"Elevation: {tile.ElevationMeters[index]:0} m -> block Y {tile.SurfaceY[index]}\n" +
            $"Slope: {tile.Slope[index] * 100f:0.#}%\n" +
            $"Temperature: {tile.TemperatureC[index]:0.#} C (sea level equivalent {tile.SeaLevelTemperatureC[index]:0.#} C)\n" +
            $"Precipitation: {tile.PrecipitationMm[index]:0} mm/year " +
            $"(game rainfall {DiffusionClimateMapLayer.PrecipitationToRainfall(tile.PrecipitationMm[index])})");
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
            lines.Add($"  model: {tile.ElevationMeters[index]:0} m -> Y {tile.SurfaceY[index]}, " +
                      $"{tile.TemperatureC[index]:0.#} C, {tile.PrecipitationMm[index]:0} mm");
        }

        // What the game actually reads at the surface, after latitude, blending and its own
        // altitude corrections - this is the number that decides block layers and vegetation.
        var surfacePos = new Vintagestory.API.MathTools.BlockPos(x, rainHeight, z);
        Vintagestory.API.Common.ClimateCondition climate =
            _api.World.BlockAccessor.GetClimateAt(surfacePos, EnumGetClimateMode.WorldGenValues);
        if (climate != null)
        {
            lines.Add($"  in game: {climate.Temperature:0.#} C, rainfall {climate.Rainfall:0.##}, " +
                      $"fertility {climate.Fertility:0.##}, forest {climate.ForestDensity:0.##}");
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
