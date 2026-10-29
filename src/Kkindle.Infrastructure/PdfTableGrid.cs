namespace Kkindle.Infrastructure;

/// <summary>Closed cells formed by connected horizontal and vertical PDF rules.</summary>
internal sealed class PdfTableGrid
{
    internal sealed record Cell(PdfTableGrid Table, PdfTextBounds Bounds);
    public List<Cell> Cells { get; } = [];

    public static IReadOnlyList<PdfTableGrid> Read(PdfPageContent page)
    {
        var dx = 0.75 / page.Width;
        var dy = 0.75 / page.Height;
        var horizontal = page.Rulings.Where(r => r.Height <= dy && r.Width > dx * 4).ToArray();
        var vertical = page.Rulings.Where(r => r.Width <= dx && r.Height > dy * 4).ToArray();
        var unvisited = horizontal.ToHashSet();
        var result = new List<PdfTableGrid>();
        bool Intersects(PdfTextBounds h, PdfTextBounds v) => v.X >= h.X - dx && v.X <= h.X + h.Width + dx
            && h.Y >= v.Y - dy && h.Y <= v.Y + v.Height + dy;
        while (unvisited.Count > 0)
        {
            var rows = new HashSet<PdfTextBounds> { unvisited.First() };
            var columns = new HashSet<PdfTextBounds>();
            var pending = new Queue<PdfTextBounds>(rows);
            while (pending.TryDequeue(out var row))
            {
                unvisited.Remove(row);
                foreach (var column in vertical.Where(v => Intersects(row, v)))
                {
                    if (!columns.Add(column)) continue;
                    foreach (var neighbor in unvisited.Where(h => Intersects(h, column)).ToArray())
                        if (rows.Add(neighbor)) pending.Enqueue(neighbor);
                }
            }
            var ys = Cluster(rows.Select(r => r.Y), dy);
            if (ys.Length < 3 || Cluster(columns.Select(c => c.X), dx).Length < 3) continue;
            var table = new PdfTableGrid();
            for (var y = 0; y < ys.Length - 1; y++)
            {
                var top = ys[y];
                var bottom = ys[y + 1];
                var xs = Cluster(columns.Where(c => c.Y <= top + dy && c.Y + c.Height >= bottom - dy).Select(c => c.X), dx);
                for (var x = 0; x < xs.Length - 1; x++)
                {
                    var left = xs[x];
                    var right = xs[x + 1];
                    bool Covered(double edge) => rows.Any(r => Math.Abs(r.Y - edge) <= dy
                        && r.X <= left + dx && r.X + r.Width >= right - dx);
                    if (Covered(top) && Covered(bottom)) table.Cells.Add(new(table, new(left, top, right - left, bottom - top)));
                }
            }
            if (table.Cells.Count >= 4) result.Add(table);
        }
        return result;
    }

    private static double[] Cluster(IEnumerable<double> values, double tolerance)
    {
        var result = new List<double>();
        foreach (var value in values.Order())
            if (result.Count == 0 || value - result[^1] > tolerance) result.Add(value);
        return [.. result];
    }
}
