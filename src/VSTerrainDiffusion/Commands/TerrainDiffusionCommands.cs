using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Native;
using VSTerrainDiffusion.Pipeline;
using VSTerrainDiffusion.WorldGen;

namespace VSTerrainDiffusion.Commands;

/// <summary>
/// The <c>/terraindiffusion</c> (<c>/tdiff</c>) server command: registration, and the subcommands that
/// inspect or steer the generator. The climate and column readouts are in <see cref="ClimateReadouts"/>.
/// </summary>
public sealed class TerrainDiffusionCommands
{
    private readonly ICoreServerAPI _api;
    private readonly TerrainDiffusionModSystem _mod;
    private readonly ClimateReadouts _readouts;

    /// <summary>Creates the command handlers for one server.</summary>
    /// <param name="api">The server API.</param>
    /// <param name="mod">The mod system whose current world state the commands read.</param>
    public TerrainDiffusionCommands(ICoreServerAPI api, TerrainDiffusionModSystem mod)
    {
        _api = api;
        _mod = mod;
        _readouts = new ClimateReadouts(api, mod);
    }

    /// <summary>Registers <c>/terraindiffusion</c> and its subcommands with the server.</summary>
    public void Register()
    {
        CommandArgumentParsers parsers = _api.ChatCommands.Parsers;
        _api.ChatCommands
            .Create("terraindiffusion")
            .WithAlias("tdiff")
            .WithDescription("Inspect the Terrain Diffusion generator")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("status")
                .WithDescription("Show the model, device and world scaling in use")
                .HandleWith(OnStatus)
            .EndSubCommand()
            .BeginSubCommand("gpulimit")
                .WithDescription("Show or set the share of the time inference may keep the device busy")
                .WithArgs(parsers.OptionalInt("percent"))
                .HandleWith(OnGpuLimit)
            .EndSubCommand()
            .BeginSubCommand("map")
                .WithDescription("Show the address of the debug map, if it is running")
                .HandleWith(OnMap)
            .EndSubCommand()
            .BeginSubCommand("here")
                .WithDescription("Show the model's elevation and climate at your position")
                .RequiresPlayer()
                .HandleWith(_readouts.OnHere)
            .EndSubCommand()
            .BeginSubCommand("season")
                .WithDescription("Show the seasonal temperature and rainfall cycle at a position")
                .WithArgs(parsers.Int("x"), parsers.Int("z"))
                .HandleWith(_readouts.OnSeason)
            .EndSubCommand()
            .BeginSubCommand("climate")
                .WithDescription("Show the climate other mods see at a position (yours by default): monthly " +
                                 "temperature and rain, diurnal range and Köppen class")
                .WithArgs(parsers.OptionalInt("x"), parsers.OptionalInt("z"))
                .HandleWith(_readouts.OnClimate)
            .EndSubCommand()
            .BeginSubCommand("rivers")
                .WithDescription("Report the Rivers network around a position, for working out why rivers come up short")
                .WithArgs(parsers.OptionalInt("x"), parsers.OptionalInt("z"))
                .HandleWith(OnRivers)
            .EndSubCommand()
            .BeginSubCommand("tiles")
                .WithDescription("Show this world's tile database, or empty it with 'clear'")
                .WithArgs(parsers.OptionalWord("clear"))
                .HandleWith(OnTiles)
            .EndSubCommand()
            .BeginSubCommand("pregen")
                .WithDescription("Pregenerate terrain into the tile database: '<radius> [x z]' starts (around " +
                                 "spawn by default), 'stop' stops, no argument shows progress")
                .WithArgs(parsers.OptionalWord("radius|stop"), parsers.OptionalInt("x"), parsers.OptionalInt("z"))
                .HandleWith(OnPregen)
            .EndSubCommand()
            .BeginSubCommand("column")
                .WithDescription("Read back the generated block column at a position, for diagnosing world generation")
                .WithArgs(parsers.Int("x"), parsers.Int("z"))
                .HandleWith(_readouts.OnColumn)
            .EndSubCommand();
    }

