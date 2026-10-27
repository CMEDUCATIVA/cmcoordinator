namespace CmConv.Lectura
{
  /// <summary>
  /// Vertex-clustering simplification for the display meshes Revit saves in the file, which can be tens of times
  /// heavier than what the viewer needs. Vertices are snapped to a grid sized from the element's extent; the grid
  /// coarsens until the element fits its triangle budget. Degenerate and repeated triangles are dropped.
  /// </summary>
  public static class Simplificador
  {
    public static List<RvtMesh> ToBudget(List<RvtMesh> meshes, int maxTriangles)
    {
      meshes = meshes.Select(Lossless).Where(m => m != null).ToList();
      var total = meshes.Sum(m => m.Indices.Length / 3);
      if (total <= maxTriangles) return meshes;
      var (min, max) = Bounds(meshes);
      var extent = Math.Max(max[0] - min[0], Math.Max(max[1] - min[1], max[2] - min[2]));
      if (!(extent > 0)) return meshes;
      // Start fine and coarsen: 1/400 of the element's largest dimension, never below 2 mm.
      for (var cell = Math.Max(extent / 400, 0.002); cell <= extent / 4; cell *= 1.6)
      {
        var simplified = meshes.Select(m => Cluster(m, min, cell)).Where(m => m != null).ToList();
        if (simplified.Sum(m => m.Indices.Length / 3) <= maxTriangles) return simplified;
      }
      return meshes.Select(m => Cluster(m, min, extent / 4)).Where(m => m != null).ToList();
    }

    /// <summary>Distance (m) a vertex may move off the planes of its faces and still count as lying on them.</summary>
    private const double PlaneTolerance = 1e-4;

    /// <summary>
    /// Removes vertices that add no shape: the subdivisions inside flat faces and the bands Revit cuts along curved
    /// sides (a cylinder's crease lines). A vertex is collapsed into a neighbour only when that neighbour lies on the
    /// plane of every face around it, no face flips and the surface stays manifold, so the outline is unchanged.
    /// </summary>
    public static RvtMesh Lossless(RvtMesh mesh)
    {
      // Weld: the saved meshes repeat vertices per face for their normals.
      var weld = new Dictionary<(long, long, long), int>();
      var points = new List<double[]>();
      var map = new int[mesh.Positions.Length / 3];
      for (var i = 0; i < map.Length; i++)
      {
        double x = mesh.Positions[i * 3], y = mesh.Positions[i * 3 + 1], z = mesh.Positions[i * 3 + 2];
        var key = ((long)Math.Round(x * 1e5), (long)Math.Round(y * 1e5), (long)Math.Round(z * 1e5));
        if (!weld.TryGetValue(key, out var index)) { index = points.Count; weld[key] = index; points.Add(new[] { x, y, z }); }
        map[i] = index;
      }
      var faces = new List<int[]>();
      var around = points.Select(_ => new HashSet<int>()).ToArray();
      // Double-sided bodies repeat every face with the opposite winding after the outward one: keep the first.
      var seen = new HashSet<(int, int, int)>();
      for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
      {
        int a = map[mesh.Indices[t]], b = map[mesh.Indices[t + 1]], c = map[mesh.Indices[t + 2]];
        if (a == b || b == c || a == c) continue;
        var sorted = new[] { a, b, c }; Array.Sort(sorted);
        if (!seen.Add((sorted[0], sorted[1], sorted[2]))) continue;
        foreach (var v in new[] { a, b, c }) around[v].Add(faces.Count);
        faces.Add(new[] { a, b, c });
      }
      var alive = new bool[faces.Count]; Array.Fill(alive, true);

      for (var changed = true; changed;)
      {
        changed = false;
        for (var v = 0; v < points.Count; v++)
        {
          if (around[v].Count == 0 || !Interior(v, faces, around)) continue;
          foreach (var u in Neighbours(v, faces, around).ToList())
          {
            if (!CanCollapse(v, u, faces, around, points)) continue;
            foreach (var f in around[v].ToList())
            {
              if (faces[f].Contains(u)) { alive[f] = false; foreach (var w in faces[f]) around[w].Remove(f); continue; }
              faces[f] = faces[f].Select(w => w == v ? u : w).ToArray();
              around[u].Add(f);
            }
            around[v].Clear();
            changed = true;
            break;
          }
        }
      }

      var used = new int[points.Count]; Array.Fill(used, -1);
      var positions = new List<double>(); var indices = new List<int>();
      for (var f = 0; f < faces.Count; f++)
      {
        if (!alive[f]) continue;
        foreach (var v in faces[f])
        {
          if (used[v] < 0) { used[v] = positions.Count / 3; positions.AddRange(points[v]); }
          indices.Add(used[v]);
        }
      }
      return indices.Count < 3 ? null : new RvtMesh { Positions = positions.ToArray(), Indices = indices.ToArray(), Color = mesh.Color, Opacity = mesh.Opacity };
    }

    private static IEnumerable<int> Neighbours(int v, List<int[]> faces, HashSet<int>[] around) =>
      around[v].SelectMany(f => faces[f]).Where(w => w != v).Distinct();

    /// <summary>Every edge from the vertex is shared by exactly two faces: not on an open border or a non-manifold seam.</summary>
    private static bool Interior(int v, List<int[]> faces, HashSet<int>[] around) =>
      Neighbours(v, faces, around).All(w => around[v].Count(f => faces[f].Contains(w)) == 2);

    private static bool CanCollapse(int v, int u, List<int[]> faces, HashSet<int>[] around, List<double[]> points)
    {
      // Link condition: the edge's two faces are the only ones the two vertices share.
      if (Neighbours(v, faces, around).Intersect(Neighbours(u, faces, around)).Count() != 2) return false;
      foreach (var f in around[v])
      {
        if (faces[f].Contains(u)) continue;
        var before = Normal(faces[f].Select(w => points[w]).ToArray());
        if (before == null) return false;
        var p = points[faces[f][0] == v ? faces[f][1] : faces[f][0]];
        if (Math.Abs(Dot(before, Sub(points[u], p))) > PlaneTolerance) return false;
        var after = Normal(faces[f].Select(w => points[w == v ? u : w]).ToArray());
        if (after == null || Dot(before, after) < 0.999) return false;
      }
      return true;
    }

    private static double[] Normal(double[][] t)
    {
      var a = Sub(t[1], t[0]); var b = Sub(t[2], t[0]);
      var n = new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
      var length = Math.Sqrt(Dot(n, n));
      return length < 1e-12 ? null : n.Select(c => c / length).ToArray();
    }

    private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    private static RvtMesh Cluster(RvtMesh mesh, double[] origin, double cell)
    {
      var vertexCount = mesh.Positions.Length / 3;
      var cellOf = new int[vertexCount];
      var cells = new Dictionary<(long, long, long), int>();
      var sums = new List<double[]>();
      for (var i = 0; i < vertexCount; i++)
      {
        var key = ((long)Math.Floor((mesh.Positions[i * 3] - origin[0]) / cell),
                   (long)Math.Floor((mesh.Positions[i * 3 + 1] - origin[1]) / cell),
                   (long)Math.Floor((mesh.Positions[i * 3 + 2] - origin[2]) / cell));
        if (!cells.TryGetValue(key, out var index)) { index = sums.Count; cells[key] = index; sums.Add(new double[4]); }
        var s = sums[index];
        s[0] += mesh.Positions[i * 3]; s[1] += mesh.Positions[i * 3 + 1]; s[2] += mesh.Positions[i * 3 + 2]; s[3]++;
        cellOf[i] = index;
      }
      var triangles = new List<int>();
      var seen = new HashSet<(int, int, int)>();
      for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
      {
        int a = cellOf[mesh.Indices[t]], b = cellOf[mesh.Indices[t + 1]], c = cellOf[mesh.Indices[t + 2]];
        if (a == b || b == c || a == c) continue;
        // Same three cells in any rotation are the same face.
        var key = a < b && a < c ? (a, b, c) : b < c ? (b, c, a) : (c, a, b);
        if (seen.Add(key)) { triangles.Add(a); triangles.Add(b); triangles.Add(c); }
      }
      if (triangles.Count < 3) return null;
      var positions = new double[sums.Count * 3];
      for (var i = 0; i < sums.Count; i++)
        for (var axis = 0; axis < 3; axis++) positions[i * 3 + axis] = Math.Round(sums[i][axis] / sums[i][3], 4);
      // Drop vertices no triangle uses.
      var used = new int[sums.Count]; Array.Fill(used, -1);
      var compact = new List<double>(); var indices = new int[triangles.Count];
      for (var i = 0; i < triangles.Count; i++)
      {
        var v = triangles[i];
        if (used[v] < 0) { used[v] = compact.Count / 3; compact.Add(positions[v * 3]); compact.Add(positions[v * 3 + 1]); compact.Add(positions[v * 3 + 2]); }
        indices[i] = used[v];
      }
      return new RvtMesh { Positions = compact.ToArray(), Indices = indices, Color = mesh.Color, Opacity = mesh.Opacity };
    }

    private static (double[] Min, double[] Max) Bounds(List<RvtMesh> meshes)
    {
      var min = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
      var max = new[] { double.MinValue, double.MinValue, double.MinValue };
      foreach (var m in meshes)
        for (var i = 0; i + 2 < m.Positions.Length; i += 3)
          for (var axis = 0; axis < 3; axis++)
          {
            min[axis] = Math.Min(min[axis], m.Positions[i + axis]);
            max[axis] = Math.Max(max[axis], m.Positions[i + axis]);
          }
      return (min, max);
    }

    /// <summary>A decoded body that is only the element's bounding box (rvt-rs draws undecoded families that way).</summary>
    public static bool IsBox(List<RvtMesh> meshes) => meshes.Count > 0 && meshes.All(m => m.Indices.Length / 3 <= 12 && m.Positions.Length / 3 <= 24);
  }
}
