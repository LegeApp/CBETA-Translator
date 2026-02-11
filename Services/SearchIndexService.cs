using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CbetaTranslator.App.Models;

namespace CbetaTranslator.App.Services;


public sealed class SearchIndexService
{
    public sealed class SearchIndexServiceOptions
    {
        // TOTAL cache budget across all cached bloom pages (bytes).
        // Each bloom page is BloomBytes (512 bytes in your current config).
        public long MaxBloomCacheBytes { get; set; } = 40L * 1024 * 1024; // default 40MB
    }

    public SearchIndexServiceOptions Options { get; } = new();

    // LRU cache for bloom pages (offset -> ulong[] bits)
    private readonly Dictionary<long, LinkedListNode<(long key, ulong[] bits)>> _bloomCache = new();
    private readonly LinkedList<(long key, ulong[] bits)> _bloomLru = new();
    private long _bloomCacheBytes = 0;
    private readonly object _bloomLock = new();


    private const string ManifestFileName = "search.index.manifest.json";
    private const string BinFileName = "search.index.bin";


    // Helpers

    private ulong[] GetBloomCached(FileStream fs, long offset)
    {
        // Cache key is the bloom offset in the bin file (unique per entry)
        lock (_bloomLock)
        {
            if (_bloomCache.TryGetValue(offset, out var node))
            {
                _bloomLru.Remove(node);
                _bloomLru.AddFirst(node);
                return node.Value.bits;
            }
        }

        // Miss: read from disk (outside lock)
        var bits = ReadBloom(fs, offset);

        // Insert + evict
        long pageBytes = BloomBytes;

        lock (_bloomLock)
        {
            // Another thread may have inserted meanwhile
            if (_bloomCache.TryGetValue(offset, out var existing))
            {
                _bloomLru.Remove(existing);
                _bloomLru.AddFirst(existing);
                return existing.Value.bits;
            }

            var node = new LinkedListNode<(long key, ulong[] bits)>((offset, bits));
            _bloomLru.AddFirst(node);
            _bloomCache[offset] = node;
            _bloomCacheBytes += pageBytes;

            EvictBloomCacheIfNeeded();
        }

        return bits;
    }

    private void EvictBloomCacheIfNeeded()
    {
        long max = Math.Max(0, Options.MaxBloomCacheBytes);

        // If budget is 0, effectively disable caching
        if (max == 0)
        {
            _bloomCache.Clear();
            _bloomLru.Clear();
            _bloomCacheBytes = 0;
            return;
        }

        while (_bloomCacheBytes > max && _bloomLru.Last != null)
        {
            var last = _bloomLru.Last!;
            _bloomLru.RemoveLast();
            _bloomCache.Remove(last.Value.key);
            _bloomCacheBytes -= BloomBytes;
        }
    }

    public void ClearBloomCache()
    {
        lock (_bloomLock)
        {
            _bloomCache.Clear();
            _bloomLru.Clear();
            _bloomCacheBytes = 0;
        }
    }



    // Small enough to keep in RAM, large enough to prune candidates aggressively
    private const int BloomBits = 4096;          // 4096 bits = 512 bytes
    private const int BloomBytes = BloomBits / 8;
    private const int BloomUlongs = BloomBits / 64;
    private const int BloomHashCount = 4;
    private const string BuildGuid = "search-v1-bloom-4096";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    // Body extraction (same idea as status logic)
    private static string ExtractBodyInnerXml(string xml)
    {
        if (string.IsNullOrEmpty(xml)) return "";

        int iBody = xml.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (iBody < 0) return "";

        int iStart = xml.IndexOf('>', iBody);
        if (iStart < 0) return "";

        int iEnd = xml.IndexOf("</body>", iStart + 1, StringComparison.OrdinalIgnoreCase);
        if (iEnd < 0) return "";

        return xml.Substring(iStart + 1, iEnd - (iStart + 1));
    }

    // Cheap tag stripping (we are NOT a real XML parser)
    private static readonly Regex TagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WsRegex = new Regex(@"[ \t\f\v]+", RegexOptions.Compiled);

