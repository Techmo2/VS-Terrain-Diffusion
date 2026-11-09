using System;
using Vintagestory.API.Server;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Asking Rivers' network where the rivers are, per chunk as it is generated or ahead of time.
/// </summary>
public static partial class RiversCompat
{
    /// <summary>
    /// The river network over one chunk, as a boxed <c>RiverSample[1024]</c>, or null when Rivers is
    /// not carving this world. Also writes the flow vectors and river distances that its boat
    /// physics and rendering read back, so this has to be called for every chunk generated.
    /// </summary>
    public static Array SamplesForChunk(int chunkX, int chunkZ, IServerChunk[] chunks)
    {
        if (!_available) return null;

        try
        {
            return (Array)_samplesForChunk.Invoke(null, new object[] { chunkX, chunkZ, chunks });
        }
        catch (Exception)
        {
            // A river region that will not build is Rivers' business; the chunk still gets terrain.
            return null;
        }
    }

    /// <summary>One column's river network, in the terms the terrain generator needs.</summary>
    public readonly struct Sample
    {
        /// <summary>Blocks to the nearest river; 0 or less is inside one.</summary>
        public readonly double Distance;

        /// <summary>Half the channel's depth, as a fraction of the world above sea level.</summary>
        public readonly double BankFactor;

        /// <summary>Creates a sample.</summary>
        /// <param name="distance">Blocks to the nearest river.</param>
        /// <param name="bankFactor">
        /// Half the channel's depth, as a fraction of the world above sea level.
        /// </param>
        public Sample(double distance, double bankFactor)
        {
            Distance = distance;
            BankFactor = bankFactor;
        }

        /// <summary>Whether the column is within a valley's reach of a river.</summary>
        /// <param name="maxValleyWidth">Rivers' <c>maxValleyWidth</c>.</param>
        /// <returns>True inside the valley.</returns>
        public bool InValley(double maxValleyWidth) => Distance < maxValleyWidth;
    }

    /// <summary>One column's sample out of <see cref="SamplesForChunk"/>'s array.</summary>
    /// <param name="samples">The chunk's samples.</param>
    /// <param name="index">The column, z * 32 + x.</param>
    /// <returns>The sample.</returns>
    public static Sample At(Array samples, int index)
    {
        object boxed = samples.GetValue(index);
        return new Sample(_riverDistance(boxed), _bankFactor(boxed));
    }

    /// <summary>
    /// Rivers' <c>maxValleyWidth</c>: blocks from a river within which the ground is pulled down.
    /// </summary>
    public static double MaxValleyWidth => _maxValleyWidth;

    /// <summary>
    /// The river sample at one column, asked of the network without generating anything, or null
    /// when it cannot say. It is the same <c>SampleRiver</c> call <see cref="SamplesForChunk"/> makes
    /// for every column of a chunk, minus the flow data that call writes into the chunk.
    /// </summary>
    public static Sample? SampleAt(int worldX, int worldZ) => SampleAt(ChunkContext(worldX >> 5, worldZ >> 5), worldX, worldZ);

    /// <summary>
    /// <see cref="SampleAt(int, int)"/> with the column's chunk context already resolved, for a
    /// walk over many columns that can resolve each chunk once (<see cref="ChunkContext"/>).
    /// </summary>
    public static Sample? SampleAt(object context, int worldX, int worldZ)
    {
        if (context is not object[] { Length: 2 } parts) return null;

        try
        {
            object sample = _sampleRiver.Invoke(null, new[] { worldX, worldZ, parts[1], parts[0] });
            return sample == null ? null : new Sample(_riverDistance(sample), _bankFactor(sample));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether the river network can be asked about ground no chunk has been made for.</summary>
    public static bool CanSampleNetwork =>
        _available && _instance != null && _getRiverRegion != null && _getSegments != null && _sampleRiver != null;

    /// <summary>
    /// The river network around one chunk: the region it belongs to and the segments near it.
    ///
    /// Resolving this is an R-tree search that allocates, and it can generate a whole river region
    /// the first time a plate is touched, so a walk over many sample points has to pay for each
    /// chunk once rather than once per point. Null when the network cannot say.
    /// </summary>
    public static object ChunkContext(int chunkX, int chunkZ)
    {
        if (!CanSampleNetwork) return null;

        try
        {
            object region = _getRiverRegion.Invoke(_instance, new object[] { chunkX, chunkZ });
            object segments = _getSegments.Invoke(_instance, new object[] { chunkX, chunkZ });
            return region == null || segments == null ? null : new[] { region, segments };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Blocks from a world position to the nearest river in <paramref name="context"/>, or -1 when
    /// it cannot say.
    ///
    /// This asks about ground that has not been generated, which Rivers is happy to answer because
    /// it routes from the ocean map rather than from terrain - there is no circularity in letting
    /// the rivers decide where the valleys go.
    /// </summary>
    public static double DistanceToRiver(object context, int worldX, int worldZ)
    {
        if (context is not object[] { Length: 2 } parts) return -1.0;

        try
        {
            object sample = _sampleRiver.Invoke(null, new[] { worldX, worldZ, parts[1], parts[0] });
            return sample == null ? -1.0 : _riverDistance(sample);
        }
        catch (Exception)
        {
            return -1.0;
        }
    }
}
