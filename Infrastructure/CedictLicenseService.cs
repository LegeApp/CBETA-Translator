using System;
using System.IO;
using System.Text;

namespace CbetaTranslator.App.Infrastructure;

public static class CedictLicenseService
{
    /// <summary>
    /// Reads the header comment block from the CEDICT file (lines starting with '#').
    /// This is the safest "correct attribution" because it's taken from the shipped dictionary file itself.
    /// </summary>
    public static string ReadCedictHeader(string cedictPath, int maxLines = 200)
    {
        if (!File.Exists(cedictPath))
            return "CC-CEDICT header not found (dictionary file missing).";

        var sb = new StringBuilder();
        using var fs = File.OpenRead(cedictPath);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        int lines = 0;
        string? line;
        while ((line = sr.ReadLine()) != null && lines < maxLines)
        {
            lines++;

            if (line.StartsWith("#"))
            {
                sb.AppendLine(line.TrimEnd());
                continue;
            }

            // once the header ends, stop
            break;
        }

        var text = sb.ToString().Trim();
        if (text.Length == 0)
            return "No CC-CEDICT header comments were found at the top of the dictionary file.";

        return text;
    }
}
