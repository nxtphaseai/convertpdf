using ReadPdf.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ReadPdf
{
    /// <summary>
    /// Zet pagesOutput om naar een LIJST van payloads:
    /// [
    ///   {
    ///     "header": "…alle header tekst voorafgaand aan de tabel…",
    ///     "table": [ { col_a: "…", col_b: "…" }, ... ]
    ///   },
    ///   {
    ///     "header": "…header voor de volgende tabel…",
    ///     "table": [ ... ]
    ///   }
    /// ]
    ///
    /// - Iedere keer dat er een tabel eindigt, wordt er direct een nieuwe payload toegevoegd
    ///   aan de lijst, en beginnen we opnieuw met een lege header + lege table voor het
    ///   volgende segment.
    /// - Header = alle lines tussen het einde van de vorige tabel en het begin van de volgende tabel.
    /// - Tabel: gebruikt bovenste rij als kolomheaders (of SetHeaders),
    ///   kolomnamen worden snake_case. Dubbele namen krijgen suffix _2, _3, …
    /// </summary>
    public class DocumentJsonNormalizer
    {
        private const double BigGapFactor = 1.5;

        /// <summary>
        /// Optioneel: forceer kolomheaders i.p.v. de eerste rij van de tabel te gebruiken.
        /// </summary>
        public IList<string>? SetHeaders;

        /// <summary>
        /// Laat staan voor compatibiliteit met bestaand gedrag (voorbewerking van items).
        /// Als dit true is, worden items eerst door de converter gehaald die subdocumenten
        /// normaliseert. (De logica hieronder blijft per-tabel payloads opleveren.)
        /// </summary>
        public bool JustHeaderAndTable = true;

        /// <summary>
        /// Structurele payload voor één (header, table)-blok.
        /// </summary>
        public sealed class Payload
        {
            public string header { get; set; } = string.Empty;
            public List<Dictionary<string, string>> table { get; set; } = new();
        }

        /// <summary>
        /// Bouw een lijst met payloads. Bij iedere aangetroffen tabel wordt een payload afgesloten.
        /// </summary>
        public IList<Payload> Render(IReadOnlyList<PdfTableTextExtractor.PageOutput> pages, bool indented = false)
        {
            if (pages == null) throw new ArgumentNullException(nameof(pages));

            var dn = new DocumentNormalizer();
            var items = dn.Build(pages.ToList());

            // Eventuele pre-normalisatie (bijv. per subdocument)
            if (JustHeaderAndTable)
                items = NormalizedConverter.DoJustHeaderAndTablePerSubdoc(items);

            // Gemiddelde lijnteskt-hoogte per pagina om lege regels te detecteren
            var avgHeights = pages.ToDictionary(
                p => p.Page,
                p => p.Lines.Count == 0 ? 0.0 : p.Lines.Average(l => (double)l.Height)
            );

            // Accumulatoren voor de "lopende" payload
            var headerSb = new StringBuilder();
            var tableRecords = new List<Dictionary<string, string>>();

            // Resultaatlijst
            var result = new List<Payload>();

            int currentPage = -1;
            double avgLineHeightOnPage = 0;
            double prevLineTop = double.NaN;

            foreach (var it in items)
            {
                if (it.Page != currentPage)
                {
                    currentPage = it.Page;
                    avgLineHeightOnPage = avgHeights.TryGetValue(currentPage, out var h) ? (h <= 0 ? 10 : h) : 10;
                    prevLineTop = double.NaN;
                }

                if (string.Equals(it.Kind, "line", StringComparison.OrdinalIgnoreCase))
                {
                    // Lines horen bij de header voor de volgende tabel.
                    double top = it.Y + it.Height;
                    if (!double.IsNaN(prevLineTop))
                    {
                        var delta = prevLineTop - top;
                        if (delta > avgLineHeightOnPage * BigGapFactor)
                            headerSb.AppendLine(); // extra lege regel bij grote gap
                    }
                    prevLineTop = top;

                    var txt = it.Line?.Text;
                    if (!string.IsNullOrWhiteSpace(txt))
                        headerSb.AppendLine(txt.Trim());
                }
                else if (string.Equals(it.Kind, "table", StringComparison.OrdinalIgnoreCase) && it.Table?.Cells != null)
                {
                    var rows = new List<List<string>>(it.Table.Cells);
                    if (rows.Count == 0)
                    {
                        // Lege tabel: sluit payload met lege table en huidige header
                        result.Add(new Payload
                        {
                            header = headerSb.ToString().TrimEnd(),
                            table = new List<Dictionary<string, string>>()
                        });
                        // Reset voor volgende payload
                        headerSb.Clear();
                        tableRecords.Clear();
                        prevLineTop = double.NaN;
                        continue;
                    }

                    // Bepaal headers
                    var headers = (SetHeaders != null && SetHeaders.Count > 0)
                        ? SetHeaders.ToList()
                        : rows[0].ToList();

                    int startRow = 0;
                    if (SetHeaders == null && rows.Count > 0 && headers[0] == rows[0][0])
                        startRow = 1; // eerste rij is header

                    // Normaliseer header-namen -> snake_case + dedup
                    var colKeys = BuildUniqueSnakeKeys(headers);

                    // Map data-rijen naar records
                    tableRecords.Clear();
                    for (int r = startRow; r < rows.Count; r++)
                    {
                        var row = rows[r];
                        var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        for (int c = 0; c < colKeys.Count; c++)
                        {
                            var key = colKeys[c];
                            var val = c < row.Count ? (row[c] ?? string.Empty).Trim() : string.Empty;
                            record[key] = val;
                        }
                        tableRecords.Add(record);
                    }

                    // Payload afsluiten op het moment dat de tabel klaar is
                    result.Add(new Payload
                    {
                        header = headerSb.ToString().TrimEnd(),
                        table = new List<Dictionary<string, string>>(tableRecords)
                    });

                    // Reset voor de volgende payload
                    headerSb.Clear();
                    tableRecords.Clear();
                    prevLineTop = double.NaN;
                }
            }

            // Als het document eindigt zonder nog een tabel, voegen we GEEN payload toe,
            // omdat de afspraak is: een payload ontstaat bij het einde van een tabel.

            return result;
        }

        private static List<string> BuildUniqueSnakeKeys(IList<string> headers)
        {
            var result = new List<string>(headers.Count);
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < headers.Count; i++)
            {
                var snake = ToSnakeCase(headers[i]);
                if (string.IsNullOrWhiteSpace(snake)) snake = $"col_{i + 1}";

                if (seen.TryGetValue(snake, out var count))
                {
                    count++;
                    seen[snake] = count;
                    snake = $"{snake}_{count}";
                }
                else
                {
                    seen[snake] = 1;
                }
                result.Add(snake);
            }
            return result;
        }

        private static string ToSnakeCase(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.Replace('\n', ' ').Trim();

            // Verwijder diacritics
            var norm = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(capacity: norm.Length);
            foreach (var ch in norm)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            var noDia = sb.ToString().Normalize(NormalizationForm.FormC);

            // Naar snake: letters/cijfers behouden; rest -> _
            sb.Clear();
            bool prevUnderscore = false;
            foreach (var ch in noDia)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    prevUnderscore = false;
                }
                else
                {
                    if (!prevUnderscore)
                    {
                        sb.Append('_');
                        prevUnderscore = true;
                    }
                }
            }
            var snake = sb.ToString().Trim('_');

            // Collapse multiple underscores (kan door leading/trailing)
            while (snake.Contains("__"))
                snake = snake.Replace("__", "_");

            return snake;
        }
    }
}