    private static string MakeSearchableTextFromXml(string xml)
    {
        // 1) body only
        var body = ExtractBodyInnerXml(xml);
        if (string.IsNullOrEmpty(body)) return "";

        // 2) strip tags
        var noTags = TagRegex.Replace(body, " ");

        // 3) decode entities
        noTags = WebUtility.HtmlDecode(noTags);

        // 4) normalize whitespace lightly (keep newlines)
        noTags = noTags.Replace("\r", "");
        noTags = WsRegex.Replace(noTags, " ");

        return noTags;
    }

    private static string NormalizeRelKey(string p)
        => (p ?? "").Replace('\\', '/').TrimStart('/');

    public string GetManifestPath(string root) => Path.Combine(root, ManifestFileName);
    public string GetBinPath(string root) => Path.Combine(root, BinFileName);

    public async Task<SearchIndexManifest?> TryLoadAsync(string root)
    {
        try
        {
            var mp = GetManifestPath(root);
            var bp = GetBinPath(root);

            if (!File.Exists(mp) || !File.Exists(bp))
                return null;

            var json = await File.ReadAllTextAsync(mp, Utf8NoBom);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var man = JsonSerializer.Deserialize<SearchIndexManifest>(json, JsonOpts);
            if (man == null) return null;

            if (!string.Equals(Path.GetFullPath(man.RootPath), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                return null;

            if (man.Version != 1) return null;
            if (!string.Equals(man.BuildGuid, BuildGuid, StringComparison.Ordinal)) return null;

            if (man.BloomBits != BloomBits || man.BloomHashCount != BloomHashCount)
                return null;

            if (man.Entries == null || man.Entries.Count == 0)
                return null;

            return man;
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(string root, SearchIndexManifest manifest)
    {
        manifest.RootPath = root;
        manifest.BuiltUtc = DateTime.UtcNow;
        manifest.Version = 1;
        manifest.BloomBits = BloomBits;
        manifest.BloomHashCount = BloomHashCount;
        manifest.BuildGuid = BuildGuid;

        var json = JsonSerializer.Serialize(manifest, JsonOpts);
        await File.WriteAllTextAsync(GetManifestPath(root), json, Utf8NoBom);
    }

    // ---------------------------
    // Bloom implementation
    // ---------------------------

    private static uint Fnv1a32(ReadOnlySpan<char> s, uint seed)
    {
        uint hash = 2166136261u ^ seed;
        for (int i = 0; i < s.Length; i++)
        {
            // char -> two bytes-ish would be more stable, but this is fine for filtering
            hash ^= s[i];
            hash *= 16777619u;
        }
        return hash;
    }

    private static void BloomAdd(ulong[] bits, ReadOnlySpan<char> gram)
    {
        uint h1 = Fnv1a32(gram, 0xA5A5A5A5);
        uint h2 = Fnv1a32(gram, 0xC3C3C3C3);

        for (int i = 0; i < BloomHashCount; i++)
        {
            uint mix = (uint)(h1 + (uint)i * 0x9E3779B9u) ^ (uint)(h2 + (uint)i * 0x7F4A7C15u);
            int bit = (int)(mix % (uint)BloomBits);
            int idx = bit / 64;
            int off = bit % 64;
            bits[idx] |= (1UL << off);
        }
    }

    private static bool BloomMightContain(ulong[] bits, ReadOnlySpan<char> gram)
    {
        uint h1 = Fnv1a32(gram, 0xA5A5A5A5);
        uint h2 = Fnv1a32(gram, 0xC3C3C3C3);

        for (int i = 0; i < BloomHashCount; i++)
        {
            uint mix = (uint)(h1 + (uint)i * 0x9E3779B9u) ^ (uint)(h2 + (uint)i * 0x7F4A7C15u);
            int bit = (int)(mix % (uint)BloomBits);
            int idx = bit / 64;
            int off = bit % 64;

            if ((bits[idx] & (1UL << off)) == 0)
                return false;
        }

        return true;
    }

    private static void WriteBloom(FileStream fs, ulong[] bits)
    {
        Span<byte> buf = stackalloc byte[BloomBytes];
        buf.Clear();

        // ulong -> bytes (little endian)
        for (int i = 0; i < BloomUlongs; i++)
        {
            ulong v = bits[i];
            int baseOff = i * 8;
            buf[baseOff + 0] = (byte)(v & 0xFF);
            buf[baseOff + 1] = (byte)((v >> 8) & 0xFF);
            buf[baseOff + 2] = (byte)((v >> 16) & 0xFF);
            buf[baseOff + 3] = (byte)((v >> 24) & 0xFF);
            buf[baseOff + 4] = (byte)((v >> 32) & 0xFF);
            buf[baseOff + 5] = (byte)((v >> 40) & 0xFF);
            buf[baseOff + 6] = (byte)((v >> 48) & 0xFF);
            buf[baseOff + 7] = (byte)((v >> 56) & 0xFF);
        }

        fs.Write(buf);
    }

    private static ulong[] ReadBloom(FileStream fs, long offset)
    {
        fs.Seek(offset, SeekOrigin.Begin);
        byte[] buf = new byte[BloomBytes];
        int read = 0;
        while (read < buf.Length)
        {
            int r = fs.Read(buf, read, buf.Length - read);
            if (r <= 0) break;
            read += r;
        }

        var bits = new ulong[BloomUlongs];
        for (int i = 0; i < BloomUlongs; i++)
        {
            int o = i * 8;
            ulong v =
                ((ulong)buf[o + 0]) |
                ((ulong)buf[o + 1] << 8) |
                ((ulong)buf[o + 2] << 16) |
                ((ulong)buf[o + 3] << 24) |
                ((ulong)buf[o + 4] << 32) |
                ((ulong)buf[o + 5] << 40) |
                ((ulong)buf[o + 6] << 48) |
                ((ulong)buf[o + 7] << 56);

            bits[i] = v;
        }

        return bits;
    }

    private static void BuildBloomFromText(ulong[] bits, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Index BOTH 2-grams and 3-grams (helps lots of Chinese queries)
        // This is still cheap and keeps collisions manageable at 4096 bits.
        for (int i = 0; i < text.Length; i++)
        {
            if (i + 2 <= text.Length)
                BloomAdd(bits, text.AsSpan(i, 2));

            if (i + 3 <= text.Length)
                BloomAdd(bits, text.AsSpan(i, 3));
        }
    }

    private static List<(int n, int start)> MakeQueryGrams(string q)
    {
        q = q ?? "";
        q = q.Trim();
        var grams = new List<(int n, int start)>();

        if (q.Length >= 3)
        {
            for (int i = 0; i + 3 <= q.Length; i++)
                grams.Add((3, i));
            return grams;
        }

        if (q.Length == 2)
        {
            grams.Add((2, 0));
            return grams;
        }

        return grams; // length 0 or 1 -> no grams
    }

    // ---------------------------
    // Build Index
    // ---------------------------

    public Task BuildAsync(
        string root,
        string originalDir,
        string translatedDir,
        IProgress<(int done, int total, string phase)>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var origFiles = Directory.EnumerateFiles(originalDir, "*.xml", SearchOption.AllDirectories).ToList();
            var tranFiles = Directory.EnumerateFiles(translatedDir, "*.xml", SearchOption.AllDirectories).ToList();

            // Relpath keys
            var origMap = origFiles.ToDictionary(f => NormalizeRelKey(Path.GetRelativePath(originalDir, f)), f => f, StringComparer.OrdinalIgnoreCase);
            var tranMap = tranFiles.ToDictionary(f => NormalizeRelKey(Path.GetRelativePath(translatedDir, f)), f => f, StringComparer.OrdinalIgnoreCase);

            // Universe: any relpath that exists in either side (practically originals drive this)
            var allRel = origMap.Keys.Union(tranMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

            int total = allRel.Count * 2; // two sides
            int done = 0;

            var manifest = new SearchIndexManifest
            {
                RootPath = root,
                BuiltUtc = DateTime.UtcNow,
                BuildGuid = BuildGuid,
                BloomBits = BloomBits,
                BloomHashCount = BloomHashCount,
                Version = 1,
            };

            var binPath = GetBinPath(root);
            using var fs = new FileStream(binPath, FileMode.Create, FileAccess.Write, FileShare.Read);

            long offset = 0;
            int id = 0;

            void IndexOne(string relKey, SearchSide side, string? absPath)
            {
                ct.ThrowIfCancellationRequested();

                var bits = new ulong[BloomUlongs];

                long ticks = 0;
                long lenBytes = 0;

                if (!string.IsNullOrWhiteSpace(absPath) && File.Exists(absPath))
                {
                    var fi = new FileInfo(absPath);
                    ticks = fi.LastWriteTimeUtc.Ticks;
                    lenBytes = fi.Length;

                    string xml = File.ReadAllText(absPath, Utf8NoBom);
                    string searchable = MakeSearchableTextFromXml(xml);

                    BuildBloomFromText(bits, searchable);
                }

                // write fixed-size bloom
                WriteBloom(fs, bits);

                manifest.Entries.Add(new SearchIndexEntry
                {
                    Id = id++,
                    RelPath = relKey,
                    Side = side,
                    LastWriteUtcTicks = ticks,
                    LengthBytes = lenBytes,
                    BloomOffset = offset
                });

                offset += BloomBytes;

                done++;
                if (done % 50 == 0)
                    progress?.Report((done, total, "Indexing..."));
            }

            progress?.Report((done, total, "Indexing..."));

            foreach (var relKey in allRel)
            {
                origMap.TryGetValue(relKey, out var oAbs);
                tranMap.TryGetValue(relKey, out var tAbs);

                // Always create both entries (even if file missing): keeps ID mapping stable
                IndexOne(relKey, SearchSide.Original, oAbs);
                IndexOne(relKey, SearchSide.Translated, tAbs);
            }

            fs.Flush(true);

            // Persist manifest
            var json = JsonSerializer.Serialize(manifest, JsonOpts);
            File.WriteAllText(GetManifestPath(root), json, Utf8NoBom);

            progress?.Report((total, total, "Done"));
        }, ct);
    }

    // ---------------------------
    // Search
    // ---------------------------

    public sealed class SearchProgress
    {
        public int Candidates { get; set; }
        public int VerifiedDocs { get; set; }
        public int TotalDocsToVerify { get; set; }
        public int Groups { get; set; }
        public int TotalHits { get; set; }
        public string Phase { get; set; } = "";
    }

    public async IAsyncEnumerable<SearchResultGroup> SearchAllAsync(
        string root,
        string originalDir,
        string translatedDir,
        SearchIndexManifest manifest,
        string query,
        bool includeOriginal,
        bool includeTranslated,
        Func<string, (string display, string tooltip, TranslationStatus? status)> fileMeta,
        int contextWidth,
        IProgress<SearchProgress>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0)
            yield break;

        // 1-char search is inherently huge; we allow it but skip bloom candidate pruning
        bool useBloom = query.Length >= 2;

        var grams = MakeQueryGrams(query);

        // Build candidate list
        progress?.Report(new SearchProgress { Phase = "Loading bloom index..." });

        var binPath = GetBinPath(root);
        using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // Filter entries by side
        var sideAllowed = new Func<SearchSide, bool>(s =>
            (s == SearchSide.Original && includeOriginal) ||
            (s == SearchSide.Translated && includeTranslated));

        // Candidate relpaths grouped
        var candidates = new Dictionary<string, (bool o, bool t)>(StringComparer.OrdinalIgnoreCase);

        if (!useBloom)
        {
            // brute candidate set (still verify with exact scan)
            foreach (var e in manifest.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!sideAllowed(e.Side)) continue;

                if (!candidates.TryGetValue(e.RelPath, out var v)) v = default;
                if (e.Side == SearchSide.Original) v.o = true;
                else v.t = true;
                candidates[e.RelPath] = v;
            }
        }
        else
        {
            foreach (var e in manifest.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!sideAllowed(e.Side)) continue;

                // If file missing (ticks==0 and len==0), it definitely doesn't contain text
                if (e.LastWriteUtcTicks == 0 || e.LengthBytes == 0)
                    continue;

                // Load bloom bits for this doc and test all grams
                var bits = GetBloomCached(fs, e.BloomOffset);


                bool ok = true;
                for (int i = 0; i < grams.Count; i++)
                {
                    var (n, start) = grams[i];
                    if (start + n > query.Length) continue;

                    if (!BloomMightContain(bits, query.AsSpan(start, n)))
                    {
                        ok = false;
                        break;
                    }
                }

                if (!ok) continue;

                if (!candidates.TryGetValue(e.RelPath, out var v)) v = default;
                if (e.Side == SearchSide.Original) v.o = true;
                else v.t = true;
                candidates[e.RelPath] = v;
            }
        }

        int totalDocsToVerify =
            candidates.Values.Sum(v => (v.o ? 1 : 0) + (v.t ? 1 : 0));

        int verifiedDocs = 0;
        int totalHits = 0;
        int groupsOut = 0;

        progress?.Report(new SearchProgress
        {
            Phase = useBloom ? "Candidate filtering done" : "Brute candidates (1-char search)",
            Candidates = totalDocsToVerify,
            TotalDocsToVerify = totalDocsToVerify
        });

        // Verify + emit groups as they are found
        foreach (var kv in candidates.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            string relKey = kv.Key;
            var sides = kv.Value;

            var meta = fileMeta(relKey);
            var group = new SearchResultGroup
            {
                RelPath = relKey,
                DisplayName = string.IsNullOrWhiteSpace(meta.display) ? relKey : meta.display,
                Tooltip = string.IsNullOrWhiteSpace(meta.tooltip) ? relKey : meta.tooltip,
                Status = meta.status
            };

            int hitsO = 0;
            int hitsT = 0;

            if (sides.o)
            {
                ct.ThrowIfCancellationRequested();
                string abs = Path.Combine(originalDir, relKey.Replace('/', Path.DirectorySeparatorChar));
                var hits = VerifyFileAllHits(abs, query, contextWidth);
                verifiedDocs++;

                foreach (var h in hits)
                {
                    hitsO++;
                    totalHits++;

                    group.Children.Add(new SearchResultChild
                    {
                        RelPath = relKey,
                        Side = SearchSide.Original,
                        Hit = h
                    });
                }
            }

            if (sides.t)
            {
                ct.ThrowIfCancellationRequested();
                string abs = Path.Combine(translatedDir, relKey.Replace('/', Path.DirectorySeparatorChar));
                var hits = VerifyFileAllHits(abs, query, contextWidth);
                verifiedDocs++;

                foreach (var h in hits)
                {
                    hitsT++;
                    totalHits++;

                    group.Children.Add(new SearchResultChild
                    {
                        RelPath = relKey,
                        Side = SearchSide.Translated,
                        Hit = h
                    });
                }
            }

            group.HitsOriginal = hitsO;
            group.HitsTranslated = hitsT;

            if (group.Children.Count > 0)
            {
                groupsOut++;

                progress?.Report(new SearchProgress
                {
                    Phase = "Searching...",
                    Candidates = totalDocsToVerify,
                    VerifiedDocs = verifiedDocs,
                    TotalDocsToVerify = totalDocsToVerify,
                    Groups = groupsOut,
                    TotalHits = totalHits
                });

                yield return group;
            }
            else
            {
                progress?.Report(new SearchProgress
                {
                    Phase = "Searching...",
                    Candidates = totalDocsToVerify,
                    VerifiedDocs = verifiedDocs,
                    TotalDocsToVerify = totalDocsToVerify,
                    Groups = groupsOut,
                    TotalHits = totalHits
                });
            }

            // Keep UI responsive
            if (verifiedDocs % 20 == 0)
                await Task.Yield();
        }

        progress?.Report(new SearchProgress
        {
            Phase = "Done",
            Candidates = totalDocsToVerify,
            VerifiedDocs = verifiedDocs,
            TotalDocsToVerify = totalDocsToVerify,
            Groups = groupsOut,
            TotalHits = totalHits
        });
    }

    private static List<SearchHit> VerifyFileAllHits(string absPath, string query, int contextWidth)
    {
        var hits = new List<SearchHit>();
        if (!File.Exists(absPath)) return hits;

        string xml;
        try { xml = File.ReadAllText(absPath, Utf8NoBom); }
        catch { return hits; }

        string text;
        try { text = MakeSearchableTextFromXml(xml); }
        catch { return hits; }

        if (string.IsNullOrEmpty(text)) return hits;

        int idx = 0;
        while (true)
        {
            idx = text.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;

            int start = idx;
            int end = idx + query.Length;

            int leftStart = Math.Max(0, start - contextWidth);
            int rightEnd = Math.Min(text.Length, end + contextWidth);

            string left = text.Substring(leftStart, start - leftStart);
            string right = text.Substring(end, rightEnd - end);

            // Clean up newlines for KWIC display (we keep meaning but avoid UI mess)
            left = left.Replace("\n", " ").TrimStart();
            right = right.Replace("\n", " ").TrimEnd();

            hits.Add(new SearchHit
            {
                Index = start,
                Left = left,
                Match = text.Substring(start, query.Length),
                Right = right
            });

            idx = Math.Max(end, idx + 1);
        }

        return hits;
    }
}
