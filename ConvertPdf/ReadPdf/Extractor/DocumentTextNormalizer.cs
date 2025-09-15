using ReadPdf.Converters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReadPdf
{
    /// <summary>
    /// Zet pagesOutput om naar platte tekst.
    /// - Lines: elke regel onder elkaar. Bij grote verticale gap: extra lege regel.
    /// - Tables: neem bovenste rij als header; voor elke volgende rij:
    ///     Header:
    ///     Waarde
    ///   (per kolom), met na elke rij 1 lege regel en na de hele tabel 2 lege regels.
    /// Let op: rijen volgorde wordt TOP->BOTTOM afgedwongen (dus omgekeerd t.o.v. PDF-y).
    /// Vereist: DocumentNormalizer (voor het bouwen van table-grids uit blocks).
    /// </summary>
    public class DocumentTextNormalizer
    {
        // Gap factor: extra lege regel als deltaY > k * gemiddelde regelhoogte op de pagina
        private const double BigGapFactor = 1.5;

        public IList<string>? SetHeaders;
        public bool JustHeaderAndTable = false; // alleen de lines op de eerste pagina boven de tabel zijn van belang.

        public string Render(IReadOnlyList<PdfTableTextExtractor.PageOutput> pages)
        {
            if (pages == null) throw new ArgumentNullException(nameof(pages));

            // Gebruik bestaande normalizer om line/table-items te maken
            var dn = new DocumentNormalizer();
            var items = dn.Build(pages.ToList());

            if (JustHeaderAndTable)
                items = NormalizedConverter.DoJustHeaderAndTablePerSubdoc(items);

            var sb = new StringBuilder();

            int currentPage = -1;
            double avgLineHeightOnPage = 0;
            double prevLineTop = double.NaN;

            // Precompute gemiddelde hoogte per pagina uit Lines
            var avgHeights = pages.ToDictionary(
                p => p.Page,
                p => p.Lines.Count == 0 ? 0.0 : p.Lines.Average(l => (double)l.Height)
            );

            foreach (var it in items)
            {
                if (it.Page != currentPage)
                {
                    currentPage = it.Page;
                    avgLineHeightOnPage = avgHeights.TryGetValue(currentPage, out var h) ? (h <= 0 ? 10 : h) : 10; // fallback 10pt
                    prevLineTop = double.NaN; // reset per pagina
                }

                if (string.Equals(it.Kind, "line", StringComparison.OrdinalIgnoreCase))
                {
                    // Groot gat tussen opeenvolgende regels?
                    double top = it.Y + it.Height;
                    if (!double.IsNaN(prevLineTop))
                    {
                        var delta = prevLineTop - top; // top->bottom afstand
                        if (delta > avgLineHeightOnPage * BigGapFactor)
                            sb.AppendLine();
                    }
                    prevLineTop = top;

                    if (!string.IsNullOrWhiteSpace(it.Line?.Text))
                        sb.AppendLine(it.Line.Text.Trim());
                }
                else if (string.Equals(it.Kind, "table", StringComparison.OrdinalIgnoreCase) && it.Table?.Cells != null)
                {
                    // Dwing rijen volgorde: TOP->BOTTOM
                    var rows = new List<List<string>>(it.Table.Cells);
                    if (rows.Count == 0) { sb.AppendLine(); sb.AppendLine(); continue; }
                    sb.AppendLine();
                    sb.AppendLine();

                    var useSetHeaders = SetHeaders != null && SetHeaders.Count > 0;

                    var headers = useSetHeaders ? SetHeaders! : rows[0];

                    var start = 0;
                    if (headers[0] == rows[0][0])
                    {
                        start = 1; // de eerste rij is header
                    }

                    // Normaliseer headers
                    for (int c = 0; c < headers.Count; c++)
                    {
                        headers[c] = "### " + headers[c].Replace("\n", " ").Trim();
                    }
                    

                    sb.Append("----");
                    // Loop over data-rijen
                    for (int r = start; r < rows.Count; r++)
                    {
                        sb.AppendLine("----");
                        var row = rows[r];
                        int cols = Math.Max(headers.Count, row.Count);
                        for (int c = 0; c < cols; c++)
                        {
                            var name = c < headers.Count ? headers[c] : $"Kolom {c + 1}";
                            var val = c < row.Count ? (row[c] ?? string.Empty).Trim() : string.Empty;
                            sb.AppendLine(name + ":");
                            if (!string.IsNullOrEmpty(val)) sb.AppendLine(val);
                            else sb.AppendLine();
                            sb.AppendLine(); // leegte na elke 'record field'
                        }
                        sb.AppendLine();
                    }

                    sb.AppendLine();
                    sb.AppendLine(); // extra leegte na tabel

                    // Na tabel mixen we lines en tables; reset prevLineTop om onterechte gap-detectie te voorkomen
                    prevLineTop = double.NaN;
                }
            }

            return sb.ToString().TrimEnd();
        }
    }
}
