using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using TerrainDiffusion.Water;
using VSTerrainDiffusion.Commands;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Debug;
using VSTerrainDiffusion.Pipeline;
using VSTerrainDiffusion.WorldGen;

namespace VSTerrainDiffusion;

/// <summary>
/// Entry point. Swaps vanilla's terrain generator for the diffusion heightmap generator and points
/// the climate, forest and shrub maps at the same model, leaving every other world generation pass
/// alone. The ocean map goes the other way: it is read as conditioning for the model rather than
/// written, so the world's own land cover and ocean scale settings still decide where the sea is.
/// </summary>
/// <remarks>
/// This class only orders the lifecycle; each concern lives in its own type:
/// <list type="bullet">
/// <item><see cref="WorldValuePins"/> - per-world values pinned at creation</item>
/// <item><see cref="SpawnPlacement"/>, <see cref="HeightCalibration"/> - spawn search and height fit</item>
/// <item><see cref="TileFingerprint"/> - what empties the tile database</item>
/// <item><see cref="TerrainHandlerInstaller"/>, <see cref="MapLayerInstaller"/> - hooks into vanilla worldgen</item>
/// <item><see cref="PregenerationController"/> - the <c>/tdiff pregen</c> job</item>
/// <item><see cref="TerrainDiffusionCommands"/> - the <c>/tdiff</c> command</item>
/// </list>
/// </remarks>
public class TerrainDiffusionModSystem : ModSystem
{
    /// <summary>Where Terrain Continuity looks for a terrain mod's surface height at a block column.</summary>
    private const string ContinuityHeightSourceKey = "terraincontinuity:surfaceheight";

    /// <summary>The server, from <c>StartServerSide</c>.</summary>
    private ICoreServerAPI _api;

    /// <summary>The world's resolved settings; null before initialisation. Read by test probes by name.</summary>
    private DiffusionWorldSettings _settings;

    /// <summary>The world's terrain; null until the generator is ready. Read by test probes by name.</summary>
    private TerrainDiffusionProvider _provider;

    /// <summary>The generated climate published to other mods; see <see cref="Climate"/>.</summary>
    private Api.TerrainClimate _climate;

    /// <summary>Story structures' climates and landforms the model is asked for (experimental), or null.</summary>
    private List<CoarseConditioningSite> _conditioningSites;

    /// <summary>What the model was told about rivers, kept so the debug map can show it; null without Rivers.</summary>
    private IRiverBasinSource _riverBasins;

    /// <summary>The chunk column generator that writes the terrain.</summary>
    private GenDiffusionTerra _generator;

    /// <summary>Lakes and rivers, or null when the world does not fill water or Watersheds does.</summary>
    private WorldWater _water;

    /// <summary>The surface pass: bare rock on steep ground, permanent ice where it never thaws.</summary>
    private DiffusionSurface _surface;

    /// <summary>The live debug map, when its port is configured.</summary>
    private DebugMapServer _debugMap;

    /// <summary>The forest map layer that replaced vanilla's.</summary>
    private DiffusionForestMapLayer _forestLayer;

    /// <summary>The shrub map layer that replaced vanilla's.</summary>
    private DiffusionForestMapLayer _shrubLayer;

    /// <summary>Finds where the player starts.</summary>
    private SpawnPlacement _spawn;

    /// <summary>Puts the generator in vanilla's place in the terrain pass.</summary>
    private TerrainHandlerInstaller _terrainHandler;

    /// <summary>Runs <c>/tdiff pregen</c>.</summary>
    private PregenerationController _pregeneration;

    /// <summary>
    /// The generated climate for other mods (see <see cref="Api.TerrainClimate"/>): server side, and
    /// null until this world's generator is ready or on a world the mod is not generating.
    /// </summary>
    public Api.TerrainClimate Climate => _climate;

    /// <summary>The world's resolved settings, or null before the world generator initialises.</summary>
    internal DiffusionWorldSettings Settings => _settings;