    /// <summary>
    /// The block column two optional int arguments name, or the caller's when either is missing.
    /// </summary>
    /// <param name="args">The command's arguments.</param>
    /// <param name="first">Index of the x argument; z follows it.</param>
    /// <param name="x">Column X.</param>
    /// <param name="z">Column Z.</param>
    /// <returns>False when the arguments are missing and the caller has no entity.</returns>
    internal static bool TryPositionOrCaller(TextCommandCallingArgs args, int first, out int x, out int z)
    {
        if (!args.Parsers[first].IsMissing && !args.Parsers[first + 1].IsMissing)
        {
            x = (int)args[first];
            z = (int)args[first + 1];
            return true;
        }

        Entity caller = args.Caller?.Entity;
        x = caller == null ? 0 : (int)caller.Pos.X;
        z = caller == null ? 0 : (int)caller.Pos.Z;
        return caller != null;
    }

    /// <summary><c>/tdiff status</c>: the model, device, caches, timings and world scaling in use.</summary>
    private TextCommandResult OnStatus(TextCommandCallingArgs args)
    {
        DiffusionWorldSettings settings = _mod.Settings;
        TerrainDiffusionProvider provider = _mod.Provider;
        if (settings == null) return TextCommandResult.Success("Terrain Diffusion has not initialised yet.");
        if (!settings.Enabled) return TextCommandResult.Success("Terrain Diffusion is disabled for this world.");
        if (provider == null)
        {
            string reason = PipelineModels.LoadFailure?.Message ?? "still loading";
            return TextCommandResult.Success("Terrain Diffusion is not running: " + reason);
        }

        InferenceSettings inference = InferenceSettings.Current;
        Debug.DebugMapServer debugMap = _mod.DebugMap;
        WorldWater water = _mod.Water;
        TilePregenerator pregen = _mod.Pregeneration.Current;
        var lines = new List<string>
        {
            "Terrain Diffusion active",
            "",
            $"Requested device: {inference.InferenceDevice}",
            $"Model precision: coarse fp32, base {inference.BasePrecision}, decoder {inference.DecoderPrecision}",
            $"Runtime: {OnnxRuntimeBootstrap.ActiveRuntimeDescription}",
            $"Provider: {OnnxModel.ActiveProvider}",
            $"Model resolution: {WorldPipelineModelConfig.Instance.NativeResolution:0.##} m per pixel",
            $"CPU threads: {(inference.CpuThreads > 0 ? inference.CpuThreadsOr(0) : "auto")}",
            $"Models resident: {(inference.OffloadModels ? "no, one at a time (terraindiffusionOffloadModels)" : "yes")}",
            $"Latent batch: {provider.LatentBatchSize}",
            $"Pipeline cache: {provider.PipelineCachedBytes / 1048576.0:0.#} / " +
            $"{DiffusionConfig.Instance.TileCacheMegabytes} MB",
            $"Pipeline windows computed: {provider.PipelineComputedWindows}",
            $"Translocator search: {TranslocatorSearchCompat.Describe()}",
            $"Tiles dropped for closer work: {provider.PreemptionCount}",
            $"Device limit: {InferenceThrottle.Describe()}",
            $"Settings screen: {(ConfigLibCompat.IsPresent(_api) ? "ConfigLib" : "not installed")}",
            $"Debug map: {(debugMap?.IsRunning == true ? debugMap.Url : "off")}",
            $"Tiles generated: {provider.TilesGenerated}",
            $"Water: {(water == null ? "off" : water.Describe())}",
            $"Tile database: {DescribeTileStore(provider.Store)}",
            $"Pregeneration: {(pregen == null ? "none" : pregen.Describe())}",
            $"Tile size: {provider.TileSize}x{provider.TileSize} blocks",
            $"Average tile time: {provider.AverageTileMillis} ms",
            $"Model inference: {provider.ModelTimingSummary}",
            $"Inference share of tile time: {provider.InferenceSharePercent}%",
            "",
            $"Horizontal scale: {settings.MetersPerBlock:0.##} m per block (scale {settings.Scale})",
            $"Vertical scale: {settings.DescribeHeight()}",
            $"Sea level: Y {settings.SeaLevel}",
            $"World height: {settings.MapSizeY}",
            $"Headroom: {settings.HeadroomBlocks} blocks above sea level",
            $"Linear up to: {settings.LinearRangeMeters:0} m of elevation",
            $"Climate: {settings.ClimateMode.ToString().ToLowerInvariant()}"
        };

        if (!settings.Climate.IsNeutral) lines.Add($"Global climate: {settings.Climate}");
        lines.Add($"Latitude bands: {settings.Latitude.Status}");

        return TextCommandResult.Success(string.Join("\n", lines));
    }

