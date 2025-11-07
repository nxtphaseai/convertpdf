using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ConvertPdf.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TypeAController : ControllerBase
    {
        private readonly string _convertPdfDir;

        public TypeAController(IConfiguration config)
        {
            _convertPdfDir = config.GetValue<string>("ConvertPdfDir")
                ?? throw new InvalidOperationException("ConvertPdfDir not set in appsettings");
        }

        // ======================================
        // POST: /api/typea/upload
        // Ondersteunt:
        // - multipart/form-data (eerste file, veldnaam willekeurig)
        // - application/octet-stream (query: ?filename=..)
        // - application/json { filename, contentBase64 }
        // ======================================
        [HttpPost("upload")]
        [DisableRequestSizeLimit]
        public async Task<ActionResult<string>> Upload([FromQuery] string? filename = null)
        {
            var pickupDir = Path.Combine(_convertPdfDir, "pickup");
            Directory.CreateDirectory(pickupDir);

            byte[]? bytes = null;
            string? incomingFileName = null;

            // 1) multipart/form-data
            if (Request.HasFormContentType && Request.Form?.Files?.Count > 0)
            {
                var formFile = Request.Form.Files[0];
                incomingFileName = string.IsNullOrWhiteSpace(formFile.FileName) ? filename : formFile.FileName;

                if (formFile.Length <= 0) return BadRequest("Empty file");

                await using var ms = new MemoryStream();
                await formFile.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
            else
            {
                // Normaliseer content-type
                var ct = (Request.ContentType ?? "").ToLowerInvariant();

                // 2) application/octet-stream
                if (ct.StartsWith("application/octet-stream"))
                {
                    incomingFileName = filename;
                    if (string.IsNullOrWhiteSpace(incomingFileName))
                        return BadRequest("Missing ?filename= for octet-stream upload");

                    await using var ms = new MemoryStream();
                    await Request.Body.CopyToAsync(ms);
                    bytes = ms.ToArray();
                }
                // 3) application/json met base64
                else if (ct.StartsWith("application/json"))
                {
                    using var doc = await JsonDocument.ParseAsync(Request.Body);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return BadRequest("Invalid JSON");

                    incomingFileName = doc.RootElement.TryGetProperty("filename", out var fnProp)
                        ? fnProp.GetString()
                        : filename;

                    if (!doc.RootElement.TryGetProperty("contentBase64", out var b64Prop))
                        return BadRequest("Missing contentBase64");

                    try
                    {
                        bytes = Convert.FromBase64String(b64Prop.GetString() ?? "");
                    }
                    catch
                    {
                        return BadRequest("contentBase64 is not valid base64");
                    }
                }
            }

            if (bytes == null || bytes.Length == 0)
                return BadRequest("No file data received");

            // ---- Bestandsnaam schoonmaken / afdwingen .pdf ----
            incomingFileName ??= "upload.pdf";

            // Pak alleen de naam, geen pad
            incomingFileName = Path.GetFileName(incomingFileName);

            // Zonder extensie → maak .pdf (of vervang naar .pdf)
            var baseName = Path.GetFileNameWithoutExtension(incomingFileName);
            var ext = Path.GetExtension(incomingFileName);
            if (string.IsNullOrWhiteSpace(ext) || !ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                ext = ".pdf";

            // Normaliseer jobnaam: lowercase, geen spaties/punten in normale naam
            var safeBase = Regex.Replace(baseName.ToLowerInvariant(), @"[^a-z0-9_-]", "_").Replace("-", "_").Replace("__", "_").Replace("__", "_").Replace("__", "_");
            var jobName = safeBase; // dit geven we terug
            var destPath = Path.Combine(pickupDir, jobName + ext);

            await System.IO.File.WriteAllBytesAsync(destPath, bytes);

            // (optioneel) trigger job hier (queue/cron/whatever)

            return Ok(jobName);
        }

        // ======================================
        // GET: /api/typea/result/{jobName}/{fileName}
        // ======================================
        [HttpGet("result/{jobName}/{fileName}")]
        public ActionResult GetResult(string jobName, string fileName)
        {
            if (string.IsNullOrWhiteSpace(jobName) || string.IsNullOrWhiteSpace(fileName))
                return BadRequest("Missing job name or file name");

            // Alleen .json en geen pad
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains('/') || fileName.Contains('\\'))
            {
                return BadRequest("Invalid file name");
            }

            var workDir = Path.Combine(_convertPdfDir, "work", jobName);
            var pickupDir = Path.Combine(_convertPdfDir, "pickup");
            var jsonPath = Path.Combine(workDir, fileName);

            if (!Directory.Exists(workDir))
            {
                var pickupPath = Path.Combine(pickupDir, jobName + ".pdf");
                if (System.IO.File.Exists(pickupPath))
                    return Ok("Job waiting to start");
                else
                    return NotFound("Job not found");
            }

            if (!System.IO.File.Exists(jsonPath))
                return Ok("Job busy");

            var content = System.IO.File.ReadAllText(jsonPath);
            return Content(content, "application/json");
        }
    }
}
