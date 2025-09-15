using System;
using System.Collections.Generic;
using System.Linq;

namespace ReadPdf
{
    /// <summary>
    /// Normaliseert de output van PdfTableTextExtractor naar een lineaire lijst van items (line of table).
    /// - Input: List<PdfTableTextExtractor.PageOutput>
    /// - Output: List<DocumentItem> waarbij elk item ofwel een losse tekstregel is, of een complete tabel (2D array strings).
    ///
    /// Heuristiek:
    /// 1) Groepeer cel-blokken (page.Blocks) per pagina tot tabellen via rechthoek-adjacentie (delen randen/overlap).
    /// 2) Binnen elke tabel: bepaal unieke X- en Y-randen, bouw cell-grid, vul met cel-tekst (centrum-in-cel).
    /// 3) Losse regels (page.Lines) blijven individuele line-items.
    /// 4) Sorteer items per pagina in leesvolgorde (top→bottom, dan links→rechts), behoud paginanummer.
    ///
    /// NB: Deze klasse gaat niet om met row/col-spans (samengevoegde cellen). Lege cellen blijven "".
    /// </summary>
    public class DocumentNormalizer
    {
        // Toleranties voor grouping & grids
        private const int EdgeTol = 2;           // maximale afstand (pt) om randen als gelijk/aanliggend te zien
        private const int OverlapTol = 1;        // minimale overlap (pt) op de andere as om adjacent te vinden
        private const int ClusterTol = 1;        // cluster-randen tot unieke X/Y sets

        public List<DocumentItem> Build(List<PdfTableTextExtractor.PageOutput> pages)
        {
            if (pages == null) throw new ArgumentNullException(nameof(pages));
            var items = new List<DocumentItem>();

            foreach (var page in pages)
            {
                // 1) Lines → items
                for (int i = 0; i < page.Lines.Count; i++)
                {
                    var l = page.Lines[i];
                    items.Add(new DocumentItem
                    {
                        Kind = "line",
                        Page = page.Page,
                        X = l.X,
                        Y = l.Y,
                        Width = l.Width,
                        Height = l.Height,
                        Line = new LineItem { Text = l.Text }
                    });
                }

                // 2) Blocks → tables (cluster in componenten)
                var tables = GroupBlocksIntoTables(page.Blocks);
                for (int t = 0; t < tables.Count; t++)
                {
                    var group = tables[t];
                    var tableItem = BuildTableFromBlocks(group);

                    items.Add(new DocumentItem
                    {
                        Kind = "table",
                        Page = page.Page,
                        X = tableItem.X,
                        Y = tableItem.Y,
                        Width = tableItem.Width,
                        Height = tableItem.Height,
                        Table = new TableItem { Cells = tableItem.Cells }
                    });
                }
            }

            // 3) Sorteer per pagina, daarna overall volgorde behouden (page ascending)
            items = items
                .OrderBy(it => it.Page)
                .ThenByDescending(it => it.Y + it.Height) // top eerst (PDF origin is linksonder)
                .ThenBy(it => it.X)
                .ToList();

            return items;
        }

        // --------- Tables from blocks ---------
        private class IntRect { public int X, Y, W, H; }
        private class BlockRect { public IntRect R; public string Text; }

        private List<List<BlockRect>> GroupBlocksIntoTables(List<PdfTableTextExtractor.BlockOutput> blocks)
        {
            // Maak interne rects
            var list = new List<BlockRect>();
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                list.Add(new BlockRect { R = new IntRect { X = b.X, Y = b.Y, W = b.Width, H = b.Height }, Text = b.Text });
            }
            var adj = BuildAdjacency(list);
            var comps = ConnectedComponents(list.Count, adj);

            var groups = new List<List<BlockRect>>();
            for (int c = 0; c < comps.Count; c++)
            {
                var grp = new List<BlockRect>();
                var idxs = comps[c];
                for (int k = 0; k < idxs.Count; k++) grp.Add(list[idxs[k]]);
                // filter heuristiek: houd alleen groepen met >= 2 cellen
                if (grp.Count >= 2) groups.Add(grp);
            }
            return groups;
        }

