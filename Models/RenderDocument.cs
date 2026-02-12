// Models/RenderedDocument.cs
using System;
using System.Collections.Generic;
using System.Linq;
using CbetaTranslator.App.Infrastructure;

namespace CbetaTranslator.App.Models;

public sealed class RenderedDocument
{
    public string Text { get; }
    public List<RenderSegment> Segments { get; }

    public List<DocAnnotation> Annotations { get; }
    public List<AnnotationMarkerInserter.MarkerSpan> AnnotationMarkers { get; }

    private readonly Dictionary<string, RenderSegment> _byKey;

    public bool IsEmpty => string.IsNullOrEmpty(Text) || Segments.Count == 0;

    public static RenderedDocument Empty { get; }
        = new RenderedDocument(
            "",
            new List<RenderSegment>(),
            new List<DocAnnotation>(),
            new List<AnnotationMarkerInserter.MarkerSpan>());

    public RenderedDocument(
        string text,
        List<RenderSegment> segments,
        List<DocAnnotation> annotations,
        List<AnnotationMarkerInserter.MarkerSpan> markers)
    {
        Text = text ?? "";
        Segments = segments ?? new List<RenderSegment>();
        Annotations = annotations ?? new List<DocAnnotation>();
        AnnotationMarkers = markers ?? new List<AnnotationMarkerInserter.MarkerSpan>();

        // CRITICAL: binary search requires sorted markers
        if (AnnotationMarkers.Count > 1)
            AnnotationMarkers.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        _byKey = Segments
            .GroupBy(s => s.Key, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToDictionary(s => s.Key, s => s, StringComparer.Ordinal);
    }

    public bool TryGetSegmentByKey(string key, out RenderSegment seg)
        => _byKey.TryGetValue(key, out seg);

    public RenderSegment? FindSegmentAtOrBefore(int renderedOffset)
    {
        if (Segments.Count == 0) return null;

        int lo = 0, hi = Segments.Count - 1;
        int best = -1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            int s = Segments[mid].Start;

            if (s <= renderedOffset)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best >= 0 ? Segments[best] : Segments[0];
    }

    public bool TryGetAnnotationByMarkerAt(int renderedOffset, out DocAnnotation ann)
    {
        ann = default!;

        if (AnnotationMarkers == null || AnnotationMarkers.Count == 0) return false;
        if (Annotations == null || Annotations.Count == 0) return false;

        // ---- binary search (fast path) ----
        int lo = 0, hi = AnnotationMarkers.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            var m = AnnotationMarkers[mid];

            if (renderedOffset < m.Start) hi = mid - 1;
            else if (renderedOffset >= m.EndExclusive) lo = mid + 1;
            else
            {
                int idx = m.AnnotationIndex;
                if ((uint)idx < (uint)Annotations.Count)
                {
                    ann = Annotations[idx];
                    return true;
                }
                return false;
            }
        }

        // ---- fallback: marker list might be weird (or click lands near marker) ----
        // This makes the feature robust even if some spans are slightly off.
        const int radius = 10;
        int a = Math.Max(0, renderedOffset - radius);
        int b = renderedOffset + radius;

        for (int i = 0; i < AnnotationMarkers.Count; i++)
        {
            var m = AnnotationMarkers[i];

            // quick reject: marker completely outside neighborhood
            if (m.EndExclusive <= a) continue;
            if (m.Start >= b) break; // because sorted

            // neighborhood overlap
            if (renderedOffset >= m.Start && renderedOffset < m.EndExclusive ||
                a >= m.Start && a < m.EndExclusive ||
                b >= m.Start && b < m.EndExclusive)
            {
                int idx = m.AnnotationIndex;
                if ((uint)idx < (uint)Annotations.Count)
                {
                    ann = Annotations[idx];
                    return true;
                }
            }
        }

        return false;
    }
}

