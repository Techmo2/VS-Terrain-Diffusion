using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using Vintagestory.API.Common;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Reads terrain from the Terrain Diffusion Flask API (<c>python -m terrain_diffusion api</c>).
///
/// The API is deliberately thin: one <c>GET /terrain</c> returns int16 elevation in metres
/// followed by four interleaved float32 climate channels. Because the server is single threaded
/// and generates uncached regions synchronously, requests are serialised here and given a long
/// timeout - a first visit to a new part of the world legitimately takes seconds.
/// </summary>
public sealed class TerrainApiSource : ITerrainSource
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILogger _logger;
    private readonly int _retries;

    /// <summary>The API does not report its model's pixel size, so it comes from the mod config.</summary>
    public float NativeResolutionMeters { get; }

    public TerrainApiSource(TerrainApiConfig config, ILogger logger)
    {
        _logger = logger;
        _baseUrl = config.Url.TrimEnd('/');
        _retries = config.Retries;
        NativeResolutionMeters = config.NativeResolutionMeters;

        _http = new HttpClient(new SocketsHttpHandler
        {
            // One connection matches the server's single-threaded loop and keeps it alive between
            // tiles, which matters when a tile is a handful of milliseconds of cached data.
            MaxConnectionsPerServer = 1,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10)
        })
        {
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };
    }

    /// <summary>
    /// Checks that the API is up before world generation starts, so a misconfigured URL is a clear
    /// startup error rather than a wall of failed chunks.
    /// </summary>
    /// <returns>Null when healthy, otherwise the reason it is not.</returns>
    public string CheckHealth()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/health");
            using HttpResponseMessage response = _http.Send(request, HttpCompletionOption.ResponseContentRead);
            if (response.StatusCode != HttpStatusCode.OK) return "the server answered " + (int)response.StatusCode;
            return null;
        }
        catch (Exception e)
        {
            return (e.InnerException ?? e).Message;
        }
    }

    public TerrainSample Fetch(int i1, int j1, int i2, int j2, bool withClimate)
    {
        int h = i2 - i1, w = j2 - j1;
        if (h <= 0 || w <= 0) throw new ArgumentException($"Empty terrain window ({i1}, {j1})-({i2}, {j2})");

        // scale is always 1: the API's upsampling is a plain bilinear filter, and doing it here
        // instead keeps the payload down and gives us the padded array the slope kernel needs.
        string url = string.Format(CultureInfo.InvariantCulture,
            "{0}/terrain?i1={1}&j1={2}&i2={3}&j2={4}&scale=1", _baseUrl, i1, j1, i2, j2);

        byte[] payload = Download(url, h, w);
        return Decode(payload, h, w, withClimate);
    }

    private byte[] Download(string url, int h, int w)
    {
        int expected = h * w * (sizeof(short) + TerrainSample.ClimateChannels * sizeof(float));
        Exception last = null;

        for (int attempt = 0; attempt <= _retries; attempt++)
        {
            if (attempt > 0)
            {
                // The server serialises requests, so a failure is usually a restart or a transient
                // overload rather than something a fast retry would fix.
                Thread.Sleep(500 * attempt);
                _logger.VerboseDebug("[{0}] Retrying terrain request ({1}/{2}): {3}",
                    DiffusionPaths.ModId, attempt, _retries, last?.Message);
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using HttpResponseMessage response = _http.Send(request, HttpCompletionOption.ResponseContentRead);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    // The API reports its own errors as JSON with a 400, and those are bugs in our
                    // request rather than something worth hammering the server over.
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    throw new InvalidOperationException(
                        $"terrain API returned {(int)response.StatusCode}: {Trim(body)}");
                }

                byte[] bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (bytes.Length < expected)
                {
                    throw new InvalidOperationException(
                        $"terrain API returned {bytes.Length} bytes for a {w}x{h} window, expected {expected}");
                }
                return bytes;
            }
            catch (Exception e)
            {
                last = e.InnerException ?? e;
            }
        }

        throw new InvalidOperationException(
            $"Could not read terrain from {_baseUrl}: {last?.Message}", last);
    }

    /// <summary>
    /// Unpacks the binary response: int16 metres, then (H, W, 4) interleaved float32 climate,
    /// which is de-interleaved into one plane per channel.
    /// </summary>
    private static TerrainSample Decode(byte[] payload, int h, int w, bool withClimate)
    {
        int plane = h * w;
        var elevation = new float[plane];
        var span = new ReadOnlySpan<byte>(payload);

        for (int i = 0; i < plane; i++)
        {
            elevation[i] = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(i * sizeof(short), sizeof(short)));
        }

        if (!withClimate) return new TerrainSample(h, w, elevation, null);

        const int channels = TerrainSample.ClimateChannels;
        var climate = new float[channels * plane];
        int offset = plane * sizeof(short);

        for (int i = 0; i < plane; i++)
        {
            int at = offset + i * channels * sizeof(float);
            for (int c = 0; c < channels; c++)
            {
                climate[c * plane + i] = BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(span.Slice(at + c * sizeof(float), sizeof(float))));
            }
        }

        return new TerrainSample(h, w, elevation, climate);
    }

    private static string Trim(string text) =>
        string.IsNullOrEmpty(text) ? "(no body)" : text.Length <= 200 ? text.Trim() : text[..200].Trim() + "...";

    public string Describe() =>
        $"Terrain Diffusion API at {_baseUrl} ({NativeResolutionMeters:0.##} m per model pixel)";

    public void Dispose() => _http.Dispose();
}