        private List<List<int>> BuildAdjacency(List<BlockRect> list)
        {
            int n = list.Count;
            var adj = new List<List<int>>(n);
            for (int i = 0; i < n; i++) adj.Add(new List<int>());

            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (AreAdjacent(list[i].R, list[j].R))
                    {
                        adj[i].Add(j);
                        adj[j].Add(i);
                    }
                }
            return adj;
        }

        private bool AreAdjacent(IntRect a, IntRect b)
        {
            int aLeft = a.X, aRight = a.X + a.W, aBottom = a.Y, aTop = a.Y + a.H;
            int bLeft = b.X, bRight = b.X + b.W, bBottom = b.Y, bTop = b.Y + b.H;

            // Overlap functie
            int Overlap1D(int s1, int e1, int s2, int e2)
            {
                int lo = Math.Max(s1, s2);
                int hi = Math.Min(e1, e2);
                return Math.Max(0, hi - lo);
            }

            // Horizontaal aangrenzend (rechts naast/links naast) met voldoende verticale overlap
            bool horizAdj = (Math.Abs(aRight - bLeft) <= EdgeTol || Math.Abs(bRight - aLeft) <= EdgeTol)
                            && Overlap1D(aBottom, aTop, bBottom, bTop) > OverlapTol;

            // Verticaal aangrenzend (boven/onder) met voldoende horizontale overlap
            bool vertAdj = (Math.Abs(aTop - bBottom) <= EdgeTol || Math.Abs(bTop - aBottom) <= EdgeTol)
                           && Overlap1D(aLeft, aRight, bLeft, bRight) > OverlapTol;

            // Of overlappend (om hiaten in lijn-detectie te overbruggen)
            bool overlap = Overlap1D(aLeft, aRight, bLeft, bRight) > 0 && Overlap1D(aBottom, aTop, bBottom, bTop) > 0;

            return horizAdj || vertAdj || overlap;
        }

        private List<List<int>> ConnectedComponents(int n, List<List<int>> adj)
        {
            var res = new List<List<int>>();
            var seen = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (seen[i]) continue;
                var comp = new List<int>();
                var stack = new Stack<int>();
                stack.Push(i); seen[i] = true;
                while (stack.Count > 0)
                {
                    int u = stack.Pop();
                    comp.Add(u);
                    var nu = adj[u];
                    for (int k = 0; k < nu.Count; k++)
                    {
                        int v = nu[k];
                        if (!seen[v]) { seen[v] = true; stack.Push(v); }
                    }
                }
                res.Add(comp);
            }
            return res;
        }

        private TableBuildResult BuildTableFromBlocks(List<BlockRect> blocks)
        {
            // Unieke kolom- en rij-randen clusteren
            var xsRaw = new List<int>();
            var ysRaw = new List<int>();
            for (int i = 0; i < blocks.Count; i++)
            {
                var r = blocks[i].R;
                xsRaw.Add(r.X); xsRaw.Add(r.X + r.W);
                ysRaw.Add(r.Y); ysRaw.Add(r.Y + r.H);
            }
            var xs = ClusterPositions(xsRaw, ClusterTol);
            var ys = ClusterPositions(ysRaw, ClusterTol);
            xs.Sort(); ys.Sort();

            int cols = Math.Max(0, xs.Count - 1);
            int rows = Math.Max(0, ys.Count - 1);
            var cells = new List<List<string>>();
            for (int r = 0; r < rows; r++)
            {
                var row = new List<string>();
                for (int c = 0; c < cols; c++) row.Add(string.Empty);
                cells.Add(row);
            }

            // Vul cellen: neem center van elk block en zet in corresponderende gridcel (concat met newline als meerdere)
            for (int i = 0; i < blocks.Count; i++)
            {
                var r = blocks[i].R;
                int cx = r.X + r.W / 2;
                int cy = r.Y + r.H / 2;
                int col = FindBand(cx, xs);
                int row = FindBand(cy, ys);
                if (row >= 0 && row < rows && col >= 0 && col < cols)
                {
                    var txt = blocks[i].Text ?? string.Empty;
                    if (string.IsNullOrEmpty(cells[row][col])) cells[row][col] = txt;
                    else cells[row][col] = cells[row][col] + "\n" + txt;
                }
            }

            // BBox van de hele tabel
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int i = 0; i < blocks.Count; i++)
            {
                var r = blocks[i].R;
                if (r.X < minX) minX = r.X;
                if (r.Y < minY) minY = r.Y;
                if (r.X + r.W > maxX) maxX = r.X + r.W;
                if (r.Y + r.H > maxY) maxY = r.Y + r.H;
            }

            cells.Reverse();
            var result = new TableBuildResult
            {
                X = minX,
                Y = minY,
                Width = Math.Max(0, maxX - minX),
                Height = Math.Max(0, maxY - minY),
                Cells = cells
            };
            return result;
        }

        private int FindBand(int v, List<int> edges)
        {
            for (int i = 0; i < edges.Count - 1; i++)
            {
                if (v >= edges[i] && v <= edges[i + 1]) return i;
            }
            return -1;
        }

        private List<int> ClusterPositions(List<int> vals, int tol)
        {
            if (vals == null || vals.Count == 0) return new List<int>();
            vals.Sort();
            var result = new List<int>();
            int runSum = vals[0]; int runCount = 1; int runStart = vals[0];
            for (int i = 1; i < vals.Count; i++)
            {
                if (Math.Abs(vals[i] - runStart) <= tol)
                {
                    runSum += vals[i]; runCount++;
                }
                else
                {
                    result.Add((int)Math.Round((double)runSum / runCount));
                    runSum = vals[i]; runCount = 1; runStart = vals[i];
                }
            }
            result.Add((int)Math.Round((double)runSum / runCount));
            return result.Distinct().OrderBy(x => x).ToList();
        }

        private class TableBuildResult
        {
            public int X, Y, Width, Height;
            public List<List<string>> Cells;
        }

        // --------- Output DTOs ---------
        public class DocumentItem
        {
            public string Kind { get; set; } // "line" | "table"
            public int Page { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public LineItem Line { get; set; } // nullable unless Kind=="line"
            public TableItem Table { get; set; } // nullable unless Kind=="table"
        }

        public class LineItem
        {
            public string Text { get; set; }
        }

        public class TableItem
        {
            public List<List<string>> Cells { get; set; }
            public int Rows { get { return Cells == null ? 0 : Cells.Count; } }
            public int Cols { get { return (Cells == null || Cells.Count == 0) ? 0 : Cells[0].Count; } }
        }
    }
}
