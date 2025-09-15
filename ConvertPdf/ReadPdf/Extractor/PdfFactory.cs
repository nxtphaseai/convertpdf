using iText.Kernel.Pdf;

namespace ReadPdf;


public static class PdfFactory
{
    public static PdfDocument FromBase64(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new ArgumentException("missing base64");

        byte[] pdfBytes;
        try { pdfBytes = Convert.FromBase64String(base64); }
        catch { throw new ArgumentException("invalid base64"); }

        var ms = new MemoryStream(pdfBytes); // disposed when doc.Dispose()
        var reader = new PdfReader(ms);
        return new PdfDocument(reader);
    }

    public static PdfDocument FromFile(string filePath)
        => new PdfDocument(new PdfReader(filePath));
}
