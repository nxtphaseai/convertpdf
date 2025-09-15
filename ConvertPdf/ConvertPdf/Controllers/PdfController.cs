using Microsoft.AspNetCore.Mvc;
using ReadPdf;
using System.Text.Json.Serialization;

namespace ConvertPdf.Controllers
{
    [ApiController]
    [Route("pdf")]
    public class PdfController : ControllerBase
    {
        private readonly ILogger<PdfController> _logger;

        public PdfController(ILogger<PdfController> logger)
        {
            _logger = logger;
        }

        public class options
        {
            [JsonPropertyName("conversiontype")]
            public string? conversiontype { get; set; } = "text";
        }

        public class pdf_request
        {
            [JsonPropertyName("base64")]
            public string base64 { get; set; } = string.Empty;

            [JsonPropertyName("options")]
            public options? options { get; set; }
        }

        [HttpPost("to-text")]
        public ActionResult<string> ConvertToText([FromBody] pdf_request req)
        {
            if (req is null || string.IsNullOrWhiteSpace(req.base64))
                return BadRequest("missing base64");

            using var doc = PdfFactory.FromBase64(req.base64);   // controller owns lifetime

            var extractor = new ReadPdf.PdfTableTextExtractor();
            var pagesOutput = extractor.Extract(doc);
            var textNorm = new ReadPdf.DocumentTextNormalizer();
            textNorm.JustHeaderAndTable = true; // alleen de lines op de eerste pagina boven de tabel zijn van belang.
            string text = textNorm.Render(pagesOutput);   // platte tekst volgens jouw format
            return Ok(text);
        }

        [HttpPost("to-json")]
        [Produces("application/json")]
        public ActionResult<object> ConvertToJson([FromBody] pdf_request req)
        {
            if (req is null || string.IsNullOrWhiteSpace(req.base64))
                return BadRequest("missing base64");

            using var doc = PdfFactory.FromBase64(req.base64);   // controller owns lifetime
            var extractor = new ReadPdf.PdfTableTextExtractor();
            var pagesOutput = extractor.Extract(doc);

            var textNorm = new ReadPdf.DocumentJsonNormalizer
            {
                JustHeaderAndTable = true
            };

            var obj = textNorm.Render(pagesOutput);
            return Ok(obj);
        }

    }
}
