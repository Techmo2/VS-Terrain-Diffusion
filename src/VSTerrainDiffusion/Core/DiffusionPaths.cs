using System.IO;
using Vintagestory.API.Config;

namespace VSTerrainDiffusion.Core;

/// <summary>Well-known on-disk locations used by the mod, all under the Vintage Story data folder.</summary>
public static class DiffusionPaths
{
    public const string ModId = "vsterraindiffusion";

    /// <summary>Where the ONNX models and pipeline metadata live (~2.3 GB once downloaded).</summary>
    public static string ModelDirectory => Path.Combine(GamePaths.DataPath, "TerrainDiffusionModels");

    /// <summary>Where the downloaded ONNX Runtime native libraries live, one folder per RID.</summary>
    public static string RuntimeDirectory => Path.Combine(ModelDirectory, "onnxruntime");

    /// <summary>Cache of runtime-optimised ONNX graphs.</summary>
    public static string OptimizedModelDirectory => Path.Combine(ModelDirectory, "onnx-cache");

    public static string ConfigFile => Path.Combine(GamePaths.ModConfig, ModId + ".json");

    public static string ResolveAsset(string fileName) => Path.Combine(ModelDirectory, fileName);
}
