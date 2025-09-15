using iText.Commons.Bouncycastle.Cert.Ocsp;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ReadPdf
{
    /// <summary>
    /// Extracts table boxes (from drawn lines) and assigns text to either box blocks or outside lines.
    /// Input: PDF filename. Output: strongly-typed C# objects with rounded integer coordinates.
    /// iText 8 (NuGet: itext)
    /// </summary>
    public class PdfTableTextExtractor
    {
        // ---------- Tunables (same defaults as your working version) ----------
        private const double LineYTol = 1.8;                 // line clustering tolerance on Y
        private const double AxisTol = 2.0;                  // |dx|<AxisTol -> vertical; |dy|<AxisTol -> horizontal
        private const double MinLineLen = 2.0;               // ignore very short path segments
        private const double PosMergeTol = 1.0;              // cluster tolerance for X/Y line positions
        private const double SegMergeGap = 1.0;              // merge intervals with small gap
        private const double CoverSlack = 1.0;               // slack for edge coverage checks
        private const double MinFragmentOverlapRatio = 0.6;  // >=60% of fragment area inside cell counts as inside
        private const float BoxInflate = 0.5f;              // small inflation of cell rect during overlap check

        static public PdfDocument ToPdfDocumentFromBase64(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("missing base64");

            byte[] pdfBytes;
            try { pdfBytes = Convert.FromBase64String(base64); }
            catch { throw new ArgumentException("invalid base64"); }

            var ms = new MemoryStream(pdfBytes); // caller should dispose later
            var reader = new PdfReader(ms);
            return new PdfDocument(reader);
        }

        static public PdfDocument ToPdfDocumentFromFile(string filePath)
        {
            var reader = new PdfReader(filePath);
            return new PdfDocument(reader);
        }



        // ---------- Public API ----------
        public List<PageOutput> Extract(PdfDocument doc)
        {
            var pages = new List<PageOutput>();

            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p);
                var pageSize = page.GetPageSize();

                // 1) Capture text fragments and vector path segments
                var listener = new PageCaptureListener();
                var processor = new PdfCanvasProcessor(listener);
                processor.ProcessPageContent(page);

                // 2) Build cell rectangles from path segments (same logic as your working ExtractTableBoxes)
                var cellRects = BuildCellRects(listener.Segments, AxisTol, PosMergeTol, SegMergeGap, MinLineLen, CoverSlack);

                // 3) Assign text fragments to cells based on overlap ratio; leftovers become outside lines
                var blocks = AssignFragmentsToBoxes(listener.Texts, cellRects, out var assignedFrags);
                var outsideFragments = listener.Texts.Where(f => !assignedFrags.Contains(f)).ToList();
                var lines = BuildLinesFromFragments(outsideFragments, LineYTol);

                pages.Add(new PageOutput
                {
                    Page = p,
                    Width = (int)Math.Round(pageSize.GetWidth()),
                    Height = (int)Math.Round(pageSize.GetHeight()),
                    Blocks = blocks.Select(b => new BlockOutput
                    {
                        X = (int)Math.Round(b.Box.GetX()),
                        Y = (int)Math.Round(b.Box.GetY()),
                        Width = (int)Math.Round(b.Box.GetWidth()),
                        Height = (int)Math.Round(b.Box.GetHeight()),
                        Text = b.Text
                    }).ToList(),
                    Lines = lines.Select(l => new LineOutput
                    {
                        X = (int)Math.Round(l.Box.GetX()),
                        Y = (int)Math.Round(l.Box.GetY()),
                        Width = (int)Math.Round(l.Box.GetWidth()),
                        Height = (int)Math.Round(l.Box.GetHeight()),
                        Text = l.Text
                    }).ToList()
                });
            }

            return pages;
        }

        // ---------- Models (public) ----------
        public class PageOutput
        {
            public int Page { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public List<BlockOutput> Blocks { get; set; } = new List<BlockOutput>();
            public List<LineOutput> Lines { get; set; } = new List<LineOutput>();
        }

        public class BlockOutput
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public string Text { get; set; } = string.Empty;
        }

        public class LineOutput
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public string Text { get; set; } = string.Empty;
        }

        // ---------- Internal DTOs ----------
        private class TextFragment { public string Text = string.Empty; public Rectangle Box = new Rectangle(0, 0); }
        private class LineObj { public string Text = string.Empty; public Rectangle Box = new Rectangle(0, 0); }
        private class BlockObj { public string Text = string.Empty; public Rectangle Box = new Rectangle(0, 0); }
        private class Seg { public double X1, Y1, X2, Y2; }
        private class VLine { public double X; public List<(double, double)> Intervals = new List<(double, double)>(); }
        private class HLine { public double Y; public List<(double, double)> Intervals = new List<(double, double)>(); }

        // ---------- Listener ----------
        private class PageCaptureListener : IEventListener
        {
            public List<TextFragment> Texts { get; } = new List<TextFragment>();
            public List<Seg> Segments { get; } = new List<Seg>();

            public void EventOccurred(IEventData data, EventType type)
            {
                switch (type)
                {
                    case EventType.RENDER_TEXT:
                        {
                            var tri = (TextRenderInfo)data;
                            var text = tri.GetText();
                            if (string.IsNullOrWhiteSpace(text)) return;

                            var a = tri.GetAscentLine().GetBoundingRectangle();
                            var d = tri.GetDescentLine().GetBoundingRectangle();
                            var x1 = Math.Min(a.GetX(), d.GetX());
                            var y1 = Math.Min(a.GetY(), d.GetY());
                            var x2 = Math.Max(a.GetRight(), d.GetRight());
                            var y2 = Math.Max(a.GetTop(), d.GetTop());
                            var rect = new Rectangle((float)x1, (float)y1, (float)(x2 - x1), (float)(y2 - y1));
                            Texts.Add(new TextFragment { Text = text, Box = rect });
                            break;
                        }
                    case EventType.RENDER_PATH:
                        {
                            var pri = (PathRenderInfo)data;
                            var path = pri.GetPath();
                            var subs = path.GetSubpaths();
                            if (subs == null) return;

                            foreach (var sp in subs)
                            {
                                var segs = sp.GetSegments();
                                if (segs == null || segs.Count == 0) continue;

                                var pts = new List<Point>();
                                foreach (var s in segs)
                                {
                                    var basePts = s.GetBasePoints();
                                    if (basePts == null || basePts.Count == 0) continue;
                                    if (pts.Count == 0 || !SamePoint(pts[pts.Count - 1], basePts[0]))
                                        pts.Add(basePts[0]);
                                    var last = basePts[basePts.Count - 1];
                                    if (!SamePoint(pts[pts.Count - 1], last)) pts.Add(last);
                                }

                                for (int i = 1; i < pts.Count; i++) AddIfLine(pts[i - 1], pts[i]);
                                if (sp.IsClosed() && pts.Count > 2) AddIfLine(pts[pts.Count - 1], pts[0]);
                            }
                            break;
                        }
                }

                void AddIfLine(Point a, Point b)
                {
                    double xA = a.GetX(), yA = a.GetY();
                    double xB = b.GetX(), yB = b.GetY();
                    var dx = xB - xA; var dy = yB - yA;
                    var len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < MinLineLen) return;
                    Segments.Add(new Seg { X1 = xA, Y1 = yA, X2 = xB, Y2 = yB });
                }
                static bool SamePoint(Point p1, Point p2)
                    => Math.Abs(p1.GetX() - p2.GetX()) < 0.001 && Math.Abs(p1.GetY() - p2.GetY()) < 0.001;
            }

            public ICollection<EventType> GetSupportedEvents()
                => new HashSet<EventType> { EventType.RENDER_TEXT, EventType.RENDER_PATH };
        }

        // ---------- Text: fragments -> lines ----------
        private static List<LineObj> BuildLinesFromFragments(List<TextFragment> texts, double yTol)
        {
            var buckets = new List<List<TextFragment>>();
            foreach (var t in texts.OrderByDescending(t => t.Box.GetY()))
            {
                var myY = t.Box.GetY() + t.Box.GetHeight() / 2f;
                bool added = false;
                for (int i = 0; i < buckets.Count; i++)
                {
                    var b = buckets[i];
                    double refY = 0;
                    for (int j = 0; j < b.Count; j++) refY += b[j].Box.GetY() + b[j].Box.GetHeight() / 2f;
                    refY /= Math.Max(1, b.Count);
                    if (Math.Abs(refY - myY) <= yTol) { b.Add(t); added = true; break; }
                }
                if (!added) buckets.Add(new List<TextFragment> { t });
            }

            var lines = new List<LineObj>();
            foreach (var b in buckets)
            {
                var ordered = b.OrderBy(x => x.Box.GetX()).ToList();
                var sb = new StringBuilder();
                TextFragment prev = null;
                for (int k = 0; k < ordered.Count; k++)
                {
                    var f = ordered[k];
                    if (prev != null)
                    {
                        var gap = f.Box.GetX() - (prev.Box.GetX() + prev.Box.GetWidth());
                        var wPrev = Math.Max(0.1f, prev.Box.GetWidth() / Math.Max(1, prev.Text.Length));
                        var wCurr = Math.Max(0.1f, f.Box.GetWidth() / Math.Max(1, f.Text.Length));
                        var threshold = 0.6f * (wPrev + wCurr) / 2f;
                        if (gap > threshold) sb.Append(' ');
                    }
                    sb.Append(f.Text);
                    prev = f;
                }
                var txt = sb.ToString();
                while (txt.Contains("  ")) txt = txt.Replace("  ", " ");
                var rect = Union(b.Select(x => x.Box));
                lines.Add(new LineObj { Text = txt.Trim(), Box = rect });
            }

            return lines.OrderByDescending(l => l.Box.GetY() + l.Box.GetHeight()).ToList();
        }

        // ---------- Assign fragments to boxes ----------
        private static List<BlockObj> AssignFragmentsToBoxes(List<TextFragment> frags, List<Rectangle> boxes, out HashSet<TextFragment> assigned)
        {
            assigned = new HashSet<TextFragment>();
            var blocks = new List<BlockObj>();

            for (int i = 0; i < boxes.Count; i++)
            {
                var rect = boxes[i];
                var inflated = new Rectangle(rect.GetX() - BoxInflate, rect.GetY() - BoxInflate,
                                             rect.GetWidth() + 2 * BoxInflate, rect.GetHeight() + 2 * BoxInflate);

                var inside = new List<TextFragment>();
                for (int j = 0; j < frags.Count; j++)
                {
                    var f = frags[j];
                    float fArea = Math.Max(0.0001f, f.Box.GetWidth() * f.Box.GetHeight());
                    float ovl = OverlapArea(inflated, f.Box);
                    double ratio = ovl / fArea;
                    if (ratio >= MinFragmentOverlapRatio)
                    {
                        inside.Add(f);
                        assigned.Add(f);
                    }
                }

                if (inside.Count == 0)
                {
                    blocks.Add(new BlockObj { Box = rect, Text = string.Empty });
                }
                else
                {
                    var lines = BuildLinesFromFragments(inside, LineYTol);
                    var sb = new StringBuilder();
                    for (int k = 0; k < lines.Count; k++) { if (k > 0) sb.Append('\n'); sb.Append(lines[k].Text); }
                    blocks.Add(new BlockObj { Box = rect, Text = sb.ToString() });
                }
            }
            return blocks;
        }

        // ---------- Lines -> grid -> cell rects ----------
        private static List<Rectangle> BuildCellRects(List<Seg> segs, double axisTol, double posMergeTol, double segMergeGap, double minLen, double coverSlack)
        {
            var vLines = BuildVerticalLines(segs, axisTol, minLen, posMergeTol, segMergeGap);
            var hLines = BuildHorizontalLines(segs, axisTol, minLen, posMergeTol, segMergeGap);

            var xs = vLines.Select(v => v.X).Distinct().OrderBy(x => x).ToList();
            var ys = hLines.Select(h => h.Y).Distinct().OrderBy(y => y).ToList();

            var boxes = new List<Rectangle>();
            if (xs.Count < 2 || ys.Count < 2) return boxes;

            for (int xi = 0; xi < xs.Count - 1; xi++)
            {
                var x1 = xs[xi];
                var x2 = xs[xi + 1];
                var vx1 = FindNearest(vLines, x1);
                var vx2 = FindNearest(vLines, x2);
                if (vx1 == null || vx2 == null) continue;

                for (int yi = 0; yi < ys.Count - 1; yi++)
                {
                    var y1 = ys[yi];
                    var y2 = ys[yi + 1];
                    var hy1 = FindNearest(hLines, y1);
                    var hy2 = FindNearest(hLines, y2);
                    if (hy1 == null || hy2 == null) continue;

                    bool left = CoversMulti(vx1.Intervals, y1, y2, coverSlack);
                    bool right = CoversMulti(vx2.Intervals, y1, y2, coverSlack);
                    bool bottom = CoversMulti(hy1.Intervals, x1, x2, coverSlack);
                    bool top = CoversMulti(hy2.Intervals, x1, x2, coverSlack);

                    if (left && right && bottom && top)
                        boxes.Add(new Rectangle((float)x1, (float)y1, (float)(x2 - x1), (float)(y2 - y1)));
                }
            }
            return boxes;
        }

        private static List<VLine> BuildVerticalLines(List<Seg> segs, double axisTol, double minLen, double posMergeTol, double segMergeGap)
        {
            var verts = segs
                .Where(s => Math.Abs(s.X2 - s.X1) < axisTol && Math.Abs(s.Y2 - s.Y1) >= minLen)
                .Select(s => (x: (s.X1 + s.X2) / 2.0, y1: Math.Min(s.Y1, s.Y2), y2: Math.Max(s.Y1, s.Y2)))
                .ToList();

            var groups = ClusterByPosition(verts.Select(v => v.x).ToList(), posMergeTol);
            var vlines = new List<VLine>();
            for (int gi = 0; gi < groups.Count; gi++)
            {
                double gx = groups[gi].Average();
                var intervals = verts.Where(v => Math.Abs(v.x - gx) <= posMergeTol).Select(v => (v.y1, v.y2)).ToList();
                var merged = MergeIntervals(intervals, segMergeGap);
                vlines.Add(new VLine { X = gx, Intervals = merged });
            }
            return vlines.OrderBy(v => v.X).ToList();
        }

        private static List<HLine> BuildHorizontalLines(List<Seg> segs, double axisTol, double minLen, double posMergeTol, double segMergeGap)
        {
            var hors = segs
                .Where(s => Math.Abs(s.Y2 - s.Y1) < axisTol && Math.Abs(s.X2 - s.X1) >= minLen)
                .Select(s => (y: (s.Y1 + s.Y2) / 2.0, x1: Math.Min(s.X1, s.X2), x2: Math.Max(s.X1, s.X2)))
                .ToList();

            var groups = ClusterByPosition(hors.Select(h => h.y).ToList(), posMergeTol);
            var hlines = new List<HLine>();
            for (int gi = 0; gi < groups.Count; gi++)
            {
                double gy = groups[gi].Average();
                var intervals = hors.Where(h => Math.Abs(h.y - gy) <= posMergeTol).Select(h => (h.x1, h.x2)).ToList();
                var merged = MergeIntervals(intervals, segMergeGap);
                hlines.Add(new HLine { Y = gy, Intervals = merged });
            }
            return hlines.OrderBy(h => h.Y).ToList();
        }

        private static List<List<double>> ClusterByPosition(List<double> vals, double tol)
        {
            var res = new List<List<double>>();
            vals.Sort();
            for (int i = 0; i < vals.Count; i++)
            {
                double v = vals[i];
                bool placed = false;
                for (int g = 0; g < res.Count; g++)
                {
                    double avg = 0; var grp = res[g];
                    for (int k = 0; k < grp.Count; k++) avg += grp[k];
                    avg /= Math.Max(1, grp.Count);
                    if (Math.Abs(avg - v) <= tol) { grp.Add(v); placed = true; break; }
                }
                if (!placed) res.Add(new List<double> { v });
            }
            return res;
        }

        private static List<(double, double)> MergeIntervals(List<(double, double)> ivals, double gap)
        {
            var result = new List<(double, double)>();
            if (ivals == null || ivals.Count == 0) return result;
            var list = ivals.Select(t => (Math.Min(t.Item1, t.Item2), Math.Max(t.Item1, t.Item2)))
                            .OrderBy(t => t.Item1).ToList();

            double curA = list[0].Item1, curB = list[0].Item2;
            for (int i = 1; i < list.Count; i++)
            {
                double a = list[i].Item1, b = list[i].Item2;
                if (a <= curB + gap) curB = Math.Max(curB, b);
                else { result.Add((curA, curB)); curA = a; curB = b; }
            }
            result.Add((curA, curB));
            return result;
        }

        private static bool CoversMulti(List<(double, double)> ivals, double start, double end, double slack)
        {
            if (end < start) { double tmp = start; start = end; end = tmp; }
            if (ivals == null || ivals.Count == 0) return false;

            var merged = MergeIntervals(ivals, 0);
            double covered = 0;
            for (int i = 0; i < merged.Count; i++)
            {
                double lo = Math.Max(merged[i].Item1, start);
                double hi = Math.Min(merged[i].Item2, end);
                if (hi > lo) covered += (hi - lo);
                if (covered >= (end - start) - slack) return true;
            }
            return false;
        }

        private static VLine FindNearest(List<VLine> xs, double x)
        {
            VLine best = null; double bestD = double.MaxValue;
            for (int i = 0; i < xs.Count; i++)
            {
                double d = Math.Abs(xs[i].X - x);
                if (d < bestD) { bestD = d; best = xs[i]; }
            }
            return best;
        }
        private static HLine FindNearest(List<HLine> ys, double y)
        {
            HLine best = null; double bestD = double.MaxValue;
            for (int i = 0; i < ys.Count; i++)
            {
                double d = Math.Abs(ys[i].Y - y);
                if (d < bestD) { bestD = d; best = ys[i]; }
            }
            return best;
        }

        // ---------- Geometry helpers ----------
        private static Rectangle Union(IEnumerable<Rectangle> rects)
        {
            using var e = rects.GetEnumerator();
            if (!e.MoveNext()) return new Rectangle(0, 0, 0, 0);
            var u = e.Current;
            while (e.MoveNext()) u = Union(u, e.Current);
            return u;
        }
        private static Rectangle Union(Rectangle a, Rectangle b)
        {
            float x1 = Math.Min(a.GetX(), b.GetX());
            float y1 = Math.Min(a.GetY(), b.GetY());
            float x2 = Math.Max(a.GetRight(), b.GetRight());
            float y2 = Math.Max(a.GetTop(), b.GetTop());
            return new Rectangle(x1, y1, x2 - x1, y2 - y1);
        }
        private static float OverlapArea(Rectangle a, Rectangle b)
        {
            float ix = Math.Max(a.GetX(), b.GetX());
            float iy = Math.Max(a.GetY(), b.GetY());
            float ax = Math.Min(a.GetRight(), b.GetRight());
            float ay = Math.Min(a.GetTop(), b.GetTop());
            float w = ax - ix; float h = ay - iy;
            if (w <= 0 || h <= 0) return 0f; return w * h;
        }
    }
}
