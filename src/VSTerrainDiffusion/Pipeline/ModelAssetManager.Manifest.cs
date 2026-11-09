using System.Collections.Generic;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// The pinned model files - where each comes from, its size and SHA-256 - and which of them the
/// current settings select.
/// </summary>
public static partial class ModelAssetManager
{
    /// <summary>The Hugging Face repository the FP32 models come from.</summary>
    private const string RepositorySlug = "xandergos/terrain-diffusion-30m-onnx";

    /// <summary>The pinned commit of <see cref="RepositorySlug"/>.</summary>
    private const string Revision = "ad2df557eca5645f588766101cf3bc3682455c3e";

    /// <summary>One pinned file.</summary>
    private sealed class Asset
    {
        /// <summary>Its name on disk and in the repository.</summary>
        public string FileName;

        /// <summary>Its SHA-256, lower-case hex.</summary>
        public string Sha256;

        /// <summary>Its size, which is checked before the hash.</summary>
        public long SizeBytes;

        /// <summary>Where it comes from when not the pinned repository, or null.</summary>
        public string UrlOverride;

        /// <summary>Where it is downloaded from.</summary>
        public string Url => UrlOverride ??
            $"https://huggingface.co/{RepositorySlug}/resolve/{Revision}/{FileName}?download=true";
    }

    /// <summary>
    /// Pinned manifest for the commit above. Sizes and hashes come from the Hugging Face
    /// <c>paths-info</c> API; regenerate them with tools/refresh-manifest.sh when bumping the revision.
    /// </summary>
    private static readonly Asset[] CommonAssets =
    {
        new()
        {
            FileName = "pipeline_data.json",
            SizeBytes = 12226,
            Sha256 = "e3132c3ef0c65d8613615f9278ffe23bbd9363ddcd87f1cc6f18456bcc9efe5c"
        },
        new()
        {
            FileName = "world_pipeline_config.json",
            SizeBytes = 774,
            Sha256 = "c60f0b74d89317e64cfc623fbfdd828f1b5b2e50aa75020ac4001103381853bd"
        }
    };

    /// <summary>The annual coarse model (<see cref="CoarseModelSpec.Annual"/>).</summary>
    private static readonly Asset Fp32CoarseAsset = new()
    {
        FileName = "coarse_model.onnx",
        SizeBytes = 22497125,
        Sha256 = "d6ca15b21b2e35d5e594a9ac7a4249a2376590c0ad2b5b49a1e6e2d033450008"
    };

    /// <summary>
    /// The seasonal coarse model (<see cref="CoarseModelSpec.Seasonal"/>), exported from
    /// <c>td30m_seasonal_ft</c> with the reference repository's ONNX exporter.
    /// </summary>
    private static readonly Asset SeasonalCoarseAsset = new()
    {
        FileName = "coarse_model.seasonal.onnx",
        SizeBytes = 22626150,
        Sha256 = "7aa8783fa496b5c742014a5a0f17f6d39246b9d490cc9e62aa90f241206079ef",
        UrlOverride = ReleaseUrl("coarse-seasonal-v1", "coarse_model.seasonal.onnx")
    };

    /// <summary>The base (latent) model at full precision.</summary>
    private static readonly Asset Fp32BaseAsset = new()
    {
        FileName = "base_model.onnx",
        SizeBytes = 2029994361,
        Sha256 = "543de788f73d0a4012685c908259f615601102aace4751aeccec64154ba145c0"
    };

    /// <summary>The decoder at full precision.</summary>
    private static readonly Asset Fp32DecoderAsset = new()
    {
        FileName = "decoder_model.onnx",
        SizeBytes = 223854143,
        Sha256 = "6473ae47ca6ec4d743d30fe4f5d381fe4158899714eff09b762005bdbdef68c1"
    };

    /// <summary>An asset published by one of this repository's model releases.</summary>
    private static string ReleaseUrl(string tag, string fileName) =>
        $"https://github.com/Techmo2/VS-Terrain-Diffusion/releases/download/{tag}/{fileName}";

    /// <summary>
    /// Half-precision exports for GPU providers: float32 in and out, narrower maths inside. Each is
    /// a separate export, not a conversion of the file above, because a naive conversion of the base
    /// model overflows its conditioning normalisation. Built by the *-fp16-release workflows, whose
    /// pinned hashes and these are updated together.
    /// </summary>
    private static readonly Asset Fp16BaseAsset = new()
    {
        FileName = "base_model.fp16.onnx",
        SizeBytes = 507810847,
        Sha256 = "fcb4ddd9a9f4b6aebcfc9663d40173128c2c0f1a105bf10a286321cf4a521647",
        UrlOverride = ReleaseUrl("base-fp16-v1", "base_model.fp16.onnx")
    };

