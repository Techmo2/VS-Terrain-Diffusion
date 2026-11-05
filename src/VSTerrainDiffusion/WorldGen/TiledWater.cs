using System;
using System.Collections.Generic;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Lake water for one water region of S x S terrain tiles, computed tile by tile in any order, with
/// each 32-block chunk released only once no later tile can change its water. A port of the
/// conditioning lab's <c>tiled_water.py</c> (<c>TiledWater</c>), on whole-block heights; the design
/// is in the terrain-diffusion project's WATER_FILL.md.
///
/// A column's water surface is the lowest level from which water could leave it for an outlet
/// without crossing anything higher (priority-flood depression filling). Outlets are columns below
/// <see cref="DrainBelow"/> (the sea) and the region's outermost ring of columns, so a region's
/// water depends on its own tiles only.
///
/// Each arriving tile is flooded on its own from its perimeter cells, which leaves its interior
/// summarised by them: a graph of perimeter cells, joined across tile seams, gives every perimeter
/// cell the level its water leaves the region at. Tiles not here yet are unknown, so the graph is
/// solved twice: with the edge of the generated area draining (a lower bound) and without (an upper
/// bound). New tiles only raise the one and lower the other, so where they meet the water is final.
///
/// Not thread safe; the caller serialises access.
/// </summary>
public sealed class WaterRegion
{
    public const int ChunkSize = 32;

    /// <summary>A released chunk with no lake water in it.</summary>
    public static readonly short[] DryChunk = Array.Empty<short>();

    private readonly int _t;
    private readonly int _s;
    private readonly int _ne;
    private readonly int _chunksPerTile;
    private readonly int _chunksPerSide;
    private readonly int[] _perimeterCell;

    private TileState[] _tiles;
    private List<Edges> _seams = new();
    private int _arrived;

    /// <summary>Node levels from the last solve: with the generated area's edge draining, and without.</summary>
    private int[] _lower;
    private int[] _upper;

    /// <summary>Per region chunk: null until released, then <see cref="DryChunk"/> or the lake top of each column.</summary>
    private readonly short[][] _chunks;
    private int _released;

    private sealed class TileState
    {
        public short[] Z;
        public short[] Fill;
        public short[] Label;
        public int[] PassA, PassB, PassW;
    }

    private readonly struct Edges
    {
        public readonly int[] A, B, W;
        public Edges(int[] a, int[] b, int[] w) { A = a; B = b; W = w; }
    }

    /// <param name="tileSize">Tile edge in blocks, a multiple of 32.</param>
    /// <param name="regionTiles">Region edge in tiles.</param>
    /// <param name="drainBelow">Columns lower than this are sea and drain.</param>
    public WaterRegion(int tileSize, int regionTiles, int drainBelow)
    {
        if (tileSize < 2 || tileSize % ChunkSize != 0) throw new ArgumentException("Tile size must be a multiple of 32.");
        if (4 * tileSize - 4 >= short.MaxValue) throw new ArgumentException("Tile size too large.");
        if (regionTiles < 1) throw new ArgumentException("A region needs at least one tile.");

        _t = tileSize;
        _s = regionTiles;
        _ne = 4 * tileSize - 4;
        _chunksPerTile = tileSize / ChunkSize;
        _chunksPerSide = regionTiles * _chunksPerTile;
        DrainBelow = drainBelow;
        _perimeterCell = PerimeterCells(tileSize);
        _tiles = new TileState[regionTiles * regionTiles];
        _chunks = new short[_chunksPerSide * _chunksPerSide][];
    }

    public int DrainBelow { get; }

    public int TileSize => _t;

    public int RegionTiles => _s;

    public int ArrivedTiles => _arrived;

    public bool Complete => _arrived == _s * _s;

    public int ReleasedChunks => _released;

    public int ChunkCount => _chunks.Length;

    /// <summary>Set when the region completes: whether its water equals one fill of the whole region.</summary>
    public bool? Verified { get; private set; }

    /// <summary>Cells that disagreed in that check (0 when it passed).</summary>
    public long VerifyMismatches { get; private set; }

