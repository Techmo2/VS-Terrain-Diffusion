using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Debug;
using VSTerrainDiffusion.Native;
using VSTerrainDiffusion.Pipeline;
using VSTerrainDiffusion.WorldGen;

namespace VSTerrainDiffusion;

/// <summary>
/// Entry point. Swaps vanilla's terrain generator for the diffusion heightmap generator and points
/// the climate, forest and shrub maps at the same model, leaving every other world generation pass
/// alone. The ocean map goes the other way: it is read as conditioning for the model rather than
/// written, so the world's own land cover and ocean scale settings still decide where the sea is.
/// </summary>
public class TerrainDiffusionModSystem : ModSystem
{
    private ICoreServerAPI _api;
    private DiffusionWorldSettings _settings;
    private TerrainDiffusionProvider _provider;
    private GenDiffusionTerra _generator;
    private DiffusionSurface _surface;
    private DebugMapServer _debugMap;
    private ChunkColumnGenerationDelegate _installedHandler;

    /// <summary>
    /// Runs after every vanilla world generation system has registered (GenTerra is 0.0,
    /// GenMaps and GenRockStrataNew are 0.1), so the handler lists are complete when we edit them.
    /// </summary>
    public override double ExecuteOrder() => 0.5;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    /// <summary>
    /// Reads the config here rather than in <see cref="StartServerSide"/> because ConfigLib takes
    /// the file over when assets load, which on a server is several phases earlier. If the file did
    /// not exist yet at that point ConfigLib would create one in its own shape - every setting flat,
    /// including the ones that belong under <c>WorldGen</c> - and the mod's own write would then
    /// throw that away, leaving ConfigLib editing a file it no longer matches.
    /// </summary>
    public override void StartPre(ICoreAPI api)
    {
        // Rivers generates terrain itself unless told not to, and it does that by stopping vanilla's
        // generator from ever registering - which is the handler this mod takes the place of. Asking
        // it to stand aside has to happen before any StartServerSide runs, and gives this mod the
        // terrain while Rivers keeps its river network. See RiversCompat.
        // Not when Watersheds is here: that takes terrain generation from both of us and already
        // arranges things with Rivers itself, and the two working together is not ours to disturb.
        bool watersheds = api.ModLoader.IsModEnabled("watersheds");

        if (api.Side == EnumAppSide.Server && !watersheds &&
            RiversCompat.IsPresent(api) && RiversCompat.StandDown(api))
        {
            api.Logger.Notification(
                "[{0}] Rivers is installed; this mod will generate the terrain and carve its rivers " +
                "into it.", DiffusionPaths.ModId);
        }

        InferenceThrottle.UtilizationPercent = DiffusionConfig.Load(api).GpuUtilizationPercent;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        _api = api;
        DiffusionFailure.UseLogger(api.Logger);
        ConfigLibCompat.Install(api);

        api.Event.InitWorldGenerator(OnInitWorldGenerator, "standard");
        api.Event.MapRegionGeneration(OnMapRegionGeneration, "standard");

        // After GenBlockLayers, which is registered on the same pass at execute order 0.4.
        api.Event.ChunkColumnGeneration(OnSurfacePass, EnumWorldGenPass.TerrainFeatures, "standard");

        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, ApplyPendingSpawn);
        api.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, OnShutdown);

        RegisterCommands(api);

        // Model download and session creation are slow, so start them the moment the server boots
        // rather than when the first chunk is requested.
        PipelineModels.BeginLoad(api.Logger);
    }

    /// <summary>
    /// The server catches whatever an InitWorldGenerator handler throws, logs it, and then goes on
    /// to generate the world without us - so nothing may escape this method. Anything that gets
    /// past the specific handling inside stops the game here instead.
    /// </summary>
    private void OnInitWorldGenerator()
    {
        try
        {
            InitWorldGenerator();
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger,
                "World generation could not be initialised.", e);
        }
    }

    private void InitWorldGenerator()
    {
        // Only the world's own on/off flag can be read before the models are: everything else in
        // the settings is derived from the model's config and its climate tables, which live with
        // the model files and may still be downloading.
        if (!DiffusionWorldSettings.EnabledForWorld(_api))
        {
            _api.Logger.Notification("[{0}] Disabled for this world; vanilla terrain generation is unchanged.",
                DiffusionPaths.ModId);
            return;
        }

        // Blocks until the assets are on disk, downloading them if the directory was emptied or
        // never populated. The metre-to-block mapping hangs off the native resolution it brings.
        PipelineModels models = LoadModels();

        try
        {
            _settings = DiffusionWorldSettings.FromWorld(_api, WorldPipelineModelConfig.Instance.NativeResolution);
            _settings.ApplyLowlandDetail(ResolveLowlandDetail());
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger,
                "This world's Terrain Diffusion settings could not be read.", e);
        }

        _provider?.Dispose();
        // Before the provider, because the first coarse window is built during the spawn search and
        // a window generated without the rivers would be cached and then seam against every one
        // after it.
        bool rivers = !WatershedsCompat.IsPresent(_api) && RiversCompat.IsPresent(_api);
        if (rivers) RiversCompat.TryInstall(_api);

        _riverBasins = rivers && RiversCompat.CanSampleNetwork
            ? new RiverBasinMap(_settings, _api.Logger)
            : null;

        _provider = new TerrainDiffusionProvider(
            WorldSeed(), models, _settings, _api.Logger, BuildLandmask(),
            _riverBasins, DiffusionConfig.Instance.WorldGen.RiverBasinDepth);

        // The spawn search can run before the height mapping is settled - and it should, because
        // the survey wants to be centred on where people will actually play.
        TerrainDiffusionProvider.SpawnCandidate? spawn = FindSpawn();

        // Must happen before anything asks for a tile: it decides the metre-to-block mapping every
        // surface height is computed from.
        CalibrateTerrainHeight(spawn);

        StartDebugMap();

        // Order terrain generation by how far it is from somebody. After a translocator hop the
        // queue is full of tiles for where the player used to be, and without this the ground they
        // are standing on is generated only once all of that has drained.
        _provider.TileUrgency = DistanceToNearestPlayer;

        _generator = new GenDiffusionTerra(_api, _provider, _settings);
        _surface = new DiffusionSurface(_api, _provider);

        InstallTerrain();
        InstallMapLayers();

        // Unrelated to the climate patches below: this one only makes the static translocator
        // search affordable, and never changes what the world contains.
        TranslocatorSearchCompat.Install(_api);

        // Both only while the model owns the climate map. Left to vanilla the stored byte is a
        // sea-level temperature already read at the game's own lapse rate, and correcting either
        // would make it wrong. After calibration, because the lapse correction follows from how
        // tall a block is, and before any chunk generates, because the map layer writes through it.
        if (_settings.ClimateMode != DiffusionClimateMode.Off)
        {
            ClimateScale.Install(_api.Logger, ClimateScale.ScaleFor(_settings.MeanMetersPerBlockVertical));
            SurfaceClimateCompat.Install(_api);
        }
        else
        {
            ClimateScale.Uninstall();
            SurfaceClimateCompat.Uninstall();
        }

        if (DiffusionConfig.Instance.WorldGen.RescaleBlockLayerAltitudes && !_settings.IsIsotropic)
        {
            try
            {
                BlockLayerAltitude.Apply(_api, _settings);
            }
            catch (Exception e)
            {
                throw DiffusionFailure.Fatal(_api.Logger,
                    "The surface block layer altitudes could not be rescaled for this world's " +
                    "vertical exaggeration.", e);
            }
        }

        RecordSpawn(spawn);

        _api.Logger.Notification("[{0}] Active: {1}", DiffusionPaths.ModId, _settings.Describe());
        WarnAboutWorldHeight();
    }

    /// <summary>
    /// Brings up the debug map if a port is configured. Deliberately after height calibration: the
    /// probes it runs are generated against a vertical scale that calibration is still in the
    /// middle of choosing, so recording them would put tiles on the map whose surface heights do
    /// not mean the same thing as every tile after them.
    /// </summary>
    private void StartDebugMap()
    {
        _debugMap?.Dispose();
        _debugMap = null;

        int port = DiffusionConfig.Instance.DebugMapPort;
        if (port == 0) return;

        var server = new DebugMapServer(_api.Logger, _provider, _settings, _riverBasins, PlayerMarkers);
        if (server.Start(DiffusionConfig.Instance.DebugMapBindAddress, port)) _debugMap = server;
        else server.Dispose();
    }

    /// <summary>Where everyone is, in world blocks, for the debug map's markers.</summary>
    private IReadOnlyList<(string Name, int X, int Z)> PlayerMarkers()
    {
        var markers = new List<(string, int, int)>();
        IPlayer[] players = _api?.World?.AllOnlinePlayers;
        if (players == null) return markers;

        foreach (IPlayer player in players)
        {
            Entity entity = player?.Entity;
            if (entity == null) continue;
            markers.Add((player.PlayerName ?? "?", (int)entity.Pos.X, (int)entity.Pos.Z));
        }

        return markers;
    }

    /// <summary>
    /// Blocks from a world position to the nearest player, or 0 when nobody is connected - during
    /// world creation everything is equally urgent. Squared distance would overflow at map scale,
    /// so this is a plain Chebyshev-ish sum that is monotone in what matters.
    /// </summary>
    private long DistanceToNearestPlayer(int blockX, int blockZ)
    {
        IPlayer[] players = _api?.World?.AllOnlinePlayers;
        if (players == null || players.Length == 0) return 0;

        long best = long.MaxValue;
        foreach (IPlayer player in players)
        {
            Entity entity = player?.Entity;
            if (entity == null) continue;

            long dx = Math.Abs((long)entity.Pos.X - blockX);
            long dz = Math.Abs((long)entity.Pos.Z - blockZ);
            long distance = dx > dz ? dx : dz;
            if (distance < best) best = distance;
        }

        return best == long.MaxValue ? 0 : best;
    }

    /// <summary>
    /// The landmask the model is conditioned on, or null when the model is to decide the coastline
    /// itself. Built before the first tile, because every tile depends on it.
    /// </summary>
    private ILandmaskSource BuildLandmask()
    {
        if (DiffusionConfig.Instance.WorldGen.OceanMap != "input") return null;
        return new OceanMapLandmask(
            () => WorldMapLayers.Resolve(_api)?.Ocean, _settings, _api.Logger);
    }

    /// <summary>Loads the ONNX models. Stops the game rather than returning without them.</summary>
    private PipelineModels LoadModels()
    {
        try
        {
            // The download itself announces its own progress from the loading thread; this is only
            // the point at which world generation actually has to stop and wait for it.
            if (!PipelineModels.IsReady)
            {
                _api.Logger.Notification("[{0}] Waiting for the models before generating terrain.",
                    DiffusionPaths.ModId);
            }
            return PipelineModels.Await();
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger,
                "The models could not be loaded.", e);
        }
    }

    private void WarnAboutWorldHeight()
    {
        // A temperate 10 C place is the useful yardstick: colder ground has scale to spare and
        // hotter ground is rarely high.
        float temperatureCeiling = _settings.TemperatureCeilingMeters(10f);
        if (temperatureCeiling < _settings.LinearRangeMeters)
        {
            _api.Logger.Warning(
                "[{0}] Vintage Story's one-byte climate map tops out at 40 C of sea-level temperature, " +
                "so temperate ground above about {1:0} m reads colder than the model intended. Only the " +
                "very highest peaks reach that.",
                DiffusionPaths.ModId, temperatureCeiling);
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
    /// <summary>What the model was told about rivers, kept so the debug map can show it.</summary>
    private IRiverBasinSource _riverBasins;

    private void OnMapRegionGeneration(IMapRegion mapRegion, int regionX, int regionZ, ITreeAttribute chunkGenParams)
    {
        if (_provider == null || _settings is not { Enabled: true }) return;
        if (_settings.ClimateMode == DiffusionClimateMode.Off) return;

        // A chunk peek that lands on virgin ground makes a whole map region to throw away with the
        // rest of the peek, and this walks a 512x512 block region through the model - four to nine
        // terrain tiles, against the one to four the peek's own 96-block footprint needs. It is the
        // largest single cost in a translocator search.
        //
        // Nothing in the Terrain or TerrainFeatures pass reads seasonality: it is map region mod
        // data, read at play time by DiffusionSeasons and the /tdiff readout. The peeked region is
        // discarded, and if the search does pick this column the region is generated again for
        // real, with the scope clear, and gets its map then.
        if (ChunkPeekScope.Active)
        {
            TranslocatorSearchCompat.CountRegionMapSkipped();
            return;
        }

        try
        {
            SeasonalityMap.Generate(mapRegion, regionX, regionZ, _api.WorldManager.RegionSize, _provider);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger,
                $"The seasonality map for region ({regionX}, {regionZ}) could not be written.", e);
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
            throw DiffusionFailure.Fatal(_api.Logger,
                $"The surface pass failed for chunk ({request.ChunkX}, {request.ChunkZ}).", e);
        }
    }

    /// <summary>Save game key holding the world's lowland detail, the ratio of its top and waterline block heights.</summary>
    private const string LowlandDetailSaveKey = "vsterraindiffusion:lowlanddetail";

    /// <summary>
    /// The lowland detail this world was created with. A new world takes the config's value and
    /// keeps it; a world from before the setting existed was generated at a uniform scale and stays
    /// at one. Either way it is written down, because the scale decides the height of every block
    /// and a world whose new chunks used a different one would have a step at every old border.
    /// </summary>
    private float ResolveLowlandDetail()
    {
        ISaveGame save = _api.WorldManager.SaveGame;
        try
        {
            byte[] stored = save.GetData(LowlandDetailSaveKey);
            if (stored is { Length: sizeof(float) }) return BitConverter.ToSingle(stored, 0);

            float detail = save.IsNew ? DiffusionConfig.Instance.WorldGen.LowlandDetail : 1f;
            save.StoreData(LowlandDetailSaveKey, BitConverter.GetBytes(detail));
            if (!save.IsNew && DiffusionConfig.Instance.WorldGen.LowlandDetail != 1f)
            {
                _api.Logger.Notification(
                    "[{0}] This world was generated before lowland detail existed, so it keeps a uniform " +
                    "vertical scale; lowlandDetail applies to worlds created from now on.", DiffusionPaths.ModId);
            }
            return detail;
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger, "This world's vertical scale could not be read or saved.", e);
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
    private void CalibrateTerrainHeight(TerrainDiffusionProvider.SpawnCandidate? center)
    {
        if (!_settings.WantsCalibration) return;

        byte[] stored = null;
        try
        {
            stored = _api.WorldManager.SaveGame.GetData(CalibrationSaveKey);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger,
                "This world's stored terrain height calibration could not be read.", e);
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
                center?.BlockX ?? _settings.OriginBlockX, center?.BlockZ ?? _settings.OriginBlockZ);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger,
                "Terrain height calibration failed.", e);
        }

        if (peak == null) return;

        _settings.ApplyCalibration(peak.Value);

        // The spawn search above may already have built a tile, and a tile stores block heights
        // rather than metres. Calibration has just changed what a metre is worth, so anything
        // generated before now describes a different landscape from everything after it.
        _provider.InvalidateTiles();

        try
        {
            _api.WorldManager.SaveGame.StoreData(CalibrationSaveKey, BitConverter.GetBytes(peak.Value));
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger,
                "The terrain height calibration could not be saved to this world.", e);
        }
    }

    private ulong WorldSeed() => (ulong)(uint)_api.WorldManager.Seed;

    /// <summary>
    /// Gets the model's heights into the world, whichever mod is generating terrain.
    ///
    /// Normally that is vanilla and this mod takes its place. When another mod has replaced terrain
    /// generation outright there is nowhere to stand: two generators filling the same column
    /// produce the union of both landscapes with only one mod's heightmaps recorded, which leaves
    /// the surface block layers buried under the other mod's rock. So the only supported
    /// arrangement with such a mod is to hand it our heights and let it do the filling.
    ///
    /// Stops the game when that could not be arranged. Standing aside would leave the world's
    /// existing chunks modelled and everything after them not.
    /// </summary>
    private void InstallTerrain()
    {
        if (WatershedsCompat.IsPresent(_api))
        {
            InstallWatershedsHandover();
            return;
        }

        InstallTerrainHandler();
    }

    /// <summary>
    /// Hands the diffusion heightmap to Algernon's Watersheds and leaves the filling, the rivers
    /// and everything downstream of them to it. See <see cref="WatershedsCompat"/> for how.
    /// </summary>
    private void InstallWatershedsHandover()
    {
        if (WatershedsCompat.TryInstall(_api, _provider, out string failure))
        {
            _api.Logger.Notification(
                "[{0}] Algernon's Watersheds is generating this world's terrain, so the model is " +
                "supplying its heights instead of filling chunks itself. Its rivers and streams are " +
                "routed over the modelled landscape.", DiffusionPaths.ModId);
            return;
        }

        throw DiffusionFailure.Fatal(_api.Logger,
            "Algernon's Watersheds also generates terrain, and " + failure +
            ". Remove or update one of the two mods.");
    }

    /// <summary>
    /// Replaces vanilla GenTerra's Terrain-pass delegate in place, so the ordering relative to
    /// rock strata, caves and block layers is exactly what those systems expect.
    /// </summary>
    private void InstallTerrainHandler()
    {
        IWorldGenHandler handlers = _api.Event.GetRegisteredWorldGenHandlers("standard");
        List<ChunkColumnGenerationDelegate> terrainPass = handlers.OnChunkColumnGen[(int)EnumWorldGenPass.Terrain];

        // Rivers leaves its own generator registered even when asked to stand aside, and two
        // generators filling one column produce the union of both landscapes. Before anything else,
        // so the vanilla slot below is the only one left to take.
        if (RiversCompat.Installed) RiversCompat.RemoveTerrainHandler(_api, terrainPass);

        ChunkColumnGenerationDelegate replacement = OnTerrainPass;

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
            // Something else already took vanilla GenTerra's place. Running as well as it would fill
            // every column twice, with the union of two landscapes and only one set of heightmaps.
            throw DiffusionFailure.Fatal(_api.Logger,
                "Another mod has already replaced vanilla GenTerra, so two generators would fill " +
                "the same columns. Remove one of them.");
        }

        _installedHandler = replacement;
    }

    /// <summary>
    /// The Terrain pass itself. Wrapped because the server catches whatever a generation handler
    /// throws and simply moves on to the next one, which would leave this column part filled by the
    /// model and part filled by whatever comes after it.
    /// </summary>
    private void OnTerrainPass(IChunkColumnGenerateRequest request)
    {
        try
        {
            _generator.OnChunkColumnGen(request);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger,
                $"Chunk column ({request.ChunkX}, {request.ChunkZ}) could not be generated.", e);
        }
    }

    /// <summary>
    /// Points GenMaps at model-backed climate, vegetation and ocean layers. All are plain map
    /// layers, so the rest of world generation keeps reading them the way it always has.
    /// </summary>
    private void InstallMapLayers()
    {
        WorldMapLayers genMaps = WorldMapLayers.Resolve(_api);
        if (genMaps == null)
        {
            throw DiffusionFailure.Fatal(_api.Logger,
                "GenMaps is not loaded, so the model's climate and ocean maps cannot be installed.");
        }

        if (!genMaps.IsVanilla)
        {
            _api.Logger.Notification("[{0}] The world's map layers belong to {1}; reading and writing those.",
                DiffusionPaths.ModId, genMaps.OwnerName);
        }

        // In "input" mode the ocean map is what the terrain was conditioned on, so it is already
        // the coastline that got generated and overwriting it would throw away the very settings -
        // and the very other mod's map - the terrain was built to honour. Only the reverse mode,
        // where the model invented the continents, has anything to correct.
        if (DiffusionConfig.Instance.WorldGen.OceanMap == "output")
        {
            genMaps.Ocean = new DiffusionOceanMapLayer(_api.WorldManager.Seed + 1873, _provider);
            _api.Logger.Notification(
                "[{0}] The model decides the coastline; the world's ocean map has been replaced to match it.",
                DiffusionPaths.ModId);
        }

        if (_settings.ClimateMode == DiffusionClimateMode.Off)
        {
            _api.Logger.Notification("[{0}] Model climate disabled for this world; using vanilla climate maps.",
                DiffusionPaths.ModId);
            return;
        }

        // Wrap the vanilla climate layer rather than replace it: its geologic activity byte has
        // nothing to do with climate and is still wanted. On re-initialisation GenMaps rebuilds
        // climateGen, but unwrap defensively anyway.
        MapLayerBase vanillaClimate = genMaps.Climate is DiffusionClimateMapLayer alreadyWrapped
            ? alreadyWrapped.Baseline
            : genMaps.Climate;

        if (vanillaClimate == null)
        {
            throw DiffusionFailure.Fatal(_api.Logger,
                "The world has no climate map layer to build the model's climate on.");
        }

        genMaps.Climate = new DiffusionClimateMapLayer(
            _api.WorldManager.Seed + 1, vanillaClimate, _provider, _settings);

        if (!_settings.Climate.IsNeutral)
        {
            // Worth saying, because it is not what these settings do in vanilla: they are part of
            // the world the model draws rather than a scaling of the numbers it produced.
            _api.Logger.Notification(
                "[{0}] The model is drawing a world {1}.{2}", DiffusionPaths.ModId, _settings.Climate,
                IsCorrected(_settings)
                    ? $" What it cannot reach is scaled onto its output afterwards " +
                      $"({_settings.TemperatureCorrection:0.##}x temperature, " +
                      $"{_settings.RainfallCorrection:0.##}x rainfall)."
                    : string.Empty);
        }

        _api.Logger.Notification(
            _settings.Latitude.IsNeutral
                ? $"[{DiffusionPaths.ModId}] Latitude bands {_settings.Latitude.Status}; the model's climate " +
                  "keeps its cold places wherever it drew them."
                : $"[{DiffusionPaths.ModId}] Latitude bands {_settings.Latitude.Status}. Heading north or " +
                  "south now changes the climate, and the equator sits where the world's starting " +
                  "climate put it.");

        WorldGenConfig worldGen = DiffusionConfig.Instance.WorldGen;
        genMaps.Forest = new DiffusionForestMapLayer(
            _api.WorldManager.Seed + 2, _provider, TerraGenConfig.forestMapScale, false,
            worldGen.ForestDensityMultiplier);
        genMaps.Bush = new DiffusionForestMapLayer(
            _api.WorldManager.Seed + 3, _provider, TerraGenConfig.shrubMapScale, true,
            worldGen.ShrubDensityMultiplier);
    }

    /// <summary>True when some of the world's climate settings had to be left to the output.</summary>
    private static bool IsCorrected(DiffusionWorldSettings settings)
        => Math.Abs(settings.TemperatureCorrection - 1f) > 0.01f
           || Math.Abs(settings.RainfallCorrection - 1f) > 0.01f;

    /// <summary>
    /// Finds somewhere to wake up. Vanilla guarantees land at the map centre by forcing its ocean
    /// map, and conditioning the terrain on that map carries the guarantee through, so this
    /// usually only has to move the spawn far enough to satisfy the starting climate - and in
    /// "output" mode, where the model decides the coastline itself, far enough to find land at all.
    ///
    /// It runs on every start, not just new saves, so that an existing world's height survey stays
    /// centred where it always was.
    /// </summary>
    private TerrainDiffusionProvider.SpawnCandidate? FindSpawn()
    {
        try
        {
            TerrainDiffusionProvider.SpawnCandidate? land = _provider.FindSpawn();
            if (land == null)
            {
                _api.Logger.Warning(
                    "[{0}] No land found near the world centre; the spawn point was left where it was. " +
                    "Try a different seed if you start in the ocean.", DiffusionPaths.ModId);
                return null;
            }

            ReportStartingClimate(land.Value);
            return land;
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger,
                "The spawn search failed.", e);
        }
    }

    /// <summary>
    /// Says whether the world's starting climate was honoured. A miss is worth a warning rather
    /// than silence: the player picked a climate and is not getting it, and the reason - no land
    /// that cold or that hot anywhere within reach of this seed's map centre - is not something
    /// they could work out from where they wake up.
    /// </summary>
    private void ReportStartingClimate(TerrainDiffusionProvider.SpawnCandidate spawn)
    {
        StartingClimate? band = _settings.StartingClimate;
        if (band == null) return;

        int distance = (int)Math.Sqrt(
            Math.Pow(spawn.BlockX - _settings.OriginBlockX, 2) + Math.Pow(spawn.BlockZ - _settings.OriginBlockZ, 2));

        if (spawn.MatchedClimate)
        {
            _api.Logger.Notification(
                "[{0}] Starting climate {1}: spawning at about {2:0.#} C, {3} blocks from the map centre.",
                DiffusionPaths.ModId, band.Value, spawn.TemperatureC, distance);
            return;
        }

        _api.Logger.Warning(
            "[{0}] No {1} land within range of the map centre on this seed; spawning at about {2:0.#} C instead, " +
            "{3} blocks out. Raise worldGen.startingClimateSearchRadiusBlocks to look further, or try another seed.",
            DiffusionPaths.ModId, band.Value, spawn.TemperatureC, distance);
    }

    /// <summary>
    /// Turns the spawn column found earlier into a position to apply once the save game's spawn
    /// record exists. Only for brand new saves, so an existing world never has its spawn moved.
    /// </summary>
    private void RecordSpawn(TerrainDiffusionProvider.SpawnCandidate? land)
    {
        if (land == null || !_api.WorldManager.SaveGame.IsNew) return;

        try
        {
            int blockX = land.Value.BlockX, blockZ = land.Value.BlockZ;
            TerrainTile tile = _provider.GetTileAt(blockX, blockZ);
            int index = tile.Index(blockX - tile.BlockX, blockZ - tile.BlockZ);
            int y = Math.Min(_settings.MapSizeY - 2, tile.SurfaceY[index] + 1);

            _pendingSpawn = (blockX, y, blockZ);
            _api.Logger.Notification("[{0}] Found a land spawn at ({1}, {2}, {3}), {4} m above sea level.",
                DiffusionPaths.ModId, blockX, y, blockZ, (int)tile.ElevationMeters[index]);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger, "The world spawn could not be placed on land.", e);
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
            throw DiffusionFailure.Fatal(_api.Logger, "The world spawn could not be moved to land.", e);
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
            .BeginSubCommand("gpulimit")
                .WithDescription("Show or set the share of the time inference may keep the device busy")
                .WithArgs(api.ChatCommands.Parsers.OptionalInt("percent"))
                .HandleWith(OnGpuLimitCommand)
            .EndSubCommand()
            .BeginSubCommand("map")
                .WithDescription("Show the address of the debug map, if it is running")
                .HandleWith(OnMapCommand)
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
            .BeginSubCommand("rivers")
                .WithDescription("Report the Rivers network around a position, for working out why rivers come up short")
                .WithArgs(api.ChatCommands.Parsers.OptionalInt("x"), api.ChatCommands.Parsers.OptionalInt("z"))
                .HandleWith(OnRiversCommand)
            .EndSubCommand()
            .BeginSubCommand("column")
                .WithDescription("Read back the generated block column at a position, for diagnosing world generation")
                .WithArgs(api.ChatCommands.Parsers.Int("x"), api.ChatCommands.Parsers.Int("z"))
                .HandleWith(OnColumnCommand)
            .EndSubCommand();
    }

    private Vintagestory.API.Common.TextCommandResult OnRiversCommand(
        Vintagestory.API.Common.TextCommandCallingArgs args)
    {
        if (!RiversCompat.Installed)
        {
            return Vintagestory.API.Common.TextCommandResult.Success(
                "Rivers is not generating this world's rivers.");
        }

        // Defaults to where the caller is standing, which is the usual thing to ask about.
        int x, z;
        if (args.Parsers[0].IsMissing || args.Parsers[1].IsMissing)
        {
            Entity caller = args.Caller?.Entity;
            if (caller == null)
            {
                return Vintagestory.API.Common.TextCommandResult.Error(
                    "Give an x and z, or run this as a player.");
            }
            x = (int)caller.Pos.X;
            z = (int)caller.Pos.Z;
        }
        else
        {
            x = (int)args[0];
            z = (int)args[1];
        }

        return Vintagestory.API.Common.TextCommandResult.Success(RiversCompat.DescribeRegion(x, z));
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

        var lines = new List<string>
        {
            "Terrain Diffusion active",
            "",
            $"Requested device: {DiffusionConfig.Instance.InferenceDevice}",
            $"Model precision: coarse {DiffusionConfig.Instance.CoarsePrecision}, " +
            $"base {DiffusionConfig.Instance.BasePrecision}, decoder {DiffusionConfig.Instance.DecoderPrecision}",
            $"Runtime: {OnnxRuntimeBootstrap.ActiveRuntimeDescription}",
            $"Provider: {OnnxModel.ActiveProvider}",
            $"Model resolution: {WorldPipelineModelConfig.Instance.NativeResolution:0.##} m per pixel",
            $"Models resident: {(DiffusionConfig.Instance.OffloadModels ? "no, one at a time (offloadModels)" : "yes")}",
            $"Latent batch: {_provider.LatentBatchSize}",
            $"Pipeline cache: {_provider.PipelineCachedBytes / 1048576.0:0.#} / " +
            $"{DiffusionConfig.Instance.TileCacheMegabytes} MB",
            $"Pipeline windows computed: {_provider.PipelineComputedWindows}",
            $"Translocator search: {TranslocatorSearchCompat.Describe()}",
            $"Tiles dropped for closer work: {_provider.PreemptionCount}",
            $"Device limit: {InferenceThrottle.Describe()}",
            $"Settings screen: {(ConfigLibCompat.IsPresent(_api) ? "ConfigLib" : "not installed")}",
            $"Debug map: {(_debugMap?.IsRunning == true ? _debugMap.Url : "off")}",
            $"Tiles generated: {_provider.TilesGenerated}",
            $"Tile size: {_provider.TileSize}x{_provider.TileSize} blocks",
            $"Average tile time: {_provider.AverageTileMillis} ms",
            $"Model inference: {_provider.ModelTimingSummary}",
            $"Inference share of tile time: {_provider.InferenceSharePercent}%",
            "",
            $"Horizontal scale: {_settings.MetersPerBlock:0.##} m per block (scale {_settings.Scale})",
            $"Vertical scale: {_settings.DescribeHeight()}",
            $"Sea level: Y {_settings.SeaLevel}",
            $"World height: {_settings.MapSizeY}",
            $"Headroom: {_settings.HeadroomBlocks} blocks above sea level",
            $"Linear up to: {_settings.LinearRangeMeters:0} m of elevation",
            $"Climate: {_settings.ClimateMode.ToString().ToLowerInvariant()}"
        };

        if (!_settings.Climate.IsNeutral) lines.Add($"Global climate: {_settings.Climate}");
        lines.Add($"Latitude bands: {_settings.Latitude.Status}");

        return Vintagestory.API.Common.TextCommandResult.Success(string.Join("\n", lines));
    }

    private Vintagestory.API.Common.TextCommandResult OnGpuLimitCommand(Vintagestory.API.Common.TextCommandCallingArgs args)
    {
        // An omitted OptionalInt reads back as 0 rather than null, which would clamp to the lowest
        // setting instead of printing the current one. ArgCount is no help either: it is how many
        // tokens the parsers consume, not how many were given.
        if (args.Parsers[0].IsMissing)
        {
            return Vintagestory.API.Common.TextCommandResult.Success(
                $"Inference is limited to {InferenceThrottle.Describe()}. " +
                "Pass a percentage from 5 to 100 to change it.");
        }

        int requested = (int)args[0];
        InferenceThrottle.UtilizationPercent = requested;
        int applied = InferenceThrottle.UtilizationPercent;
        DiffusionConfig.Instance.GpuUtilizationPercent = applied;

        string persisted = PersistGpuLimit(applied)
            ? $"Saved to {DiffusionPaths.ModId}.json."
            : $"Could not write {DiffusionPaths.ModId}.json, so this lasts until the server restarts.";

        string effect = applied >= 100
            ? "World generation runs at full speed."
            : $"World generation will take roughly {100f / applied:0.#} times as long, " +
              "in exchange for the device being free between model runs.";

        return Vintagestory.API.Common.TextCommandResult.Success(
            $"Inference is now limited to {applied}% of the time. {effect} {persisted}");
    }

    private Vintagestory.API.Common.TextCommandResult OnMapCommand(Vintagestory.API.Common.TextCommandCallingArgs args)
    {
        if (_debugMap == null || !_debugMap.IsRunning)
        {
            return Vintagestory.API.Common.TextCommandResult.Success(
                $"The debug map is off. Set debugMapPort in {DiffusionPaths.ModId}.json (8088 is a reasonable " +
                "choice) and restart the server.");
        }

        return Vintagestory.API.Common.TextCommandResult.Success(
            $"Debug map: {_debugMap.Url}\n" +
            $"Remembering {_debugMap.TileCount} of the last {DiffusionConfig.Instance.DebugMapHistoryTiles} tiles.");
    }

    /// <summary>
    /// Writes just this one key back, rather than serialising the whole config. ConfigLib edits the
    /// same file when it is installed, and the in-memory config is not kept in step with its
    /// changes on purpose (see <see cref="ConfigLibCompat"/>), so rewriting the file wholesale here
    /// would quietly revert them.
    /// </summary>
    private bool PersistGpuLimit(int percent)
    {
        try
        {
            string path = DiffusionPaths.ConfigFile;
            if (!System.IO.File.Exists(path))
            {
                _api.StoreModConfig(DiffusionConfig.Instance, DiffusionPaths.ModId + ".json");
                return true;
            }

            var root = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(path));
            root[nameof(DiffusionConfig.GpuUtilizationPercent)] = percent;
            System.IO.File.WriteAllText(path, root.ToString(Newtonsoft.Json.Formatting.Indented));
            return true;
        }
        catch (Exception e)
        {
            _api.Logger.Warning("[{0}] Could not save the GPU limit: {1}", DiffusionPaths.ModId, e.Message);
            return false;
        }
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

        return Vintagestory.API.Common.TextCommandResult.Success(string.Join("\n", lines));
    }

    /// <summary>
    /// Where a column sits between equator and pole, and which way round its year runs there. The
    /// hemisphere is the game's own, so the season named here is the one every other system will
    /// think it is.
    /// </summary>
    private IEnumerable<string> PlaceLines(Vintagestory.API.MathTools.BlockPos pos)
    {
        double latitude = _api.World.Calendar.OnGetLatitude(pos.Z);
        yield return $"Latitude: {Math.Abs(latitude) * 90.0:0.0} deg {(latitude > 0.0 ? "north" : "south")}";
        yield return $"Hemisphere: {(latitude > 0.0 ? "northern" : "southern")}, " +
                     $"currently {_api.World.Calendar.GetSeason(pos)}";
        yield return $"Latitude bands: {_settings?.Latitude.Status ?? "unknown"}";
    }

    /// <summary>
    /// The workings behind one column's temperature: the same reading with the altitude taken back
    /// out, and what the latitude band was aiming for.
    ///
    /// The two answer different questions and neither settles anything on its own. The band is a
    /// median over all the land in its belt, relief included, so the like-for-like figure is the
    /// surface reading — and one column scatters either side of it by several degrees, which is the
    /// point of having a model rather than a gradient. Sea level is the number to compare two
    /// places by, because it has the mountain taken out of it.
    ///
    /// Empty when the pipeline could not be re-queried for the workings.
    /// </summary>
    private IEnumerable<string> TemperatureLines(Vintagestory.API.MathTools.BlockPos pos)
    {
        if (_provider == null || _settings == null) yield break;

        TerrainDiffusionProvider.ColumnDetail? detail = _provider.GetColumnDetail(pos.X, pos.Z);
        if (detail != null)
        {
            // Through the same corrections the surface value went through, so the two compare. The
            // elevation behind it is the model pixel's own, which differs from the column's by the
            // upsampling and the slope noise, so it is not repeated here.
            float seaLevel = _settings.WorldTemperature(detail.Value.SeaLevelTemperatureC, pos.Z);
            yield return $"Sea-level temperature: {seaLevel:0.#} C " +
                         $"(lapse {detail.Value.LapseRateKPerKm:0.0} C/km)";
        }

        if (_settings.Latitude.IsNeutral) yield break;

        TerrainTile tile = _provider.GetTileAt(pos.X, pos.Z);
        float surface = tile.TemperatureC[tile.Index(pos.X - tile.BlockX, pos.Z - tile.BlockZ)];
        float band = _settings.Latitude.BandTemperatureC(pos.Z);
        float offset = _settings.Latitude.TemperatureOffsetC(pos.Z);

        yield return $"Band temperature: {band:0.#} C (median over this belt's land; " +
                     $"this column is {surface - band:+0.#;-0.#} C)";
        if (Math.Abs(offset) >= 0.05f)
        {
            yield return $"Band offset applied: {offset:+0.#;-0.#} C, added after the model ran " +
                         "because the conditioning could not reach it";
        }
    }

    /// <summary>What the latitude band asked for in rainfall, against what this column reads.</summary>
    private IEnumerable<string> PrecipitationLines(Vintagestory.API.MathTools.BlockPos pos, Bioclim climate)
    {
        if (_settings == null || _settings.Latitude.IsNeutral) yield break;

        float band = _settings.Latitude.BandPrecipitationMm(pos.Z);
        yield return $"Band precipitation: {band:0} mm (median over this belt's land; " +
                     $"this column is {climate.PrecipitationMm / Math.Max(1f, band):0.00}x)";
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
            $"Terrain Diffusion column ({x}, {z})",
            "",
            $"Terrain height: Y {terrainHeight}",
            $"Rain height: Y {rainHeight}",
            $"Sea level: Y {_api.World.SeaLevel}",
            "",
            "Blocks:"
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
            lines.Add("");
            lines.Add("What the model said:");
            lines.Add($"  Elevation: {tile.ElevationMeters[index]:0} m (Y {tile.SurfaceY[index]})");
            lines.Add($"  Temperature: {climate.MeanTemperatureC:0.#} C");
            lines.Add($"  Precipitation: {climate.PrecipitationMm:0} mm/year");
            lines.Add($"  Tree moisture: {climate.TreeMoisture:0.00}");
        }

        // What the game actually reads at the surface, after blending and its own altitude
        // corrections - this is the number that decides block layers and vegetation.
        var surfacePos = new Vintagestory.API.MathTools.BlockPos(x, rainHeight, z);
        Vintagestory.API.Common.ClimateCondition climate2 =
            _api.World.BlockAccessor.GetClimateAt(surfacePos, EnumGetClimateMode.WorldGenValues);
        if (climate2 != null)
        {
            lines.Add("");
            lines.Add("What the game reads at the surface:");
            lines.Add($"  Temperature: {climate2.Temperature:0.#} C");
            lines.Add($"  Rainfall: {climate2.Rainfall:0.##}");
            lines.Add($"  Fertility: {climate2.Fertility:0.##}");
            lines.Add($"  Forest: {climate2.ForestDensity:0.##}");
            lines.Add($"  Shrubs: {climate2.ShrubDensity:0.##}");
        }

        return Vintagestory.API.Common.TextCommandResult.Success(string.Join("\n", lines));
    }

    private void OnShutdown()
    {
        _debugMap?.Dispose();
        _debugMap = null;
        WatershedsCompat.Uninstall();
        RiversCompat.Uninstall();
        SurfaceClimateCompat.Uninstall();
        TranslocatorSearchCompat.Uninstall();
        ClimateScale.Uninstall();
        ConfigLibCompat.Uninstall();
        _provider?.Dispose();
        _provider = null;
        PipelineModels.Shutdown();
    }

    public override void Dispose()
    {
        _debugMap?.Dispose();
        _debugMap = null;
        SurfaceClimateCompat.Uninstall();
        ClimateScale.Uninstall();
        _provider?.Dispose();
        _provider = null;

        // Normally OnShutdown got here first. This is the backstop for a server that never reached
        // the shutdown run phase, where otherwise the models would stay resident with no owner.
        PipelineModels.Shutdown();
    }
}