    /// <summary>
    /// H/W are fixed at export, so the file name and release tag carry the window. 256 is what the
    /// decoder stage asks for (<see cref="WorldPipeline.DecoderTileSize"/>).
    /// </summary>
    private static readonly Asset Fp16DecoderAsset = new()
    {
        FileName = "decoder_model.fp16.256.onnx",
        SizeBytes = 56264771,
        Sha256 = "2429cf246bf8905b17763bb91edd0adbe5a8dc09be7c2228a62e3ad968f0fb8e",
        UrlOverride = ReleaseUrl("decoder-fp16-256-v1", "decoder_model.fp16.256.onnx")
    };

    /// <summary>The decoder with INT8 weights, for the CPU.</summary>
    private static readonly Asset Int8DecoderAsset = new()
    {
        FileName = "decoder_model.int8.onnx",
        SizeBytes = 43497445,
        Sha256 = "0ae464c884593b3016a19365caf3ae43a7e26743c8ef1234814e10bbbd9b5b74",
        UrlOverride = ReleaseUrl("decoder-int8-v1", "decoder_model.int8.onnx")
    };

    /// <summary>
    /// Which trained models these are, whatever precision they run at: the FP16 and INT8 files are
    /// exports of the same weights, so they are named by the FP32 originals.
    /// </summary>
    public static string ModelIdentity =>
        $"{RepositorySlug}@{Revision}|{SelectedCoarseAsset.Sha256}|{Fp32BaseAsset.Sha256}|{Fp32DecoderAsset.Sha256}";

    /// <summary>The decoder at the configured precision.</summary>
    /// <remarks>
    /// Every precision is matched by name. The config is validated at startup, so the default arms are
    /// unreachable; they throw rather than pick a file, because a model nobody selected must never load.
    /// </remarks>
    /// <exception cref="ModelAssetException">The precision names no model.</exception>
    private static Asset SelectedDecoderAsset => InferenceSettings.Current.DecoderPrecision switch
    {
        "fp32" => Fp32DecoderAsset,
        "fp16" => Fp16DecoderAsset,
        "int8" => Int8DecoderAsset,
        var other => throw UnknownPrecision(nameof(InferenceSettings.DecoderPrecision), other)
    };

    /// <summary>
    /// The world's coarse model, always FP32: there is no half-precision coarse model to download,
    /// because it is not worth shipping until its layernorms are re-exported.
    /// </summary>
    private static Asset SelectedCoarseAsset => InferenceSettings.Current.ClimateModel switch
    {
        "annual" => Fp32CoarseAsset,
        "seasonal" => SeasonalCoarseAsset,
        var other => throw new ModelAssetException($"{nameof(InferenceSettings.ClimateModel)} \"{other}\" does not name a model")
    };

    /// <summary>The base model at the configured precision.</summary>
    /// <exception cref="ModelAssetException">The precision names no model.</exception>
    private static Asset SelectedBaseAsset => InferenceSettings.Current.BasePrecision switch
    {
        "fp32" => Fp32BaseAsset,
        "fp16" => Fp16BaseAsset,
        var other => throw UnknownPrecision(nameof(InferenceSettings.BasePrecision), other)
    };

    /// <summary>The error for a precision setting that names no model.</summary>
    /// <param name="setting">The setting's name.</param>
    /// <param name="value">Its value.</param>
    /// <returns>The exception, to throw.</returns>
    private static ModelAssetException UnknownPrecision(string setting, string value) =>
        new($"{setting} \"{value}\" does not name a model");

    /// <summary>Where the player can download the files by hand.</summary>
    public static string OfflineHelpUrl => $"https://huggingface.co/{RepositorySlug}/tree/{Revision}";

    /// <summary>
    /// Every file the current settings need: the shared data, then the coarse, base and decoder models.
    /// </summary>
    /// <returns>The files.</returns>
    private static IEnumerable<Asset> RequiredAssets()
    {
        foreach (Asset asset in CommonAssets) yield return asset;
        yield return SelectedCoarseAsset;
        yield return SelectedBaseAsset;
        yield return SelectedDecoderAsset;
    }

    /// <summary>The files the current configuration asks for, as a comparable key.</summary>
    private static string CurrentSelection()
    {
        var names = new List<string>();
        foreach (Asset asset in RequiredAssets()) names.Add(asset.FileName);
        return string.Join('|', names);
    }
}