    public bool HasTile(int tileX, int tileZ) => _tiles == null || _tiles[tileZ * _s + tileX] != null;

    /// <summary>Approximate heap held, in bytes. Safe to read without the caller's lock; then approximate.</summary>
    public long ApproxBytes
    {
        get
        {
            long bytes = (long)_chunks.Length * 8;
            foreach (short[] chunk in _chunks) if (chunk != null) bytes += chunk.Length * sizeof(short);

            TileState[] tiles = _tiles;
            List<Edges> seams = _seams;
            int[] lower = _lower;
            if (tiles == null) return bytes;
            foreach (TileState tile in tiles)
            {
                if (tile == null) continue;
                bytes += (long)_t * _t * 3 * sizeof(short);
                if (tile.PassA != null) bytes += (long)tile.PassA.Length * 12;
            }
            if (seams != null) bytes += (long)seams.Count * 3 * _t * 12;
            if (lower != null) bytes += 2L * lower.Length * sizeof(int);
            return bytes;
        }
    }

    /// <summary>
    /// Adds tile (tileX, tileZ) of this region, given as block heights (rows along Z, columns along X).
    /// Heights must already be raised to at least <c>DrainBelow - 1</c> where the sea stands. Returns
    /// false if the tile was already here.
    /// </summary>
    public bool AddTile(int tileX, int tileZ, short[] z)
    {
        if (tileX < 0 || tileZ < 0 || tileX >= _s || tileZ >= _s) throw new ArgumentOutOfRangeException(nameof(tileX));
        if (z.Length != _t * _t) throw new ArgumentException("Wrong tile size.", nameof(z));
        if (HasTile(tileX, tileZ)) return false;

        int slot = tileZ * _s + tileX;
        var tile = new TileState { Z = z };
        FloodTile(tile, tileX, tileZ);
        _tiles[slot] = tile;
        _arrived++;

        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dz == 0) continue;
            int nx = tileX + dx, nz = tileZ + dz;
            if (nx < 0 || nz < 0 || nx >= _s || nz >= _s || _tiles[nz * _s + nx] == null) continue;
            _seams.Add(Seam(tileX, tileZ, dx, dz));
        }

        Solve();

        if (Complete)
        {
            Verify();
            // Everything is final: only the released chunks are needed from here on.
            _tiles = null;
            _seams = null;
            _lower = _upper = null;
        }
        return true;
    }

    /// <summary>
    /// The released water of chunk (chunkX, chunkZ) of this region: <see cref="DryChunk"/>, or the top
    /// water block of each column (index lz * 32 + lx; 0 where the column is dry). False while the
    /// chunk is not final.
    /// </summary>
    public bool TryGetChunk(int chunkX, int chunkZ, out short[] lakeTop)
    {
        lakeTop = _chunks[chunkZ * _chunksPerSide + chunkX];
        return lakeTop != null;
    }

    /// <summary>
    /// The water a chunk has so far, released or not: the lower bound where it is not final yet.
    /// Null if its tile is not here.
    /// </summary>
    public short[] EstimateChunk(int chunkX, int chunkZ)
    {
        if (TryGetChunk(chunkX, chunkZ, out short[] released)) return released;
        int tileX = chunkX / _chunksPerTile, tileZ = chunkZ / _chunksPerTile;
        TileState tile = _tiles?[tileZ * _s + tileX];
        if (tile == null || _lower == null) return null;

        int slot = tileZ * _s + tileX;
        var top = new short[ChunkSize * ChunkSize];
        bool wet = false;
        int x0 = (chunkX % _chunksPerTile) * ChunkSize, z0 = (chunkZ % _chunksPerTile) * ChunkSize;
        for (int lz = 0; lz < ChunkSize; lz++)
        for (int lx = 0; lx < ChunkSize; lx++)
        {
            int cell = (z0 + lz) * _t + x0 + lx;
            int lo = Level(tile, slot, cell, _lower);
            if (lo != int.MaxValue && lo > tile.Z[cell]) { top[lz * ChunkSize + lx] = (short)lo; wet = true; }
        }
        return wet ? top : DryChunk;
    }

    // ---------- per tile: local flood ----------

    /// <summary>
    /// Priority flood of one tile from its drains (label 0) and its perimeter cells (label k + 1 for
    /// perimeter cell k). Gives each cell its fill level and the outlet it leaves the tile by, and the
    /// lowest pass between each pair of outlets. Heights are whole blocks, so a bucket queue serves.
    /// </summary>
    private void FloodTile(TileState tile, int tileX, int tileZ)
    {
        int t = _t, n = t * t;
        short[] z = tile.Z;
        var level = new short[n];
        var label = new int[n];
        Array.Fill(label, -1);

        bool ringN = tileZ == 0, ringS = tileZ == _s - 1, ringW = tileX == 0, ringE = tileX == _s - 1;

        int min = int.MaxValue, max = int.MinValue;
        foreach (short h in z)
        {
            if (h < min) min = h;
            if (h > max) max = h;
        }
        var queue = new BucketQueue(min, max, n);

        for (int k = 0; k < _ne; k++)
        {
            int cell = _perimeterCell[k];
            int row = cell / t, col = cell % t;
            bool ring = (ringN && row == 0) || (ringS && row == t - 1) || (ringW && col == 0) || (ringE && col == t - 1);
            label[cell] = ring || z[cell] < DrainBelow ? 0 : k + 1;
        }
        for (int cell = 0; cell < n; cell++)
        {
            if (label[cell] < 0 && z[cell] < DrainBelow) label[cell] = 0;
            if (label[cell] < 0) continue;
            level[cell] = z[cell];
            queue.Push(z[cell], cell);
        }

        // Lowest pass per unordered label pair. Labels only differ along the lines where outlets'
        // floods meet, so this sees a few thousand entries per tile.
        var passes = new Dictionary<long, int>();
        long pairBase = _ne + 1;

        while (queue.TryPop(out int cell))
        {
            int row = cell / t, col = cell % t;
            int lab = label[cell], lvc = level[cell];
            for (int dz = -1; dz <= 1; dz++)
            {
                int nr = row + dz;
                if (nr < 0 || nr >= t) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nc = col + dx;
                    if (nc < 0 || nc >= t) continue;
                    int next = nr * t + nc;
                    int ln = label[next];
                    if (ln < 0)
                    {
                        label[next] = lab;
                        int v = Math.Max(z[next], lvc);
                        level[next] = (short)v;
                        queue.Push(v, next);
                    }
                    else if (ln != lab)
                    {
                        long key = Math.Min(lab, ln) * pairBase + Math.Max(lab, ln);
                        int w = Math.Max(lvc, level[next]);
                        if (!passes.TryGetValue(key, out int old) || w < old) passes[key] = w;
                    }
                }
            }
        }

        tile.Fill = level;
        tile.Label = new short[n];
        for (int i = 0; i < n; i++) tile.Label[i] = (short)label[i];

        int slot = tileZ * _s + tileX;
        tile.PassA = new int[passes.Count];
        tile.PassB = new int[passes.Count];
        tile.PassW = new int[passes.Count];
        int e = 0;
        foreach (KeyValuePair<long, int> pass in passes)
        {
            tile.PassA[e] = Node(slot, (int)(pass.Key / pairBase));
            tile.PassB[e] = Node(slot, (int)(pass.Key % pairBase));
            tile.PassW[e] = pass.Value;
            e++;
        }
    }

    /// <summary>Perimeter cells of a t x t tile, clockwise from the top-left corner.</summary>
    private static int[] PerimeterCells(int t)
    {
        var cells = new int[4 * t - 4];
        int k = 0;
        for (int x = 0; x < t; x++) cells[k++] = x;                         // top row, left to right
        for (int y = 1; y < t - 1; y++) cells[k++] = y * t + t - 1;         // right column, downwards
        for (int x = t - 1; x >= 0; x--) cells[k++] = (t - 1) * t + x;      // bottom row, right to left
        for (int y = t - 2; y >= 1; y--) cells[k++] = y * t;                // left column, upwards
        return cells;
    }

    /// <summary>Graph node of a tile's local label; 0 is the drain.</summary>
    private int Node(int slot, int label) => label == 0 ? 0 : slot * _ne + label;

    // ---------- per region: outlet graph ----------

    /// <summary>
    /// Edges between tile (tileX, tileZ) and its arrived neighbour in direction (dx, dz): every pair
    /// of 8-adjacent cells across the seam, weighted by the higher of the two.
    /// </summary>
    private Edges Seam(int tileX, int tileZ, int dx, int dz)
    {
        int t = _t;
        int slotA = tileZ * _s + tileX, slotB = (tileZ + dz) * _s + tileX + dx;
        TileState a = _tiles[slotA], b = _tiles[slotB];
        var ea = new List<int>(3 * t);
        var eb = new List<int>(3 * t);
        var ew = new List<int>(3 * t);

        void Pair(int cellA, int cellB)
        {
            int na = Node(slotA, a.Label[cellA]), nb = Node(slotB, b.Label[cellB]);
            if (na == 0 && nb == 0) return;
            ea.Add(na);
            eb.Add(nb);
            ew.Add(Math.Max(a.Z[cellA], b.Z[cellB]));
        }

        if (dx != 0 && dz != 0)
        {
            int rowA = dz < 0 ? 0 : t - 1, colA = dx < 0 ? 0 : t - 1;
            Pair(rowA * t + colA, (t - 1 - rowA) * t + t - 1 - colA);
        }
        else if (dz == 0)
        {
            int colA = dx < 0 ? 0 : t - 1, colB = t - 1 - colA;
            for (int r = 0; r < t; r++)
            for (int r2 = Math.Max(0, r - 1); r2 <= Math.Min(t - 1, r + 1); r2++)
                Pair(r * t + colA, r2 * t + colB);
        }
        else
        {
            int rowA = dz < 0 ? 0 : t - 1, rowB = t - 1 - rowA;
            for (int c = 0; c < t; c++)
            for (int c2 = Math.Max(0, c - 1); c2 <= Math.Min(t - 1, c + 1); c2++)
                Pair(rowA * t + c, rowB * t + c2);
        }

        return new Edges(ea.ToArray(), eb.ToArray(), ew.ToArray());
    }

    /// <summary>
    /// Lower and upper levels for every perimeter node, then the chunks that became final. Rebuilt
    /// from scratch on each arrival: a few tens of thousands of edges.
    /// </summary>
    private void Solve()
    {
        int baseCount = 0;
        foreach (TileState tile in _tiles) if (tile != null) baseCount += tile.PassA.Length;
        foreach (Edges e in _seams) baseCount += e.A.Length;

        var fa = new List<int>();
        var fw = new List<int>();
        CollectFrontier(fa, fw);

        int total = baseCount + fa.Count;
        var ea = new int[total];
        var eb = new int[total];
        var ew = new int[total];
        int i = 0;
        foreach (TileState tile in _tiles)
        {
            if (tile == null) continue;
            Array.Copy(tile.PassA, 0, ea, i, tile.PassA.Length);
            Array.Copy(tile.PassB, 0, eb, i, tile.PassB.Length);
            Array.Copy(tile.PassW, 0, ew, i, tile.PassW.Length);
            i += tile.PassA.Length;
        }
        foreach (Edges e in _seams)
        {
            Array.Copy(e.A, 0, ea, i, e.A.Length);
            Array.Copy(e.B, 0, eb, i, e.B.Length);
            Array.Copy(e.W, 0, ew, i, e.W.Length);
            i += e.A.Length;
        }
        for (int f = 0; f < fa.Count; f++, i++)
        {
            ea[i] = fa[f];
            eb[i] = 0;
            ew[i] = fw[f];
        }

        int nodes = _s * _s * _ne + 1;
        _upper = MinimaxLevels(nodes, ea, eb, ew, baseCount);
        _lower = MinimaxLevels(nodes, ea, eb, ew, total);

        for (int slot = 0; slot < _tiles.Length; slot++)
        {
            if (_tiles[slot] != null) Release(slot);
        }
    }

    /// <summary>Edges to the drain from perimeter cells facing a tile of this region that is not here yet.</summary>
    private void CollectFrontier(List<int> nodes, List<int> weights)
    {
        int t = _t;
        for (int tileZ = 0; tileZ < _s; tileZ++)
        for (int tileX = 0; tileX < _s; tileX++)
        {
            int slot = tileZ * _s + tileX;
            TileState tile = _tiles[slot];
            if (tile == null) continue;

            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                int nx = tileX + dx, nz = tileZ + dz;
                if (nx < 0 || nz < 0 || nx >= _s || nz >= _s || _tiles[nz * _s + nx] != null) continue;

                void Add(int cell)
                {
                    int node = Node(slot, tile.Label[cell]);
                    if (node == 0) return;
                    nodes.Add(node);
                    weights.Add(tile.Z[cell]);
                }

                if (dx != 0 && dz != 0)
                {
                    Add((dz < 0 ? 0 : t - 1) * t + (dx < 0 ? 0 : t - 1));
                }
                else if (dz == 0)
                {
                    int col = dx < 0 ? 0 : t - 1;
                    for (int r = 0; r < t; r++) Add(r * t + col);
                }
                else
                {
                    int row = dz < 0 ? 0 : t - 1;
                    for (int c = 0; c < t; c++) Add(row * t + c);
                }
            }
        }
    }

    /// <summary>
    /// For each node, the lowest possible highest edge weight on a path to node 0 (the drain), using
    /// the first <paramref name="count"/> edges. Kruskal: edges join components in order of weight,
    /// and a component's nodes get the weight at which it joins the drain's. The drain's root never
    /// moves, so a node's level is the join weight on its first ancestor that has one.
    /// int.MinValue for the drain itself, int.MaxValue where there is no path.
    /// </summary>
    internal static int[] MinimaxLevels(int nodes, int[] ea, int[] eb, int[] ew, int count)
    {
        var order = new long[count];
        for (int i = 0; i < count; i++) order[i] = ((long)ew[i] << 32) | (uint)i;
        Array.Sort(order);

        var parent = new int[nodes];
        var size = new int[nodes];
        var joined = new int[nodes];
        for (int v = 0; v < nodes; v++)
        {
            parent[v] = v;
            size[v] = 1;
            joined[v] = int.MaxValue;
        }

        foreach (long key in order)
        {
            int idx = (int)(key & 0xFFFFFFFF);
            int a = ea[idx];
            while (parent[a] != a) a = parent[a];
            int b = eb[idx];
            while (parent[b] != b) b = parent[b];
            if (a == b) continue;

            if (a == 0 || b == 0)
            {
                int other = a == 0 ? b : a;
                joined[other] = ew[idx];
                parent[other] = 0;
            }
            else
            {
                if (size[a] < size[b]) (a, b) = (b, a);
                parent[b] = a;
                size[a] += size[b];
            }
        }

        var level = new int[nodes];
        level[0] = int.MinValue;
        for (int v = 1; v < nodes; v++)
        {
            int u = v;
            while (true)
            {
                if (joined[u] != int.MaxValue) { level[v] = joined[u]; break; }
                if (parent[u] == u) { level[v] = int.MaxValue; break; }
                u = parent[u];
            }
        }
        return level;
    }

    /// <summary>A cell's water surface against one set of node levels.</summary>
    private int Level(TileState tile, int slot, int cell, int[] nodeLevels)
    {
        int fill = tile.Fill[cell];
        int lab = tile.Label[cell];
        return lab == 0 ? fill : Math.Max(fill, nodeLevels[slot * _ne + lab]);
    }

    /// <summary>Releases the chunks of one tile whose every cell is final.</summary>
    private void Release(int slot)
    {
        TileState tile = _tiles[slot];
        int tileX = slot % _s, tileZ = slot / _s;
        for (int cz = 0; cz < _chunksPerTile; cz++)
        for (int cx = 0; cx < _chunksPerTile; cx++)
        {
            int chunk = (tileZ * _chunksPerTile + cz) * _chunksPerSide + tileX * _chunksPerTile + cx;
            if (_chunks[chunk] != null) continue;

            short[] top = null;
            bool final = true;
            for (int lz = 0; lz < ChunkSize && final; lz++)
            for (int lx = 0; lx < ChunkSize; lx++)
            {
                int cell = (cz * ChunkSize + lz) * _t + cx * ChunkSize + lx;
                int lo = Level(tile, slot, cell, _lower);
                if (lo != Level(tile, slot, cell, _upper)) { final = false; break; }
                if (lo > tile.Z[cell])
                {
                    top ??= new short[ChunkSize * ChunkSize];
                    top[lz * ChunkSize + lx] = (short)lo;
                }
            }

            if (!final) continue;
            _chunks[chunk] = top ?? DryChunk;
            _released++;
        }
    }

    // ---------- check on completion ----------

    /// <summary>
    /// Compares the finished region with one priority flood of the whole region (ring and sea as
    /// outlets): every chunk must be released, with exactly that water.
    /// </summary>
    private void Verify()
    {
        int t = _t, w = _s * t, n = w * w;
        var z = new short[n];
        int min = int.MaxValue, max = int.MinValue;
        for (int slot = 0; slot < _tiles.Length; slot++)
        {
            TileState tile = _tiles[slot];
            int x0 = (slot % _s) * t, z0 = (slot / _s) * t;
            for (int r = 0; r < t; r++)
            {
                Array.Copy(tile.Z, r * t, z, (z0 + r) * w + x0, t);
            }
        }
        foreach (short h in z)
        {
            if (h < min) min = h;
            if (h > max) max = h;
        }

        var level = new short[n];
        var done = new bool[n];
        var queue = new BucketQueue(min, max, n);
        for (int cell = 0; cell < n; cell++)
        {
            int r = cell / w, c = cell % w;
            if (r == 0 || c == 0 || r == w - 1 || c == w - 1 || z[cell] < DrainBelow)
            {
                done[cell] = true;
                level[cell] = z[cell];
                queue.Push(z[cell], cell);
            }
        }
        while (queue.TryPop(out int cell))
        {
            int r = cell / w, c = cell % w, lv = level[cell];
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nr = r + dz, nc = c + dx;
                if (nr < 0 || nc < 0 || nr >= w || nc >= w) continue;
                int next = nr * w + nc;
                if (done[next]) continue;
                done[next] = true;
                int v = Math.Max(z[next], lv);
                level[next] = (short)v;
                queue.Push(v, next);
            }
        }

        long mismatches = 0;
        for (int cz = 0; cz < _chunksPerSide; cz++)
        for (int cx = 0; cx < _chunksPerSide; cx++)
        {
            short[] top = _chunks[cz * _chunksPerSide + cx];
            for (int lz = 0; lz < ChunkSize; lz++)
            for (int lx = 0; lx < ChunkSize; lx++)
            {
                int cell = (cz * ChunkSize + lz) * w + cx * ChunkSize + lx;
                int expected = level[cell] > z[cell] ? level[cell] : 0;
                int got = top == null ? -1 : top.Length == 0 ? 0 : top[lz * ChunkSize + lx];
                if (got != expected) mismatches++;
            }
        }

        VerifyMismatches = mismatches;
        Verified = mismatches == 0;
    }

    /// <summary>
    /// Min-priority queue over whole-block heights. Pushes never go below the level being popped,
    /// which is all a priority flood needs.
    /// </summary>
    private sealed class BucketQueue
    {
        private readonly int _min;
        private readonly int[] _head;
        private readonly int[] _next;
        private int _current;

        public BucketQueue(int min, int max, int cells)
        {
            _min = min;
            _head = new int[Math.Max(1, max - min + 1)];
            Array.Fill(_head, -1);
            _next = new int[cells];
            _current = 0;
        }

        public void Push(int level, int cell)
        {
            int b = level - _min;
            _next[cell] = _head[b];
            _head[b] = cell;
            if (b < _current) _current = b;
        }

        public bool TryPop(out int cell)
        {
            while (_current < _head.Length && _head[_current] < 0) _current++;
            if (_current >= _head.Length) { cell = -1; return false; }
            cell = _head[_current];
            _head[_current] = _next[cell];
            return true;
        }
    }
}
