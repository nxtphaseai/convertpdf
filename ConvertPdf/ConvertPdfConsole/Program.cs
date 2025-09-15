using System.Text.Json;

namespace ConvertPdfConsole
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: ConvertPdfConsole <input-pdf-file>");
                return;
            }
            var inputPdf = args[0];
            if (!File.Exists(inputPdf))
            {
                Console.WriteLine($"File not found: {inputPdf}");
                return;
            }

            var extractor = new ReadPdf.PdfTableTextExtractor();
            using var doc = ReadPdf.PdfFactory.FromFile(inputPdf);  
            var pagesOutput = extractor.Extract(doc);

            var outputJson = System.IO.Path.ChangeExtension(inputPdf, ".okay.json");
            File.WriteAllText(outputJson, JsonSerializer.Serialize(pagesOutput, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Saved: {outputJson}");

            var normalizer = new ReadPdf.DocumentNormalizer();
            var items = normalizer.Build(pagesOutput);

            outputJson = System.IO.Path.ChangeExtension(inputPdf, ".norm.json");
            File.WriteAllText(outputJson, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Saved: {outputJson}");

            var textNorm = new ReadPdf.DocumentTextNormalizer();
            textNorm.JustHeaderAndTable = true; // alleen de lines op de eerste pagina boven de tabel zijn van belang.
            //textNorm.SetHeaders = new List<string> { "Tijd", "Artikel", "..." };

            string text = textNorm.Render(pagesOutput);

            outputJson = System.IO.Path.ChangeExtension(inputPdf, ".norm.md");
            File.WriteAllText(outputJson, text);
            Console.WriteLine($"Saved: {outputJson}");


            var jsonNorm = new ReadPdf.DocumentJsonNormalizer();
            jsonNorm.JustHeaderAndTable = true; // alleen de lines op de eerste pagina boven de tabel zijn van belang.
            var obj = jsonNorm.Render(pagesOutput); 

            outputJson = System.IO.Path.ChangeExtension(inputPdf, ".norm2.json");
            File.WriteAllText(outputJson, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Saved: {outputJson}");
        }
    }
}
