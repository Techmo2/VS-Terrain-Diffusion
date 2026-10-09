using System.IO;
using Vintagestory.API.Config;

namespace VSTerrainDiffusion.Core;

/// <summary>Well-known on-disk locations used by the mod, all under the Vintage Story data folder.</summary>
public static class DiffusionPaths
{
    public const string ModId = "vsterraindiffusion";

    /// <summary>
    /// Where the ONNX models and pipeline metadata live (~2.0-2.3 GB once downloaded). Absolute even
    /// when the server was given a relative <c>--dataPath</c>, because the worker process runs in a
    /// directory of its own and would resolve a relative path from there.
    /// </summary>
    public static string ModelDirectory => Path.GetFullPath(Path.Combine(GamePaths.DataPath, "TerrainDiffusionModels"));

    /// <summary>Where the downloaded ONNX Runtime native libraries live, one folder per RID.</summary>
    public static string RuntimeDirectory => Path.Combine(ModelDirectory, "onnxruntime");

    /// <summary>Cache of runtime-optimised ONNX graphs.</summary>
    public static string OptimizedModelDirectory => Path.Combine(ModelDirectory, "onnx-cache");

    public static string ConfigFile => Path.Combine(GamePaths.ModConfig, ModId + ".json");

    public static string ResolveAsset(string fileName) => Path.Combine(ModelDirectory, fileName);
}