    /// <summary>One line on the tile database: size, and this session's reads and writes.</summary>
    private static string DescribeTileStore(TerrainTileStore store) => store == null
        ? "off (terraindiffusionTileDatabase)"
        : $"{store.CountTiles()} tiles and {store.CountCoarseWindows()} coarse windows, " +
          $"{store.SizeOnDisk() / 1048576.0:0.#} MB; this session {store.TilesLoaded} tiles read " +
          $"({store.AverageLoadMillis:0.#} ms each), {store.TilesSaved} written ({store.AverageTileBytes / 1024} KB, " +
          $"{store.AverageSaveMillis:0.#} ms each), {store.TilesPending} queued; {store.CoarseWindowsLoaded} coarse " +
          $"windows read, {store.CoarseWindowsSaved} written";

    /// <summary><c>/tdiff gpulimit [percent]</c>: shows or sets, and saves with the world, the inference duty cycle.</summary>
    private TextCommandResult OnGpuLimit(TextCommandCallingArgs args)
    {
        // An omitted OptionalInt reads back as 0 rather than null, which would clamp to the lowest
        // setting instead of printing the current one. ArgCount is no help either: it is how many
        // tokens the parsers consume, not how many were given.
        if (args.Parsers[0].IsMissing)
        {
            return TextCommandResult.Success(
                $"Inference is limited to {InferenceThrottle.Describe()}. " +
                "Pass a percentage from 5 to 100 to change it.");
        }

        int requested = (int)args[0];
        InferenceThrottle.UtilizationPercent = requested;
        int applied = InferenceThrottle.UtilizationPercent;
        InferenceSettings.Current.GpuUtilizationPercent = applied;

        // Saved with the world, as the Customize screen's own value is.
        string code = InferenceSettings.Code(nameof(InferenceSettings.GpuUtilizationPercent));
        _api.WorldManager.SaveGame.WorldConfiguration.SetInt(code, applied);
        string persisted = $"Saved to this world's {code}.";

        string effect = applied >= 100
            ? "World generation runs at full speed."
            : $"World generation will take roughly {100f / applied:0.#} times as long, " +
              "in exchange for the device being free between model runs.";

        return TextCommandResult.Success($"Inference is now limited to {applied}% of the time. {effect} {persisted}");
    }

    /// <summary><c>/tdiff map</c>: the debug map's address, or how to turn it on.</summary>
    private TextCommandResult OnMap(TextCommandCallingArgs args)
    {
        Debug.DebugMapServer debugMap = _mod.DebugMap;
        if (debugMap == null || !debugMap.IsRunning)
        {
            return TextCommandResult.Success(
                $"The debug map is off. Set debugMapPort in {DiffusionPaths.ModId}.json (8088 is a reasonable " +
                "choice) and restart the server.");
        }

        return TextCommandResult.Success(
            $"Debug map: {debugMap.Url}\n" +
            $"Remembering {debugMap.TileCount} of the last {DiffusionConfig.Instance.DebugMapHistoryTiles} tiles.");
    }

    /// <summary><c>/tdiff rivers [x z]</c>: the Rivers network around a position, the caller's by default.</summary>
    private TextCommandResult OnRivers(TextCommandCallingArgs args)
    {
        if (!RiversCompat.Installed) return TextCommandResult.Success("Rivers is not generating this world's rivers.");

        if (!TryPositionOrCaller(args, 0, out int x, out int z))
            return TextCommandResult.Error("Give an x and z, or run this as a player.");

        return TextCommandResult.Success(RiversCompat.DescribeRegion(x, z));
    }

