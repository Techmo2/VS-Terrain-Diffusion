using System.Threading;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Marks the thread generating throwaway chunk columns for one of Vintage Story's chunk peeks, and
/// remembers which ground that peek will actually look at.
///
/// A peek runs the registered worldgen handlers over a 3x3 block of columns, hands the result to a
/// callback and drops the lot. The static translocator search does one every 250 ms, hundreds or
/// thousands of blocks away, until it finds a ruin.
///
/// Two things key off this. Terrain generated for a peek is evicted from the tile cache before a
/// player's (<see cref="TerrainDiffusionProvider.GetTile"/>), and the model-backed map layers only
/// sample inside <see cref="Covers"/> (<see cref="DiffusionMapLayer.GenLayer"/>) instead of across
/// the whole 512-block map region the game asks them for.
///
/// Armed by <see cref="TranslocatorSearchCompat"/> around the game's own peek routine, which calls
/// the generators inline on that thread.
/// </summary>
public static class ChunkPeekScope
{
    /// <summary>
    /// Chunks of slack around the peeked 3x3, so a map value read for one of those columns is
    /// never one of the cheap ones. One chunk would do - the corners
    /// <c>GenStructures</c> reads for a column sit one map sample past its far edge, and a sample
    /// is 32 blocks at every scale the model layers use except shrubs, which is 16 - but the whole
    /// point is to be able to say the read set is covered without counting on that.
    /// </summary>
    private const int MarginChunks = 2;

    private const int ChunkSize = 32;

    // A depth rather than a flag, so a nested or re-entered peek cannot disarm the outer one.
    private static readonly ThreadLocal<int> Depth = new(() => 0);
    private static readonly ThreadLocal<Bounds> Window = new(() => default);

    private readonly struct Bounds
    {
        public readonly int MinX, MinZ, MaxX, MaxZ;

        public Bounds(int minX, int minZ, int maxX, int maxZ)
        {
            MinX = minX; MinZ = minZ; MaxX = maxX; MaxZ = maxZ;
        }
    }

    public static bool Active => Depth.Value > 0;

    /// <summary>Enters a peek centred on a chunk column, which the game generates a 3x3 around.</summary>
    public static void Enter(int centreChunkX, int centreChunkZ)
    {
        if (Depth.Value == 0)
        {
            int reach = 1 + MarginChunks;
            Window.Value = new Bounds(
                (centreChunkX - reach) * ChunkSize,
                (centreChunkZ - reach) * ChunkSize,
                (centreChunkX + reach + 1) * ChunkSize,
                (centreChunkZ + reach + 1) * ChunkSize);
        }

        Depth.Value++;
    }

    public static void Exit()
    {
        if (Depth.Value > 0) Depth.Value--;
    }

    /// <summary>
    /// Whether a world position is one the running peek can actually read. False only while a peek
    /// is running, so callers outside one always get the real thing.
    /// </summary>
    public static bool Covers(int blockX, int blockZ)
    {
        if (Depth.Value == 0) return true;

        Bounds w = Window.Value;
        return blockX >= w.MinX && blockX < w.MaxX && blockZ >= w.MinZ && blockZ < w.MaxZ;
    }
}
