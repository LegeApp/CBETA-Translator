using System;
using System.IO;

namespace CbetaTranslator.App.Infrastructure;

public static partial class AppPaths
{
    public static string GetCedictPath()
        => Path.Combine(AppContext.BaseDirectory, "assets", "dict", "cedict_ts.u8");

    public static void EnsureCedictFolderExists()
    {
        var path = GetCedictPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
    }
}
