using System.Text.RegularExpressions;

namespace ReadPdf.Converters
{
    internal class NormalizedConverter
    {
        public static List<DocumentNormalizer.DocumentItem> DoJustHeaderAndTable(List<DocumentNormalizer.DocumentItem> items)
        {
            var r = new List<DocumentNormalizer.DocumentItem>();
            var headersDone = false;
            DocumentNormalizer.DocumentItem? firstTable = null;

            foreach (var item in items)
            {
                if (headersDone)
                {
                    if (item.Table != null && firstTable != null)
                        firstTable.Table.Cells.AddRange(item.Table.Cells); // tabellen stapelen
                    // lines na tabel overslaan
                }
                else
                {
                    r.Add(item); // header-lines + eerste tabel
                }

                if (item.Table != null && firstTable == null)
                {
                    headersDone = true;
                    firstTable = item;
                }
            }

            return r;
        }

        public static List<DocumentNormalizer.DocumentItem> DoJustHeaderAndTablePerSubdoc(
            List<DocumentNormalizer.DocumentItem> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));

            // Bepaal voor elke pagina de onderste regel (footer) en lees het paginanummer uit
            var itemsByPage = items.GroupBy(i => i.Page).ToDictionary(g => g.Key, g => g.ToList());
            var startPages = new SortedSet<int>(); // pagina's die met 1 beginnen => nieuwe subdoc

            foreach (var kv in itemsByPage)
            {
                int page = kv.Key;
                var bottomLine = kv.Value
                    .Where(i => i.Kind == "line" && i.Line != null)
                    .OrderBy(i => i.Y) // kleinste Y = onderaan
                    .ThenBy(i => i.X)
                    .FirstOrDefault();

                if (bottomLine != null && TryParseFooterNumber(bottomLine.Line.Text, out int n) && n == 1)
                    startPages.Add(page);
            }

            // Geen startmarkers? Val terug op oude gedrag over alle items
            if (startPages.Count == 0) return JustHeaderAndMergedFirstTable(items);

            // Partitioneer items per subdocument (paginaranges)
            var starts = startPages.OrderBy(p => p).ToList();
            var ranges = new List<(int start, int end)>();
            for (int i = 0; i < starts.Count; i++)
            {
                int start = starts[i];
                int end = (i < starts.Count - 1) ? starts[i + 1] - 1 : int.MaxValue;
                ranges.Add((start, end));
            }

            var result = new List<DocumentNormalizer.DocumentItem>();
            foreach (var range in ranges)
            {
                var chunk = items.Where(i => i.Page >= range.start && i.Page <= range.end).ToList();
                result.AddRange(JustHeaderAndMergedFirstTable(chunk));
            }
            return result;
        }

        // --- Helpers ---

        private static bool TryParseFooterNumber(string text, out int n)
        {
            n = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();

            if (int.TryParse(text, out n)) return true;

            var m = Regex.Match(text, @"(?:pagina|bladzijde|page)\s*(\d+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (m.Success && int.TryParse(m.Groups[1].Value, out n)) return true;

            return false;
        }

        private static List<DocumentNormalizer.DocumentItem> JustHeaderAndMergedFirstTable(
            List<DocumentNormalizer.DocumentItem> items)
        {
            var r = new List<DocumentNormalizer.DocumentItem>();
            bool headersDone = false;
            DocumentNormalizer.DocumentItem firstTable = null;

            foreach (var item in items)
            {
                if (headersDone)
                {
                    if (item.Table != null && firstTable != null && item.Table.Cells != null)
                        firstTable.Table.Cells.AddRange(item.Table.Cells); // tabellen stapelen
                                                                           // lines na tabel overslaan
                }
                else
                {
                    r.Add(item); // header-lines + eerste tabel
                }

                if (!headersDone && item.Table != null && firstTable == null)
                {
                    headersDone = true;
                    firstTable = item;
                }
            }

            return r;
        }
    }
}