    /// <summary><c>/tdiff tiles [clear]</c>: shows or empties the world's tile database.</summary>
    private TextCommandResult OnTiles(TextCommandCallingArgs args)
    {
        TerrainDiffusionProvider provider = _mod.Provider;
        TerrainTileStore store = provider?.Store;
        if (store == null)
        {
            return TextCommandResult.Success(
                "This world keeps no tile database. Turn on terraindiffusionTileDatabase with /worldconfig and reload the world.");
        }

        string action = args.Parsers[0].IsMissing ? "" : ((string)args[0]).Trim().ToLowerInvariant();
        if (action == "") return TextCommandResult.Success($"{store.Path}\n{DescribeTileStore(store)}");
        if (action != "clear") return TextCommandResult.Error("The only action is 'clear'.");

        long removed = provider.ClearStoredTiles();
        return TextCommandResult.Success(
            $"Removed {removed} stored tiles; terrain not yet in a chunk will be generated again.");
    }

    /// <summary><c>/tdiff pregen [radius [x z] | stop]</c>: starts, stops or reports pregeneration.</summary>
    private TextCommandResult OnPregen(TextCommandCallingArgs args)
    {
        PregenerationController pregeneration = _mod.Pregeneration;
        string word = args.Parsers[0].IsMissing ? "" : ((string)args[0]).Trim().ToLowerInvariant();
        if (word == "") return TextCommandResult.Success(pregeneration.Describe());
        if (word == "stop") return StopPregen(pregeneration);

        if (!int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out int radius) || radius < 1)
        {
            return TextCommandResult.Error("Usage: /tdiff pregen <radius> [x z], or /tdiff pregen stop.");
        }
        if (radius > PregenerationController.MaxRadius)
        {
            return TextCommandResult.Error(
                $"The radius can be at most {TilePregenerator.Count(PregenerationController.MaxRadius)} blocks.");
        }

        TerrainDiffusionProvider provider = _mod.Provider;
        if (provider == null) return TextCommandResult.Error("Terrain Diffusion is not generating this world.");
        if (provider.Store == null)
        {
            return TextCommandResult.Error(
                "Pregen needs the tile database. Turn on terraindiffusionTileDatabase with /worldconfig, then " +
                "reload the world.");
        }
        if (pregeneration.IsRunning)
        {
            return TextCommandResult.Error(
                $"A pregen is already running: {pregeneration.Current.Describe()}. Stop it first with /tdiff pregen stop.");
        }

        int x, z;
        if (!args.Parsers[1].IsMissing && !args.Parsers[2].IsMissing)
        {
            x = (int)args[1];
            z = (int)args[2];
        }
        else
        {
            BlockPos spawn = _api.World.DefaultSpawnPosition?.AsBlockPos;
            x = spawn?.X ?? _api.WorldManager.MapSizeX / 2;
            z = spawn?.Z ?? _api.WorldManager.MapSizeZ / 2;
        }

        TilePregenerator job = pregeneration.Start(provider, x, z, radius, args.Caller.Player?.PlayerUID);

        // From this session's tile times, so only once some tile has been generated.
        int minutes = (int)Math.Ceiling(job.TileCount * provider.AverageTileMillis / 60000.0);
        string eta = minutes > 0 ? $" ETA {minutes} minute{(minutes == 1 ? "" : "s")}." : "";
        return TextCommandResult.Success(
            $"Pregen started: Radius is {TilePregenerator.Count(radius)} blocks " +
            $"({TilePregenerator.Count(job.TileCount)} tiles and {job.CoarseAreaCount} coarse tiles).{eta} " +
            "Stop with /tdiff pregen stop.");
    }

    /// <summary><c>/tdiff pregen stop</c>: stops the running job after its current tile and forgets it.</summary>
    private static TextCommandResult StopPregen(PregenerationController pregeneration)
    {
        TilePregenerator job = pregeneration.Current;
        if (job == null || !job.IsRunning)
        {
            pregeneration.Forget();
            return TextCommandResult.Success("No pregen is running.");
        }
        if (job.IsStopping) return TextCommandResult.Success($"Pregen is already stopping: {job.Describe()}.");

        string progress = job.Describe();
        job.Stop();
        pregeneration.Forget();
        return TextCommandResult.Success(
            $"Pregen stopping after the current tile: {progress}. Start it again with the same radius to resume.");
    }
}
