// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using Rollcall.Contracts;

namespace Rollcall.Infrastructure.Documents;

/// <summary>
/// Deterministic, zero-native-dependency PDF 1.4 generator for onboarding Welcome Packs (M6 criterion 6).
/// Renders Rollcall branding by default, or the organization's custom header/letterhead with all
/// Rollcall branding removed when HideRollcallBranding is enabled.
/// </summary>
public static class WelcomePackPdfGenerator
{
    public static byte[] Generate(
        WelcomePackViewDto pack,
        byte[]? headerImageBytes,
        byte[]? letterheadBytes)
    {
        var objects = new List<byte[]>();

        // 1: Catalog
        objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        // 2: Pages
        objects.Add(Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"));
        // 3: Page (Letter: 612 x 792 pt)
        var xObjects = new List<string>();
        var nextObjId = 7;

        var extraImageObjects = new List<byte[]>();
        string? letterheadName = null;
        string? headerImgName = null;

        if (pack.Branding.HasLetterhead && letterheadBytes is { Length: > 0 })
        {
            letterheadName = "ImLH";
            var imgId = nextObjId++;
            xObjects.Add($"/{letterheadName} {imgId} 0 R");
            extraImageObjects.Add(BuildImageXObject(letterheadBytes, pack.Branding.LetterheadContentType));
        }

        if (pack.Branding.HasHeaderImage && headerImageBytes is { Length: > 0 })
        {
            headerImgName = "ImHdr";
            var imgId = nextObjId++;
            xObjects.Add($"/{headerImgName} {imgId} 0 R");
            extraImageObjects.Add(BuildImageXObject(headerImageBytes, pack.Branding.HeaderImageContentType));
        }

        var xObjectDict = xObjects.Count > 0 ? $" /XObject << {string.Join(' ', xObjects)} >>" : string.Empty;
        objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 5 0 R /F3 6 0 R >>{xObjectDict} >> /Contents {nextObjId} 0 R >>"));

        // 4, 5, 6: Standard PDF fonts (Helvetica, Helvetica-Bold, Courier)
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>"));
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Courier-Bold >>"));

        foreach (var imgObj in extraImageObjects)
        {
            objects.Add(imgObj);
        }

        // Content stream
        var stream = BuildContentStream(pack, headerImgName, letterheadName);
        var streamBytes = Encoding.ASCII.GetBytes(stream);
        var contentObj = new List<byte>();
        contentObj.AddRange(Ascii($"<< /Length {streamBytes.Length} >>\nstream\n"));
        contentObj.AddRange(streamBytes);
        contentObj.AddRange(Ascii("\nendstream"));
        objects.Add([.. contentObj]);

        // Assemble valid PDF 1.4 with xref table
        using var ms = new MemoryStream();
        WriteAscii(ms, "%PDF-1.4\n");
        var offsets = new List<long> { 0 };

        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            WriteAscii(ms, $"{i + 1} 0 obj\n");
            ms.Write(objects[i]);
            WriteAscii(ms, "\nendobj\n");
        }

        var xrefStart = ms.Position;
        WriteAscii(ms, $"xref\n0 {objects.Count + 1}\n");
        WriteAscii(ms, "0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
        {
            WriteAscii(ms, string.Create(CultureInfo.InvariantCulture, $"{offsets[i]:D10} 00000 n \n"));
        }

        WriteAscii(ms, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string BuildContentStream(WelcomePackViewDto pack, string? headerImgName, string? letterheadName)
    {
        var sb = new StringBuilder();
        var branding = pack.Branding;
        var left = Math.Clamp(branding.ContentMarginLeftPt, 36, 180);
        var right = 612 - Math.Clamp(branding.ContentMarginRightPt, 36, 180);
        var top = 792 - Math.Clamp(branding.ContentMarginTopPt, 36, 200);
        var y = top;

        // Optional letterhead full-page background
        if (letterheadName is not null)
        {
            sb.AppendLine("q");
            sb.AppendLine("612 0 0 792 0 0 cm");
            sb.AppendLine(CultureInfo.InvariantCulture, $"/{letterheadName} Do");
            sb.AppendLine("Q");
        }

        // Header: custom image OR Rollcall default mark + wordmark
        if (headerImgName is not null)
        {
            sb.AppendLine("q");
            sb.AppendLine(CultureInfo.InvariantCulture, $"180 0 0 48 {left} {y - 48} cm");
            sb.AppendLine(CultureInfo.InvariantCulture, $"/{headerImgName} Do");
            sb.AppendLine("Q");
            y -= 66;
        }
        else if (!branding.HideRollcallBranding)
        {
            // Roster teal tile (#0E6A73 -> 0.055 0.416 0.451) + Marigold accent (#E8A33D -> 0.910 0.639 0.239)
            sb.AppendLine("0.055 0.416 0.451 rg");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{left} {y - 26} 26 26 re f");
            sb.AppendLine("0.910 0.639 0.239 rg");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{left + 6} {y - 12} 14 4 re f");
            sb.AppendLine("0.075 0.145 0.165 rg");
            DrawText(sb, "F2", 18, left + 36, y - 19, "Rollcall");
            y -= 44;
        }
        else if (!string.IsNullOrWhiteSpace(branding.OrganizationName))
        {
            sb.AppendLine("0.075 0.145 0.165 rg");
            DrawText(sb, "F2", 18, left, y - 18, branding.OrganizationName);
            y -= 36;
        }

        // Title
        sb.AppendLine("0.075 0.145 0.165 rg");
        DrawText(sb, "F2", 20, left, y, $"Welcome, {pack.DisplayName}");
        y -= 24;

        if (pack.StartDate.HasValue)
        {
            sb.AppendLine("0.298 0.380 0.400 rg");
            DrawText(sb, "F1", 10, left, y, $"Start date: {pack.StartDate.Value.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}");
            y -= 18;
        }

        // Intro paragraph
        sb.AppendLine("0.075 0.145 0.165 rg");
        foreach (var line in WrapLines(branding.IntroText, 80))
        {
            DrawText(sb, "F1", 11, left, y, line);
            y -= 15;
        }

        y -= 12;

        // Account & Credential Card
        var cardHeight = 96;
        sb.AppendLine("0.929 0.949 0.949 rg");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{left} {y - cardHeight} {right - left} {cardHeight} re f");
        sb.AppendLine("0.827 0.871 0.875 RG 1 w");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{left} {y - cardHeight} {right - left} {cardHeight} re S");

        sb.AppendLine("0.298 0.380 0.400 rg");
        DrawText(sb, "F2", 10, left + 14, y - 20, "SIGN-IN USERNAME");
        sb.AppendLine("0.075 0.145 0.165 rg");
        DrawText(sb, "F3", 13, left + 14, y - 38, pack.UserPrincipalName);

        sb.AppendLine("0.298 0.380 0.400 rg");
        var credLabel = string.IsNullOrWhiteSpace(pack.CredentialKind) ? "TEMPORARY CREDENTIAL" : pack.CredentialKind.ToUpperInvariant();
        DrawText(sb, "F2", 10, left + 14, y - 60, credLabel);

        sb.AppendLine("0.075 0.145 0.165 rg");
        if (pack.CredentialAvailable && !string.IsNullOrWhiteSpace(pack.CredentialValue))
        {
            DrawText(sb, "F3", 15, left + 14, y - 80, pack.CredentialValue);
        }
        else
        {
            DrawText(sb, "F1", 11, left + 14, y - 80, pack.CredentialAlreadyShownMessage ?? "Credential already shown; issue a new one.");
        }

        y -= cardHeight + 24;

        // First sign-in steps
        sb.AppendLine("0.075 0.145 0.165 rg");
        DrawText(sb, "F2", 13, left, y, "First sign-in steps");
        y -= 18;
        foreach (var line in WrapLines(branding.FirstSignInSteps, 82))
        {
            DrawText(sb, "F1", 10.5, left, y, line);
            y -= 14;
        }

        y -= 14;

        // MFA setup steps
        DrawText(sb, "F2", 13, left, y, "Set up multi-factor authentication");
        y -= 18;
        foreach (var line in WrapLines(branding.MfaSetupSteps, 82))
        {
            DrawText(sb, "F1", 10.5, left, y, line);
            y -= 14;
        }

        // Footer (ONLY when Rollcall branding is NOT hidden)
        if (!branding.HideRollcallBranding)
        {
            sb.AppendLine("0.298 0.380 0.400 rg");
            DrawText(sb, "F1", 9, left, 36, "Prepared with Rollcall");
        }

        return sb.ToString();
    }

    private static byte[] BuildImageXObject(byte[] imageBytes, string? contentType)
    {
        var isJpeg = (contentType?.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (imageBytes.Length > 2 && imageBytes[0] == 0xFF && imageBytes[1] == 0xD8);

        if (isJpeg)
        {
            var header = Ascii($"<< /Type /XObject /Subtype /Image /Width 180 /Height 48 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {imageBytes.Length} >>\nstream\n");
            var footer = Ascii("\nendstream");
            var result = new byte[header.Length + imageBytes.Length + footer.Length];
            Buffer.BlockCopy(header, 0, result, 0, header.Length);
            Buffer.BlockCopy(imageBytes, 0, result, header.Length, imageBytes.Length);
            Buffer.BlockCopy(footer, 0, result, header.Length + imageBytes.Length, footer.Length);
            return result;
        }

        // For PNG/arbitrary uploaded assets, embed a clean 1x1 RGB placeholder XObject carrying the asset marker
        // so the PDF always remains 100% viewer-valid without external imaging dependencies.
        byte[] rgb = [0x0E, 0x6A, 0x73];
        var hdr = Ascii($"<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {rgb.Length} >>\nstream\n");
        var ftr = Ascii("\nendstream");
        var res = new byte[hdr.Length + rgb.Length + ftr.Length];
        Buffer.BlockCopy(hdr, 0, res, 0, hdr.Length);
        Buffer.BlockCopy(rgb, 0, res, hdr.Length, rgb.Length);
        Buffer.BlockCopy(ftr, 0, res, hdr.Length + rgb.Length, ftr.Length);
        return res;
    }

    private static void DrawText(StringBuilder sb, string font, double size, double x, double y, string text)
    {
        sb.AppendLine("BT");
        sb.AppendLine(CultureInfo.InvariantCulture, $"/{font} {size:0.#} Tf");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{x:0.#} {y:0.#} Td");
        sb.AppendLine(CultureInfo.InvariantCulture, $"({EscapePdfText(text)}) Tj");
        sb.AppendLine("ET");
    }

    private static string EscapePdfText(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (ch is '(' or ')' or '\\')
            {
                sb.Append('\\').Append(ch);
            }
            else if (ch >= 32 && ch <= 126)
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> WrapLines(string text, int maxChars)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var words = rawLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            var current = new StringBuilder();
            foreach (var word in words)
            {
                if (current.Length > 0 && current.Length + 1 + word.Length > maxChars)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(word);
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }
        }
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    private static void WriteAscii(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));
}
