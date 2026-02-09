using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CbetaTranslator.App.Models;

namespace CbetaTranslator.App.Services;

public sealed class IndexCacheService
{
    private const string CacheFileName = "index.cache.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public string GetCachePath(string root)
        => Path.Combine(root, CacheFileName);

    public async Task<IndexCache?> TryLoadAsync(string root)
    {
        try
        {
            var path = GetCachePath(root);
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var cache = JsonSerializer.Deserialize<IndexCache>(json, JsonOpts);
            if (cache == null)
                return null;

            // very light sanity
            if (!string.Equals(Path.GetFullPath(cache.RootPath), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                return null;

            return cache;
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(string root, IndexCache cache)
    {
        cache.RootPath = root;
        cache.BuiltUtc = DateTime.UtcNow;

        var path = GetCachePath(root);
        var json = JsonSerializer.Serialize(cache, JsonOpts);
        await File.WriteAllTextAsync(path, json);
    }

    public Task<IndexCache> BuildAsync(
        string originalDir,
        string root,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var files = Directory.EnumerateFiles(originalDir, "*.xml", SearchOption.AllDirectories).ToList();
            int total = files.Count;

            var rel = new List<string>(capacity: total);

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();

                var f = files[i];
                rel.Add(Path.GetRelativePath(originalDir, f));

                // report progress not too often
                if (progress != null && (i % 200 == 0 || i == total - 1))
                    progress.Report((i + 1, total));
            }

            rel.Sort(StringComparer.OrdinalIgnoreCase);

            return new IndexCache
            {
                Version = 1,
                RootPath = root,
                RelativePaths = rel,
                BuiltUtc = DateTime.UtcNow
            };
        }, ct);
    }
}
