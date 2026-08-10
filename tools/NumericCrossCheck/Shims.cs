using System.IO;
namespace VSTerrainDiffusion.Pipeline;

// Minimal stand-ins so the numeric code can run outside of Vintage Story.
public static class ModelAssetManager
{
    public static string ResolveAssetPath(string fileName) => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        ".config/VintagestoryData/TerrainDiffusionModels", fileName);
}

public sealed class WorldPipelineModelConfig
{
    public float[] FrequencyMult = { 1f, 1f, 1f, 1f, 1f };
    public float NativeResolution = 30f;
    public int LatentCompression = 8;
    public static WorldPipelineModelConfig Instance { get; } = new();
}
