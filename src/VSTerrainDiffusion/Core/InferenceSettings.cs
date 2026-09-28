using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// How the model runs for the loaded world, from the world's Terrain Diffusion settings: the
/// <c>terraindiffusion</c> tab of the Customize screen (<c>worldconfig.json</c>), or
/// <c>/worldconfig</c> afterwards. Each attribute code is <see cref="CodePrefix"/> plus the property
/// name.
///
/// Read once as the server starts, before anything is downloaded. The Customize screen is built by
/// the game before any mod code runs, so it lists every device and precision; one this machine
/// cannot run stops the game here (<see cref="InferenceCompatibility"/>) rather than being swapped
/// for another.
/// </summary>
public sealed class InferenceSettings
{
    public const string CodePrefix = "terraindiffusion";

    public static readonly string[] AllModelLoadModes = { "file", "memory" };

    /// <summary>
    /// "auto", "cpu", "openvino", "cuda", "tensorrt-rtx", "directml" or "coreml". OpenVINO and
    /// TensorRT RTX are used only when named.
    /// </summary>
    public string InferenceDevice { get; private init; } = "auto";

    /// <summary>"file" or "memory": where ONNX sessions load model graphs from.</summary>
    public string ModelLoadMode { get; private init; } = "file";

    /// <summary>
    /// Keep only one model resident on the GPU at a time. Holds peak VRAM near 1.5 GB instead of
    /// about 2.5 GB, and roughly triples tile time.
    /// </summary>
    public bool OffloadModels { get; private init; }

    /// <summary>
    /// Share of the time, as a percentage, that world generation may keep the inference device
    /// busy; see <see cref="Pipeline.InferenceThrottle"/>. 100 is unlimited.
    /// </summary>
    public int GpuUtilizationPercent { get; internal set; } = 100;

    /// <summary>Verify SHA-256 of pre-existing model files on startup.</summary>
    public bool ValidateModelHashes { get; private init; } = true;

    /// <summary>Download the ONNX Runtime and OpenVINO native libraries automatically.</summary>
    public bool DownloadRuntime { get; private init; } = true;

    /// <summary>"fp32", "fp16" or "int8". FP16 needs a GPU provider; INT8 is for CPU and OpenVINO.</summary>
    public string DecoderPrecision { get; private init; } = "fp32";

    /// <summary>"fp32" or "fp16" for the base (latent) model, which is most of the work in a tile.</summary>
    public string BasePrecision { get; private init; } = "fp32";

    /// <summary>"fp32" or "fp16" for the coarse model.</summary>
    public string CoarsePrecision { get; private init; } = "fp32";

    private static InferenceSettings _current;

    /// <summary>The loaded world's settings, or the defaults when no world has been loaded (offline tools).</summary>
    public static InferenceSettings Current => _current ??= new InferenceSettings();

    /// <summary>The world config attribute code of a setting, e.g. <c>terraindiffusionInferenceDevice</c>.</summary>
    public static string Code(string setting) => CodePrefix + setting;

    /// <summary>
    /// Reads the settings from the world being loaded and stops the game if this machine cannot run
    /// them. The save is loaded, and a new world's config written, before any mod's
    /// <c>StartPre</c>, so this can run there, ahead of the model download.
    /// </summary>
    public static InferenceSettings Load(ICoreServerAPI api)
    {
        ITreeAttribute world = api.WorldManager.SaveGame?.WorldConfiguration;
        var defaults = new InferenceSettings();

        var settings = new InferenceSettings
        {
            InferenceDevice = NormalizeDevice(Read(world, nameof(InferenceDevice), defaults.InferenceDevice)),
            ModelLoadMode = Normalize(Read(world, nameof(ModelLoadMode), defaults.ModelLoadMode)),
            OffloadModels = Read(world, nameof(OffloadModels), "false").ToBool(defaults.OffloadModels),
            GpuUtilizationPercent = Pipeline.InferenceThrottle.Clamp(
                Read(world, nameof(GpuUtilizationPercent), "").ToInt(defaults.GpuUtilizationPercent)),
            ValidateModelHashes = Read(world, nameof(ValidateModelHashes), "true").ToBool(defaults.ValidateModelHashes),
            DownloadRuntime = Read(world, nameof(DownloadRuntime), "true").ToBool(defaults.DownloadRuntime),
            DecoderPrecision = Normalize(Read(world, nameof(DecoderPrecision), defaults.DecoderPrecision)),
            BasePrecision = Normalize(Read(world, nameof(BasePrecision), defaults.BasePrecision)),
            CoarsePrecision = Normalize(Read(world, nameof(CoarsePrecision), defaults.CoarsePrecision))
        };

        if (Array.IndexOf(AllModelLoadModes, settings.ModelLoadMode) < 0)
        {
            throw DiffusionFailure.Fatal(api.Logger,
                $"World setting {Code(nameof(ModelLoadMode))} is \"{settings.ModelLoadMode}\", which is not one of " +
                $"{string.Join(", ", AllModelLoadModes)}. Change it with /worldconfig.");
        }

        InferenceCompatibility compatibility = InferenceCompatibility.Current;
        compatibility.Log(api.Logger);
        compatibility.Require(settings, api.Logger);

        _current = settings;
        return settings;
    }

    /// <summary>One spelling per device, so "RTX" and "tensorrt-rtx" are the same choice.</summary>
    internal static string NormalizeDevice(string device) => Normalize(device) switch
    {
        "dml" => "directml",
        "trt-rtx" or "tensorrtrtx" or "rtx" => "tensorrt-rtx",
        var other => other
    };

    private static string Normalize(string value) => (value ?? "").Trim().ToLowerInvariant();

    private static string Read(ITreeAttribute world, string setting, string fallback) =>
        DiffusionWorldSettings.ReadWorldConfig(world, Code(setting), fallback);
}
