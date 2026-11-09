using System;
using Vintagestory.API.Server;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Fits the metre-to-block mapping to the terrain a seed actually produces around spawn, for worlds
/// using <c>heightMode: "auto"</c>. At the shipped isotropic scale the mapping is fixed and nothing
/// is measured.
/// </summary>
/// <remarks>
/// The measurement costs a few model invocations, so its result is kept in the save game: paying for
/// it on every server start would be wasteful, and a stored value keeps old and new chunks agreeing
/// if the survey settings change.
/// </remarks>
public static class HeightCalibration
{
    /// <summary>Save game key holding the measured peak elevation, in metres.</summary>
    private const string SaveKey = "vsterraindiffusion:peakelevation";

    /// <summary>The peak elevation this world stored when it was calibrated, if it has been.</summary>
    /// <param name="api">The server API.</param>
    /// <returns>The peak in metres, or null when the world has never been calibrated.</returns>
    public static float? Stored(ICoreServerAPI api)
    {
        byte[] stored;
        try
        {
            stored = api.WorldManager.SaveGame.GetData(SaveKey);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(api.Logger,
                "This world's stored terrain height calibration could not be read.", e);
        }
        return stored is { Length: sizeof(float) } ? BitConverter.ToSingle(stored, 0) : null;
    }

    /// <summary>
    /// Applies the stored calibration, or measures, applies and stores a new one. Must run before
    /// anything asks for a tile that is kept: it decides the block height of every surface.
    /// </summary>
    /// <param name="api">The server API.</param>
    /// <param name="provider">The world's terrain; its tiles are dropped after a new measurement.</param>
    /// <param name="settings">The world's settings, which receive the calibration.</param>
    /// <param name="center">Where to centre the survey: the spawn, or null for the model's origin.</param>
    public static void Apply(ICoreServerAPI api, TerrainDiffusionProvider provider, DiffusionWorldSettings settings,
        TerrainDiffusionProvider.SpawnCandidate? center)
    {
        if (!settings.WantsCalibration) return;

        if (Stored(api) is { } saved)
        {
            settings.ApplyCalibration(saved);
            api.Logger.VerboseDebug("[{0}] Reusing the stored terrain height calibration ({1:0} m peak).",
                DiffusionPaths.ModId, saved);
            return;
        }

        api.Logger.Notification(
            "[{0}] Measuring how tall the terrain gets around spawn so the world height can be used fully. " +
            "This runs the model a few times and only happens once for this world.",
            DiffusionPaths.ModId);

        float? peak;
        try
        {
            peak = provider.MeasurePeakElevation(
                center?.BlockX ?? settings.OriginBlockX, center?.BlockZ ?? settings.OriginBlockZ);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(api.Logger, "Terrain height calibration failed.", e);
        }

        if (peak == null) return;

        settings.ApplyCalibration(peak.Value);

        // The spawn search may already have built a tile, and a tile stores block heights rather than
        // metres. Calibration has just changed what a metre is worth, so anything generated before
        // now describes a different landscape from everything after it.
        provider.InvalidateTiles();

        try
        {
            api.WorldManager.SaveGame.StoreData(SaveKey, BitConverter.GetBytes(peak.Value));
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(api.Logger,
                "The terrain height calibration could not be saved to this world.", e);
        }
    }
}
