using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
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
        public long MaxBloomCacheBytes { get; set; } = 40L * 1024 * 1024; // default 40MB

        // Parallelism controls
        public int MaxVerifyDegreeOfParallelism { get; set; } = Math.Min(8, Environment.ProcessorCount);
        public int MaxBloomDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

        // File replace retries (Defender/indexer can briefly hold locks)
        public int ReplaceTries { get; set; } = 18;
        public int ReplaceDelayMs { get; set; } = 80;
    }

    public SearchIndexServiceOptions Options { get; } = new();

    // Single gate for ANY index IO that would conflict: build/update OR search.
    private static readonly SemaphoreSlim _indexIoGate = new(1, 1);

    // LRU cache for bloom pages (offset -> ulong[] bits) -- used only for sequential reads (if you ever switch back).
    private readonly Dictionary<long, LinkedListNode<(long key, ulong[] bits)>> _bloomCache = new();
    private readonly LinkedList<(long key, ulong[] bits)> _bloomLru = new();
    private long _bloomCacheBytes = 0;
    private readonly object _bloomLock = new();

    private const string ManifestFileName = "search.index.manifest.json";
    private const string BinFileName = "search.index.bin";

    // Small enough to keep in RAM, large enough to prune candidates aggressively
    private const int BloomBits = 4096;          // 4096 bits = 512 bytes
    private const int BloomBytes = BloomBits / 8;
    private const int BloomUlongs = BloomBits / 64;
    private const int BloomHashCount = 4;
    private const string BuildGuid = "search-v1-bloom-4096";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    // ---------------------------
    // Helpers
    // ---------------------------

    private static FileStream OpenFileWithRetry(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        int tries = 12,
        int delayMs = 80)
    {
        Exception? last = null;

        for (int i = 0; i < tries; i++)
        {
            try
            {
                return new FileStream(path, mode, access, share);
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(delayMs);
                delayMs = Math.Min(500, (int)(delayMs * 1.4));
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
                Thread.Sleep(delayMs);
                delayMs = Math.Min(500, (int)(delayMs * 1.4));
            }
        }

        throw new IOException($"Could not open '{path}' after {tries} attempts. Still locked by another process.", last);
    }

    private void ReplaceFileAtomicWithRetry(string tmp, string final)
    {
        Exception? last = null;

        int tries = Math.Max(1, Options.ReplaceTries);
        int delayMs = Math.Max(10, Options.ReplaceDelayMs);

        for (int i = 0; i < tries; i++)
        {
            try
            {
                if (File.Exists(final))
                {
                    var bak = final + ".bak";
                    try { if (File.Exists(bak)) File.Delete(bak); } catch { }

                    File.Replace(tmp, final, bak, ignoreMetadataErrors: true);

                    try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                }
                else
                {
                    File.Move(tmp, final);
                }

                return;
            }
            catch (IOException ex)
            {
                last = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
            }

            Thread.Sleep(delayMs);
            delayMs = Math.Min(500, (int)(delayMs * 1.4));
        }

        throw new IOException($"Failed to replace '{final}' after {tries} attempts.", last);
    }

    private static string NormalizeRelKey(string p)
        => (p ?? "").Replace('\\', '/').TrimStart('/');

    public string GetManifestPath(string root) => Path.Combine(root, ManifestFileName);
    public string GetBinPath(string root) => Path.Combine(root, BinFileName);

    public void ClearBloomCache()
    {
        lock (_bloomLock)
        {
            _bloomCache.Clear();
            _bloomLru.Clear();
            _bloomCacheBytes = 0;
        }
    }

    private ulong[] GetBloomCached(FileStream fs, long offset)
    {
        lock (_bloomLock)
        {
            if (_bloomCache.TryGetValue(offset, out var node))
            {
                _bloomLru.Remove(node);
                _bloomLru.AddFirst(node);
                return node.Value.bits;
            }
        }

        var bits = ReadBloom(fs, offset);

        lock (_bloomLock)
        {
            if (_bloomCache.TryGetValue(offset, out var existing))
            {
                _bloomLru.Remove(existing);
                _bloomLru.AddFirst(existing);
                return existing.Value.bits;
            }

            var node = new LinkedListNode<(long key, ulong[] bits)>((offset, bits));
            _bloomLru.AddFirst(node);
            _bloomCache[offset] = node;
            _bloomCacheBytes += BloomBytes;

            EvictBloomCacheIfNeeded();
        }

        return bits;
    }

    private void EvictBloomCacheIfNeeded()
    {
        long max = Math.Max(0, Options.MaxBloomCacheBytes);

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

    // ---------------------------
    // Body extraction / normalization
    // ---------------------------

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

    private static readonly Regex TagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WsRegex = new Regex(@"[ \t\f\v]+", RegexOptions.Compiled);

    private static string MakeSearchableTextFromXml(string xml)
    {
        var body = ExtractBodyInnerXml(xml);
        if (string.IsNullOrEmpty(body)) return "";

        var noTags = TagRegex.Replace(body, " ");
        noTags = WebUtility.HtmlDecode(noTags);

        noTags = noTags.Replace("\r", "");
        noTags = WsRegex.Replace(noTags, " ");

        return noTags;
    }

    // ---------------------------
    // Manifest I/O
    // ---------------------------

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

            // Basic sanity: offsets within file length
            var binLen = new FileInfo(bp).Length;
            foreach (var e in man.Entries)
            {
                if (e.BloomOffset < 0 || e.BloomOffset + BloomBytes > binLen)
                    return null;
            }

            return man;
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveManifestAtomicAsync(string root, SearchIndexManifest manifest, CancellationToken ct)
    {
        manifest.RootPath = root;
        manifest.BuiltUtc = DateTime.UtcNow;
        manifest.Version = 1;
        manifest.BloomBits = BloomBits;
        manifest.BloomHashCount = BloomHashCount;
        manifest.BuildGuid = BuildGuid;

        var final = GetManifestPath(root);
        var tmp = final + ".tmp";

        var json = JsonSerializer.Serialize(manifest, JsonOpts);
        await File.WriteAllTextAsync(tmp, json, Utf8NoBom, ct);

        ReplaceFileAtomicWithRetry(tmp, final);
    }

    // ---------------------------
    // Bloom implementation
    // ---------------------------

    private static uint Fnv1a32(ReadOnlySpan<char> s, uint seed)
    {
        uint hash = 2166136261u ^ seed;
        for (int i = 0; i < s.Length; i++)
        {
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

    private static void WriteBloom(Stream fs, ulong[] bits)
    {
        Span<byte> buf = stackalloc byte[BloomBytes];
        buf.Clear();

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
        q = (q ?? "").Trim();
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

        return grams;
    }

    // ---------------------------
    // Phase 4: Build / Update Index (incremental)
    // ---------------------------

    // Keeps your old signature (full rebuild) for compatibility.
    public Task BuildAsync(
        string root,
        string originalDir,
        string translatedDir,
        IProgress<(int done, int total, string phase)>? progress = null,
        CancellationToken ct = default)
        => BuildOrUpdateAsync(root, originalDir, translatedDir, forceRebuild: true, progress, ct);

    public Task BuildOrUpdateAsync(
        string root,
        string originalDir,
        string translatedDir,
        bool forceRebuild,
        IProgress<(int done, int total, string phase)>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();

            await _indexIoGate.WaitAsync(ct);
            try
            {
                // Load existing index if allowed
                SearchIndexManifest? oldMan = null;
                string oldBinPath = GetBinPath(root);

                if (!forceRebuild)
                    oldMan = await TryLoadAsync(root);

                FileStream? oldFs = null;
                if (!forceRebuild && oldMan != null && File.Exists(oldBinPath))
                {
                    try { oldFs = new FileStream(oldBinPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
                    catch { oldFs = null; }
                }

                // Map (rel,side) -> old entry
                var oldMap = new Dictionary<(string rel, SearchSide side), SearchIndexEntry>(new RelSideComparer());
                if (!forceRebuild && oldMan != null)
                {
                    foreach (var e in oldMan.Entries)
                        oldMap[(e.RelPath, e.Side)] = e;
                }

                progress?.Report((0, 0, "Scanning filesystem..."));

                // Enumerate files
                var origFiles = Directory.EnumerateFiles(originalDir, "*.xml", SearchOption.AllDirectories)
                    .Select(f => (rel: NormalizeRelKey(Path.GetRelativePath(originalDir, f)), abs: f, fi: new FileInfo(f)))
                    .ToDictionary(x => x.rel, x => x, StringComparer.OrdinalIgnoreCase);

                var tranFiles = Directory.EnumerateFiles(translatedDir, "*.xml", SearchOption.AllDirectories)
                    .Select(f => (rel: NormalizeRelKey(Path.GetRelativePath(translatedDir, f)), abs: f, fi: new FileInfo(f)))
                    .ToDictionary(x => x.rel, x => x, StringComparer.OrdinalIgnoreCase);

                var allRel = origFiles.Keys.Union(tranFiles.Keys, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Total = number of actual sides that exist (we REMOVE missing files)
                int total = 0;
                foreach (var rel in allRel)
                {
                    if (origFiles.ContainsKey(rel)) total++;
                    if (tranFiles.ContainsKey(rel)) total++;
                }

                var manifest = new SearchIndexManifest
                {
                    RootPath = root,
                    BuiltUtc = DateTime.UtcNow,
                    BuildGuid = BuildGuid,
                    BloomBits = BloomBits,
                    BloomHashCount = BloomHashCount,
                    Version = 1,
                };

                // Write to temp bin then swap
                var finalBin = GetBinPath(root);
                var tmpBin = finalBin + ".tmp";

                // Always start clean
                try { if (File.Exists(tmpBin)) File.Delete(tmpBin); } catch { }

                try
                {
                    using (var outFs = new FileStream(tmpBin, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        long offset = 0;
                        int id = 0;
                        int done = 0;

                        void CopyBloomBlock(FileStream src, long srcOffset, Stream dst)
                        {
                            src.Seek(srcOffset, SeekOrigin.Begin);
                            Span<byte> buf = stackalloc byte[BloomBytes];
                            int r = src.Read(buf);
                            if (r == BloomBytes) dst.Write(buf);
                            else
                            {
                                buf.Clear();
                                dst.Write(buf);
                            }
                        }

                        void IndexOne(string relKey, SearchSide side, string absPath, FileInfo fi)
                        {
                            ct.ThrowIfCancellationRequested();

                            long ticks = fi.LastWriteTimeUtc.Ticks;
                            long lenBytes = fi.Length;

                            bool copied = false;

                            if (!forceRebuild && oldFs != null &&
                                oldMap.TryGetValue((relKey, side), out var old) &&
                                old.LastWriteUtcTicks == ticks &&
                                old.LengthBytes == lenBytes &&
                                old.BloomOffset >= 0)
                            {
                                CopyBloomBlock(oldFs, old.BloomOffset, outFs);
                                copied = true;
                            }

                            if (!copied)
                            {
                                var bits = new ulong[BloomUlongs];

                                string xml = File.ReadAllText(absPath, Utf8NoBom);
                                string searchable = MakeSearchableTextFromXml(xml);
                                BuildBloomFromText(bits, searchable);

                                WriteBloom(outFs, bits);
                            }

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

                            if (done % 200 == 0 || done == total)
                                progress?.Report((done, total, forceRebuild ? "Rebuilding index..." : "Updating index..."));
                        }

                        progress?.Report((0, total, forceRebuild ? "Rebuilding index..." : "Updating index..."));

                        foreach (var relKey in allRel)
                        {
                            ct.ThrowIfCancellationRequested();

                            if (origFiles.TryGetValue(relKey, out var o))
                                IndexOne(relKey, SearchSide.Original, o.abs, o.fi);

                            if (tranFiles.TryGetValue(relKey, out var t))
                                IndexOne(relKey, SearchSide.Translated, t.abs, t.fi);
                        }

                        outFs.Flush(true);
                    }
                }
                catch
                {
                    // if anything fails during build, clean tmp so next build isn't confused
                    try { if (File.Exists(tmpBin)) File.Delete(tmpBin); } catch { }
                    throw;
                }
                finally
                {
                    try { oldFs?.Dispose(); } catch { }
                }

                // Swap bin atomically + retry (handles Defender/indexer hiccups)
                ReplaceFileAtomicWithRetry(tmpBin, finalBin);

                // Save manifest atomically too
                await SaveManifestAtomicAsync(root, manifest, ct);

                progress?.Report((total, total, "Done"));
            }
            finally
            {
                _indexIoGate.Release();
            }
        }, ct);
    }

    // ---------------------------
    // Search (Phase: parallelize)
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

        await _indexIoGate.WaitAsync(ct);
        try
        {
            bool useBloom = query.Length >= 2;
            var grams = MakeQueryGrams(query);

            bool sideAllowed(SearchSide s)
                => (s == SearchSide.Original && includeOriginal) ||
                   (s == SearchSide.Translated && includeTranslated);

            progress?.Report(new SearchProgress { Phase = "Building candidates..." });

            // Candidate map: relKey -> bitmask (1=O,2=T)
            var candidates = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (!useBloom)
            {
                // brute candidates
                foreach (var e in manifest.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!sideAllowed(e.Side)) continue;

                    candidates.AddOrUpdate(
                        e.RelPath,
                        _ => e.Side == SearchSide.Original ? 1 : 2,
                        (_, v) => v | (e.Side == SearchSide.Original ? 1 : 2));
                }
            }
            else
            {
                // Parallel bloom filtering using MemoryMappedFile
                string binPath = GetBinPath(root);

                using var mmf = MemoryMappedFile.CreateFromFile(binPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                var po = new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = Math.Max(1, Options.MaxBloomDegreeOfParallelism)
                };

                Parallel.ForEach(manifest.Entries, po, e =>
                {
                    if (!sideAllowed(e.Side)) return;
                    if (e.LastWriteUtcTicks == 0 || e.LengthBytes == 0) return;

                    // Read 512 bytes and convert to ulong[64]
                    byte[] arr = new byte[BloomBytes];
                    accessor.ReadArray(e.BloomOffset, arr, 0, BloomBytes);

                    var bits = new ulong[BloomUlongs];
                    for (int i = 0; i < BloomUlongs; i++)
                    {
                        int o = i * 8;
                        ulong v =
                            ((ulong)arr[o + 0]) |
                            ((ulong)arr[o + 1] << 8) |
                            ((ulong)arr[o + 2] << 16) |
                            ((ulong)arr[o + 3] << 24) |
                            ((ulong)arr[o + 4] << 32) |
                            ((ulong)arr[o + 5] << 40) |
                            ((ulong)arr[o + 6] << 48) |
                            ((ulong)arr[o + 7] << 56);
                        bits[i] = v;
                    }

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

                    if (!ok) return;

                    int mask = (e.Side == SearchSide.Original) ? 1 : 2;
                    candidates.AddOrUpdate(e.RelPath, _ => mask, (_, v) => v | mask);
                });
            }

            var candidateList = candidates.Keys
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int totalDocsToVerify = 0;
            foreach (var rel in candidateList)
            {
                int mask = candidates[rel];
                if ((mask & 1) != 0) totalDocsToVerify++;
                if ((mask & 2) != 0) totalDocsToVerify++;
            }

            progress?.Report(new SearchProgress
            {
                Phase = useBloom ? "Candidate filtering done" : "Brute candidates (1-char search)",
                Candidates = totalDocsToVerify,
                TotalDocsToVerify = totalDocsToVerify
            });

            // Parallel verification (bounded)
            var outGroups = new ConcurrentBag<SearchResultGroup>();
            int verifiedDocs = 0;
            int totalHits = 0;

            var verifyPo = new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(1, Options.MaxVerifyDegreeOfParallelism)
            };

            Parallel.ForEach(candidateList, verifyPo, relKey =>
            {
                ct.ThrowIfCancellationRequested();

                int mask = candidates[relKey];

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

                if ((mask & 1) != 0)
                {
                    string abs = Path.Combine(originalDir, relKey.Replace('/', Path.DirectorySeparatorChar));
                    var hits = VerifyFileAllHits(abs, query, contextWidth);
                    Interlocked.Increment(ref verifiedDocs);

                    foreach (var h in hits)
                    {
                        hitsO++;
                        Interlocked.Increment(ref totalHits);

                        group.Children.Add(new SearchResultChild
                        {
                            RelPath = relKey,
                            Side = SearchSide.Original,
                            Hit = h
                        });
                    }
                }

                if ((mask & 2) != 0)
                {
                    string abs = Path.Combine(translatedDir, relKey.Replace('/', Path.DirectorySeparatorChar));
                    var hits = VerifyFileAllHits(abs, query, contextWidth);
                    Interlocked.Increment(ref verifiedDocs);

                    foreach (var h in hits)
                    {
                        hitsT++;
                        Interlocked.Increment(ref totalHits);

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
                    outGroups.Add(group);

                int v = Volatile.Read(ref verifiedDocs);
                if (v % 50 == 0)
                {
                    progress?.Report(new SearchProgress
                    {
                        Phase = "Searching...",
                        Candidates = totalDocsToVerify,
                        VerifiedDocs = v,
                        TotalDocsToVerify = totalDocsToVerify,
                        Groups = outGroups.Count,
                        TotalHits = Volatile.Read(ref totalHits)
                    });
                }
            });

            var ordered = outGroups
                .OrderBy(g => g.RelPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            progress?.Report(new SearchProgress
            {
                Phase = "Done",
                Candidates = totalDocsToVerify,
                VerifiedDocs = verifiedDocs,
                TotalDocsToVerify = totalDocsToVerify,
                Groups = ordered.Count,
                TotalHits = totalHits
            });

            foreach (var g in ordered)
            {
                ct.ThrowIfCancellationRequested();
                yield return g;
                await Task.Yield();
            }
        }
        finally
        {
            _indexIoGate.Release();
        }
    }

    private sealed class RelSideComparer : IEqualityComparer<(string rel, SearchSide side)>
    {
        public bool Equals((string rel, SearchSide side) x, (string rel, SearchSide side) y)
            => string.Equals(x.rel, y.rel, StringComparison.OrdinalIgnoreCase) && x.side == y.side;

        public int GetHashCode((string rel, SearchSide side) obj)
        {
            unchecked
            {
                int h = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.rel ?? "");
                h = (h * 397) ^ obj.side.GetHashCode();
                return h;
            }
        }
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