    /// <summary>The world's terrain, or null when the mod is not generating this world.</summary>
    internal TerrainDiffusionProvider Provider => _provider;

    /// <summary>The world's lakes and rivers, or null when it has none.</summary>
    internal WorldWater Water => _water;

    /// <summary>The debug map server, or null when it is off.</summary>
    internal DebugMapServer DebugMap => _debugMap;

    /// <summary>The <c>/tdiff pregen</c> job controller.</summary>
    internal PregenerationController Pregeneration => _pregeneration;

    /// <summary>
    /// Runs after every vanilla world generation system has registered (GenTerra is 0.0,
    /// GenMaps and GenRockStrataNew are 0.1), so the handler lists are complete when we edit them.
    /// </summary>
    public override double ExecuteOrder() => 0.5;

    /// <summary>Server only: clients receive finished chunks.</summary>
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

        DiffusionConfig.Load(api);
        InferenceThrottle.UtilizationPercent = InferenceSettings.Load((ICoreServerAPI)api).GpuUtilizationPercent;
        WorldValuePins.Pin((ICoreServerAPI)api);
    }

    /// <summary>Registers the world generation hooks, run phase hooks and commands, and starts loading the models.</summary>
    public override void StartServerSide(ICoreServerAPI api)
    {
        _api = api;
        _spawn = new SpawnPlacement(api);
        _terrainHandler = new TerrainHandlerInstaller(api);
        _pregeneration = new PregenerationController(api, () => _water);
        DiffusionFailure.UseLogger(api.Logger);
        ConfigLibCompat.Install(api);

        api.Event.InitWorldGenerator(OnInitWorldGenerator, "standard");

        // Before any world generator's set-up, one of which loads the tree bands it moves.
        TreeHeightFrame.Install();
        api.Event.MapRegionGeneration(OnMapRegionGeneration, "standard");

        // After GenBlockLayers, which is registered on the same pass at execute order 0.4.
        api.Event.ChunkColumnGeneration(OnSurfacePass, EnumWorldGenPass.TerrainFeatures, "standard");

        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, _spawn.ApplyPending);
        api.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, OnShutdown);

        new TerrainDiffusionCommands(api, this).Register();

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
            throw DiffusionFailure.Fatal(_api.Logger, "World generation could not be initialised.", e);
        }
    }

    /// <summary>
    /// Builds everything this world's generation needs, in dependency order, and hooks it into
    /// vanilla's world generation. Runs once per world load and again on <c>/wgen regen</c>.
    /// </summary>
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
        _settings = ReadSettings();

        ReleaseWorldState();
        SetUpRivers();

        ILandmaskSource landmask = BuildLandmask();
        _conditioningSites = DiffusionConfig.Instance.WorldGen.StructureConditioning
            ? StoryStructureConditioning.Read(_api, _settings, _api.Logger)
            : null;
        _provider = new TerrainDiffusionProvider(
            WorldSeed(), models, _settings, _api.Logger, landmask,
            _riverBasins, _settings.RiverBasinDepth, StoredTileSize(), _conditioningSites);
        _climate = new Api.TerrainClimate(_provider);

        // Before anything builds a Rivers region, which the spawn search below is the first to do.
        if (RiversCompat.Installed) RiversCompat.UseModelSea(_provider.IsCoarseSea);
        TerrainSamplerCompat.Install(_api, _provider);

        TerrainDiffusionProvider.SpawnCandidate? spawn = SearchAndCalibrate(landmask);

        StartDebugMap();
        PublishToOtherMods();

        // Order terrain generation by how far it is from somebody. After a translocator hop the
        // queue is full of tiles for where the player used to be, and without this the ground they
        // are standing on is generated only once all of that has drained.
        _provider.TileUrgency = (x, z) => PlayerProximity.Urgency(_api?.World, x, z);

        _water = CreateWater();
        _generator = new GenDiffusionTerra(_api, _provider, _settings, _water);
        _surface = new DiffusionSurface(_api, _provider);

        _terrainHandler.Install(OnTerrainPass, _provider);
        _forestLayer = _shrubLayer = null;
        (_forestLayer, _shrubLayer) = MapLayerInstaller.Install(_api, _provider, _settings);

        InstallCompatibilityPatches();
        InstallClimateCorrections();
        FitAltitudeBands();

        _spawn.Record(spawn, _provider, _settings, _water);

        _api.Logger.Notification("[{0}] Active: {1}", DiffusionPaths.ModId, _settings.Describe());
        WarnAboutWorldHeight();
        _pregeneration.Resume(_provider);
    }

    /// <summary>The world's settings with its pinned shore detail applied. Stops the game if they cannot be read.</summary>
    private DiffusionWorldSettings ReadSettings()
    {
        try
        {
            DiffusionWorldSettings settings =
                DiffusionWorldSettings.FromWorld(_api, WorldPipelineModelConfig.Instance.NativeResolution);
            (float shoreDetail, float shoreFade) = WorldValuePins.ShoreDetail(_api.WorldManager.SaveGame.WorldConfiguration);
            settings.ApplyShoreDetail(shoreDetail, shoreFade);
            return settings;
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger,
                "This world's Terrain Diffusion settings could not be read.", e);
        }
    }

    /// <summary>Stops and drops what a previous initialisation built: the pregen job, the water and the terrain.</summary>
    private void ReleaseWorldState()
    {
        _pregeneration.Stop(forget: false);
        _water?.Dispose();
        _water = null;
        _provider?.Dispose();
    }

    /// <summary>
    /// Hooks into Rivers, when it is generating this world's rivers, and decides whether the model
    /// shapes valleys for them. Before the provider, because the first coarse window is built during
    /// the spawn search and a window generated without the rivers would be cached and then seam
    /// against every one after it.
    /// </summary>
    private void SetUpRivers()
    {
        bool rivers = !WatershedsCompat.IsPresent(_api) && RiversCompat.IsPresent(_api);
        if (rivers) RiversCompat.TryInstall(_api);

        // At a valley depth of 0 the basins would weigh nothing, and building them costs Rivers a
        // whole region for every 32 km a coarse window covers - minutes at high scales.
        _riverBasins = rivers && RiversCompat.CanSampleNetwork && _settings.RiverBasinDepth > 0f
            ? new RiverBasinMap(_settings, _api.Logger)
            : null;
        if (RiversCompat.Installed)
        {
            string shaping = _riverBasins != null
                ? "The model shapes valleys for the rivers, {1:0}% deep (terraindiffusionRiverValleyDepth)."
                : _settings.RiverBasinDepth > 0f
                    ? "This Rivers version does not expose its river network, so the model is not shaped around the rivers."
                    : "The model is not shaped around the rivers (terraindiffusionRiverValleyDepth {1:0}%).";
            _api.Logger.Notification("[{0}] " + shaping, DiffusionPaths.ModId, _settings.RiverBasinDepth * 100f);
        }
    }

    /// <summary>
    /// Runs the spawn search when it is useful, settles the height mapping, and attaches the tile
    /// database once block heights are final.
    /// </summary>
    /// <remarks>
    /// The spawn search can run before the height mapping is settled - and it should, because the
    /// survey wants to be centred on where people will actually play. Only a new world uses its result
    /// for the spawn, though, and an existing one only for a height calibration it has not stored yet.
    /// Otherwise it is minutes of coarse surveying on every load, for nothing.
    /// </remarks>
    /// <returns>The spawn column, or null when there was no search or it found no land.</returns>
    private TerrainDiffusionProvider.SpawnCandidate? SearchAndCalibrate(ILandmaskSource landmask)
    {
        // A stored tile keeps block heights, so the tile database waits for a height calibration;
        // without one the mapping is already settled and the spawn search can read from it.
        bool calibrates = _settings.WantsCalibration;
        if (!calibrates) AttachTileStore(landmask);

        bool searchSpawn = _api.WorldManager.SaveGame.IsNew || (calibrates && HeightCalibration.Stored(_api) == null);
        TerrainDiffusionProvider.SpawnCandidate? spawn = searchSpawn ? _spawn.Find(_provider, _settings) : null;
        if (!searchSpawn)
        {
            _api.Logger.VerboseDebug("[{0}] Existing world: its spawn is already placed, so no spawn search.",
                DiffusionPaths.ModId);
        }

        // Must happen before anything asks for a tile: it decides the metre-to-block mapping every
        // surface height is computed from.
        HeightCalibration.Apply(_api, _provider, _settings, spawn);
        if (calibrates) AttachTileStore(landmask);
        return spawn;
    }

    /// <summary>
    /// Publishes the built surface height for Terrain Continuity (a separate mod), which slopes new
    /// ground to meet chunks built by an older setup and corrects only real steps. A plain delegate,
    /// so neither mod needs the other.
    /// </summary>
    private void PublishToOtherMods()
    {
        _api.ObjectCache[ContinuityHeightSourceKey] =
            (System.Func<int, int, int>)((x, z) => _provider?.SampleSurfaceY(x, z, SurfaceSampleMode.Full) ?? 0);
    }

    /// <summary>
    /// Patches that make vanilla systems cheap or correct on modelled terrain. The translocator patch
    /// only makes the static translocator search affordable and never changes what the world contains.
    /// </summary>
    private void InstallCompatibilityPatches()
    {
        TranslocatorSearchCompat.Install(_api, _provider);
        StoryTeleportCompat.Install(_api, _provider);
        DryShallowsCompat.Install(_settings.DryShallowsFill, _api.Logger);
        if (!_settings.DryShallowsFill)
        {
            _api.Logger.Notification("[{0}] Vanilla's filling of dry-climate shallows is off: the model's water stays water.",
                DiffusionPaths.ModId);
        }
    }

    /// <summary>
    /// The altitude and surface climate corrections, only while the model owns the climate map. Left to
    /// vanilla the stored byte is a sea-level temperature already read at the game's own lapse rate, and
    /// correcting it would make it wrong. After calibration, because the lapse correction follows from
    /// how tall a block is, and before any chunk generates, because the map layer writes through it.
    /// </summary>
    private void InstallClimateCorrections()
    {
        if (_settings.ClimateMode != DiffusionClimateMode.Off)
        {
            ClimateScale.Install(_api.Logger,
                ClimateScale.ScaleFor(_settings.MeanMetersPerBlockVertical) * _settings.AltitudeCooling,
                ClimateScale.ReferenceLapseCPerKm * _settings.AltitudeCooling);
            SurfaceClimateCompat.Install(_api, _settings.Heights);
        }
        else
        {
            ClimateScale.Uninstall();
            SurfaceClimateCompat.Uninstall();
        }
    }

    /// <summary>
    /// Moves the block layer and tree altitude bands to this world's sea level and vertical scale.
    /// Always, even when there is nothing to move: the bands are shared by every world in the process,
    /// so one moved for the last world has to be put back for this one.
    /// </summary>
    private void FitAltitudeBands()
    {
        try
        {
            bool exaggerated = DiffusionConfig.Instance.WorldGen.RescaleBlockLayerAltitudes && !_settings.IsIsotropic;
            BlockLayerAltitude.Apply(_api, _settings, exaggerated);
            TreeHeightFrame.Use(_settings, exaggerated, _api.Logger);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger,
                "The altitude bands of surface layers and trees could not be fitted to this world's sea level " +
                "and vertical scale.", e);
        }
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

        var server = new DebugMapServer(_api.Logger, _provider, _settings, _riverBasins,
            () => PlayerProximity.Markers(_api?.World), () => (_forestLayer, _shrubLayer));
        if (server.Start(DiffusionConfig.Instance.DebugMapBindAddress, port)) _debugMap = server;
        else server.Dispose();
    }

    /// <summary>
    /// The landmask the model is conditioned on, or null when the model is to decide the coastline
    /// itself. Built before the first tile, because every tile depends on it.
    /// </summary>
    private ILandmaskSource BuildLandmask()
    {
        if (DiffusionConfig.Instance.WorldGen.OceanMap != "input") return null;
        int samples = WorldValuePins.OceanMapSamples(_api.WorldManager.SaveGame.WorldConfiguration);
        return new OceanMapLandmask(() => WorldMapLayers.Resolve(_api)?.Ocean, _settings, samples, _api.Logger);
    }

    /// <summary>Waits for the ONNX models. Stops the game rather than returning without them.</summary>
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
            throw DiffusionFailure.Fatal(_api.Logger, "The models could not be loaded.", e);
        }
    }

    /// <summary>The world seed as the pipeline takes it: the int's bits, zero-extended.</summary>
    private ulong WorldSeed() => (ulong)(uint)_api.WorldManager.Seed;

    /// <summary>
    /// The tile size this world's tile database holds, if it has one with tiles in it: the world keeps
    /// it whatever this machine would pick, so tiles pregenerated on a GPU (256 blocks) are still
    /// served on a CPU (128).
    /// </summary>
    private int? StoredTileSize() => InferenceSettings.Current.TileDatabase
        ? TerrainTileStore.ReadTileSize(DiffusionPaths.TileDatabaseFile(_api.WorldManager.SaveGame.SavegameIdentifier))
        : null;

    /// <summary>
    /// Opens the world's tile database when its <c>terraindiffusionTileDatabase</c> setting asks for
    /// one. A database that cannot be opened only costs speed, so the world carries on without it.
    /// </summary>
    private void AttachTileStore(ILandmaskSource landmask)
    {
        if (!InferenceSettings.Current.TileDatabase) return;

        string path = DiffusionPaths.TileDatabaseFile(_api.WorldManager.SaveGame.SavegameIdentifier);
        try
        {
            Dictionary<string, string> fingerprint = TileFingerprint.Build(_api, _settings, landmask, _conditioningSites);
            _provider.AttachStore(TerrainTileStore.Open(path, _provider.TileSize, fingerprint, _api.Logger));
        }
        catch (Exception e)
        {
            _api.Logger.Warning("[{0}] The tile database {1} could not be opened, so tiles will not be kept: {2}",
                DiffusionPaths.ModId, path, e.Message);
        }
    }

    /// <summary>Warns when the world's height or the climate map's range will flatten the model's terrain or climate.</summary>
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
    /// Lakes and rivers for this world, or null when it has them off or another mod fills the terrain.
    /// </summary>
    private WorldWater CreateWater()
    {
        if (!_settings.WaterFill)
        {
            _api.Logger.Notification("[{0}] Lakes and rivers are off for this world: depressions above sea level stay dry.",
                DiffusionPaths.ModId);
            return null;
        }

        if (WatershedsCompat.IsPresent(_api))
        {
            _api.Logger.Notification("[{0}] Lakes and rivers are left to Algernon's Watersheds, which fills this world's terrain.",
                DiffusionPaths.ModId);
            return null;
        }

        if (_settings.WaterVersion != WorldValuePins.CurrentWaterVersion)
        {
            _api.Logger.Warning(
                "[{0}] This world's water was made with water version {1}, and this version of the mod makes version {2}: " +
                "lakes and rivers may not meet at the edge of the explored ground.",
                DiffusionPaths.ModId, _settings.WaterVersion, WorldValuePins.CurrentWaterVersion);
        }

        // Two river networks would cross each other: with Rivers installed it keeps its own.
        bool rivers = !RiversCompat.IsPresent(_api);
        if (!rivers && _settings.RiverMode != RiverMode.Off)
        {
            _api.Logger.Notification("[{0}] Rivers is installed, so this mod adds lakes but no rivers of its own.",
                DiffusionPaths.ModId);
        }

        // Nobody online means nobody needs anything kept.
        var water = new WorldWater(_provider, _settings, (uint)_api.WorldManager.Seed, rivers,
            (x, z) => PlayerProximity.NearestPlayerDistance(_api?.World, x, z), _api.Logger);
        LogWater(water);
        return water;
    }

    /// <summary>Logs the water settings this world runs with.</summary>
    private void LogWater(WorldWater water)
    {
        RiverGraphSettings graph = water.Settings;
        MeanderSettings meander = graph.Meander;
        _api.Logger.Notification(
            "[{0}] Lakes and rivers: lakes {8} block(s) below their spill, wetland above; rivers {1} from {9:0}% of peak discharge, width x{2:0.#}; meanders {3}; " +
            "{4} m per cell, discharge cap {5}, fill up to {6}x{6} tiles, {7} thread(s).",
            DiffusionPaths.ModId, water.RiverMode.ToString().ToLowerInvariant(), _settings.RiverWidth,
            meander == null
                ? "off"
                : string.Format(CultureInfo.InvariantCulture,
                    "sinuosity {0:0.##}, wavelength {1:0} m, slope {2:0.####}, relief {3:0.#} m, confinement {4:0} m, free relief x{5:0.#}",
                    meander.MaxSinuosity, meander.WavelengthMetres, meander.SlopeScale, meander.ReliefScaleMetres,
                    meander.ConfinementMetres, meander.FreeReliefRatio),
            graph.CellSizeMetres, graph.DischargeCap, 2 * graph.MaxFillRadius + 1, graph.MaxDegreeOfParallelism,
            _settings.WetlandDepth, _settings.RiverTrim * 100f);
    }

    /// <summary>
    /// Records the model's temperature and precipitation seasonality for a new map region, which the
    /// climate map has no room for and the seasons need at runtime.
    /// </summary>
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
            SeasonalCycleMap.Generate(mapRegion, regionX, regionZ, _api.WorldManager.RegionSize, _provider);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger,
                $"The seasonality map for region ({regionX}, {regionZ}) could not be written.", e);
        }
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

    /// <summary>The TerrainFeatures-pass surface shaping (beaches, bare rock), stopping the game if it fails.</summary>
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

    /// <summary>
    /// Stops only what chunk generation doesn't need; see <see cref="Dispose"/> for the rest.
    /// </summary>
    private void OnShutdown()
    {
        _debugMap?.Dispose();
        _debugMap = null;
        TranslocatorSearchCompat.Uninstall();
        ConfigLibCompat.Uninstall();
        _pregeneration?.Stop(forget: false);
        _water?.StopBackground();

        // Let the last column finish at full speed rather than at the /tdiff gpulimit share.
        InferenceThrottle.UtilizationPercent = 100;

        // Still loading: nothing can be generated without the models, so the server isn't kept
        // waiting for them. A chunk thread waiting on them dies; SaveDatabaseBackstop closes the save.
        if (!PipelineModels.IsReady) PipelineModels.Shutdown();
    }

    /// <summary>
    /// The server disposes mods before its final save and before it stops the chunk thread, which
    /// may still be generating a column. What generation uses is released once that thread has
    /// stopped, so it isn't killed and the server closes the save as usual.
    /// </summary>
    public override void Dispose()
    {
        OnShutdown();
        _api?.ObjectCache.Remove(ContinuityHeightSourceKey);

        TerrainDiffusionProvider provider = _provider;
        WorldWater water = _water;
        _provider = null;
        _water = null;
        _climate = null;
        void Release()
        {
            water?.Dispose();
            WatershedsCompat.Uninstall();
            RiversCompat.Uninstall();
            TerrainSamplerCompat.Uninstall();
            StoryTeleportCompat.Uninstall();
            DryShallowsCompat.Uninstall();
            TreeHeightFrame.Uninstall();
            SurfaceClimateCompat.Uninstall();
            ClimateScale.Uninstall();
            provider?.Dispose();
            PipelineModels.Shutdown();
        }

        if (_api == null) Release();
        else SaveDatabaseBackstop.ReleaseAfterChunkThread(_api, Release);
    }
}
