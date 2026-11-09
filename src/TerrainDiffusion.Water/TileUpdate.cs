using System.Collections.Generic;

namespace TerrainDiffusion.Water;

/// <summary>What one <see cref="RiverGraph.AddTile"/> changed.</summary>
/// <param name="ChangedTiles">Tiles whose level, discharge or finality may have changed, in row-major order;
/// the added tile is always among them.</param>
/// <param name="ReleasedChunks">Chunks that became final: their water will never change again.</param>
public sealed record TileUpdate(IReadOnlyList<TileCoord> ChangedTiles, IReadOnlyList<ReleasedChunk> ReleasedChunks);
