// Text/CbetaTeiRenderer.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CbetaTranslator.App.Infrastructure;
using CbetaTranslator.App.Models;

namespace CbetaTranslator.App.Text;

/// <summary>
/// Converts TEI/CBETA-ish XML into readable text WITH stable segment keys.
/// Fine-grained segmentation (preferred):
/// - Start new segment on sync-tags: lb, pb, p(xml:id), anchor, cb:juan
/// - Render lb as newline, pb/p/head as paragraph break
/// Not a full XML parser; fast tag/text scanner.
///
/// Notes/annotations:
/// - Skips rendering <back> entirely (so “校注” blocks don’t show in the reader)
/// - Collects:
///   1) Inline notes: <note place="inline">...</note> at current text position
///   2) End notes in <back>: <note ... target="#nkr_note_mod_XXXX">...</note>
///      anchored by <anchor xml:id="nkr_note_mod_XXXX" .../> in the body.
/// - Builds DocAnnotation list + calls AnnotationMarkerInserter.InsertMarkers(...)
/// </summary>
public static class CbetaTeiRenderer
{
    public static RenderedDocument Render(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return RenderedDocument.Empty;

        var sb = new StringBuilder(xml.Length);
        var segments = new List<RenderSegment>(capacity: 4096);

        // collected annotations
        var annotations = new List<DocAnnotation>(capacity: 128);

        // anchor xml:id -> rendered offset (in sb) + inferred kind
        var anchorPosById = new Dictionary<string, (int Pos, string? Kind)>(StringComparer.Ordinal);

        // note capture state (for <note> ... </note>)
        bool inNoteCapture = false;
        var noteSb = new StringBuilder(256);
        int noteAnchorPos = -1;
        string? noteKind = null;

        string currentKey = "START";
        int segStart = 0;

        int teiHeaderDepth = 0;
        int backDepth = 0; // when >0, we do not render text to sb, but we still parse notes

        bool lastWasNewline = false;       // for main sb
        bool noteLastWasNewline = false;   // for noteSb

        void StartNewSegment(string newKey)
        {
            int end = sb.Length;
            if (end > segStart)
                segments.Add(new RenderSegment(currentKey, segStart, end));

            currentKey = newKey;
            segStart = sb.Length;
        }

        StartNewSegment("START");

        ReadOnlySpan<char> s = xml.AsSpan();
        int i = 0;

        while (i < s.Length)
        {
            int relLt = s.Slice(i).IndexOf('<');
            if (relLt < 0)
            {
                // trailing text
                if (teiHeaderDepth == 0 && backDepth == 0 && !inNoteCapture)
                    AppendText(sb, s.Slice(i), ref lastWasNewline);
                else if (inNoteCapture)
                    AppendText(noteSb, s.Slice(i), ref noteLastWasNewline);
                break;
            }

            int lt = i + relLt;

            // text before tag
            if (lt > i)
            {
                var rawText = s.Slice(i, lt - i);

                if (inNoteCapture)
                {
                    AppendText(noteSb, rawText, ref noteLastWasNewline);
                }
                else if (teiHeaderDepth == 0 && backDepth == 0)
                {
                    AppendText(sb, rawText, ref lastWasNewline);
                }
            }

            // find end of tag
            int relGt = s.Slice(lt).IndexOf('>');
            if (relGt < 0)
            {
                // malformed tail -> treat as text
                var tail = s.Slice(lt);
                if (inNoteCapture)
                    AppendText(noteSb, tail, ref noteLastWasNewline);
                else if (teiHeaderDepth == 0 && backDepth == 0)
                    AppendText(sb, tail, ref lastWasNewline);
                break;
            }

            int gt = lt + relGt;
            var tagSpan = s.Slice(lt, gt - lt + 1);

            if (TryParseTag(tagSpan, out var isEndTag, out var tagName, out var attrs))
            {
                if (isEndTag)
                {
                    // depth tracking
                    if (EqualsIgnoreCase(tagName, "teiHeader"))
                        teiHeaderDepth = Math.Max(0, teiHeaderDepth - 1);

                    if (EqualsIgnoreCase(tagName, "back"))
                        backDepth = Math.Max(0, backDepth - 1);

                    // finish note capture
                    if (EqualsIgnoreCase(tagName, "note") && inNoteCapture)
                    {
                        inNoteCapture = false;

                        var noteText = noteSb.ToString().Trim();
                        noteSb.Clear();
                        noteLastWasNewline = false;

                        if (noteAnchorPos >= 0 && !string.IsNullOrWhiteSpace(noteText))
                        {
                            // anchor point note: Start==EndExclusive is fine for marker insertion
                            annotations.Add(new DocAnnotation(noteAnchorPos, noteAnchorPos, noteText, noteKind));
                        }

                        noteAnchorPos = -1;
                        noteKind = null;
                    }

                    // paragraph end spacing (only in main rendered part)
                    if (teiHeaderDepth == 0 && backDepth == 0 && EqualsIgnoreCase(tagName, "p"))
                        EnsureParagraphBreak(sb, ref lastWasNewline);
                }
                else
                {
                    // entering blocks
                    if (EqualsIgnoreCase(tagName, "teiHeader"))
                    {
                        teiHeaderDepth++;
                    }
                    else if (EqualsIgnoreCase(tagName, "back"))
                    {
                        backDepth++;
                    }

                    // If we're capturing a note and we hit any start-tag: treat as a soft separator
                    // (so <lb/> etc. doesn't smash words together inside notes)
                    if (inNoteCapture)
                    {
                        // a little conservative: only add space/newline for obvious breaks
                        if (EqualsIgnoreCase(tagName, "lb") || EqualsIgnoreCase(tagName, "p") || EqualsIgnoreCase(tagName, "head") || EqualsIgnoreCase(tagName, "br"))
                            AppendNewline(noteSb, ref noteLastWasNewline);
                        else
                            AppendText(noteSb, " ".AsSpan(), ref noteLastWasNewline);
                    }

                    // Only do segmentation/rendering while not in teiHeader and not in back and not in note capture
                    if (teiHeaderDepth == 0 && backDepth == 0 && !inNoteCapture)
                    {
                        // Segment boundary keys
                        if (TryMakeSyncKey(tagName, attrs, out var key))
                            StartNewSegment(key);

                        // Record note anchors in main text:
                        // <anchor xml:id="nkr_note_mod_0535011" .../>
                        if (EqualsIgnoreCase(tagName, "anchor"))
                        {
                            var id = Attr(attrs, AttrXmlId);
                            if (!string.IsNullOrWhiteSpace(id) && id.StartsWith("nkr_note_", StringComparison.Ordinal))
                            {
                                var kind = InferNoteKindFromId(id);
                                anchorPosById[id] = (sb.Length, kind);
                            }
                        }

                        // Inline notes: <note place="inline">...</note>
                        if (EqualsIgnoreCase(tagName, "note"))
                        {
                            var place = Attr(attrs, "place");
                            if (string.Equals(place, "inline", StringComparison.OrdinalIgnoreCase))
                            {
                                inNoteCapture = true;
                                noteSb.Clear();
                                noteLastWasNewline = false;

                                noteAnchorPos = sb.Length;
                                noteKind = Attr(attrs, "type") ?? "inline";
                            }
                        }

                        // Rendering structural breaks
                        if (EqualsIgnoreCase(tagName, "lb"))
                        {
                            AppendNewline(sb, ref lastWasNewline);
                        }
                        else if (EqualsIgnoreCase(tagName, "pb") ||
                                 EqualsIgnoreCase(tagName, "p") ||
                                 EqualsIgnoreCase(tagName, "head"))
                        {
                            EnsureParagraphBreak(sb, ref lastWasNewline);
                        }
                    }
                    else
                    {
                        // We are inside <back> (or header) — do NOT render, but we DO want to collect end-notes.
                        // End-notes format: <note ... target="#nkr_note_mod_0535011">...</note>
                        if (teiHeaderDepth == 0 && EqualsIgnoreCase(tagName, "note"))
                        {
                            var target = Attr(attrs, "target");
                            if (!string.IsNullOrWhiteSpace(target) && target[0] == '#')
                            {
                                var targetId = target.Substring(1);

                                // Only handle CBETA end-note style that has an anchor in body
                                if (targetId.StartsWith("nkr_note_", StringComparison.Ordinal))
                                {
                                    inNoteCapture = true;
                                    noteSb.Clear();
                                    noteLastWasNewline = false;

                                    if (anchorPosById.TryGetValue(targetId, out var hit))
                                    {
                                        noteAnchorPos = hit.Pos;
                                        noteKind = hit.Kind ?? Attr(attrs, "type");
                                    }
                                    else
                                    {
                                        // anchor missing (still capture text, but won't attach)
                                        noteAnchorPos = -1;
                                        noteKind = InferNoteKindFromId(targetId) ?? Attr(attrs, "type");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            i = gt + 1;
        }

        // Close last segment (main text only)
        int finalEnd = sb.Length;
        if (finalEnd > segStart)
            segments.Add(new RenderSegment(currentKey, segStart, finalEnd));

        // Build final text
        var text = sb.ToString();

        // Insert visible markers/superscripts into the rendered text.
        // (Your AnnotationMarkerInserter decides how markers look.)
        var (newText, newSegments, markers) =
            AnnotationMarkerInserter.InsertMarkers(text, annotations, segments);

        return new RenderedDocument(
            newText,
            newSegments,
            annotations,
            markers);
    }

    // ------------------------------------------------------------
    // Fast tag parsing (no regex, minimal allocations)
    // ------------------------------------------------------------

    private static bool TryParseTag(ReadOnlySpan<char> tag, out bool isEndTag, out ReadOnlySpan<char> tagName, out ReadOnlySpan<char> attrs)
    {
        isEndTag = false;
        tagName = default;
        attrs = default;

        // must start with < and end with >
        if (tag.Length < 3 || tag[0] != '<' || tag[^1] != '>')
            return false;

        int p = 1;

        // comments / PI / doctype etc => ignore
        char c1 = tag[p];
        if (c1 == '!' || c1 == '?')
            return false;

        if (c1 == '/')
        {
            isEndTag = true;
            p++;
        }

        // skip whitespace
        while (p < tag.Length && char.IsWhiteSpace(tag[p])) p++;
        if (p >= tag.Length - 1) return false;

        int nameStart = p;

        // tag name: letters + optional colon, e.g. cb:juan
        while (p < tag.Length - 1)
        {
            char ch = tag[p];
            if (char.IsLetter(ch) || ch == ':')
            {
                p++;
                continue;
            }
            break;
        }

        if (p == nameStart) return false;

        tagName = tag.Slice(nameStart, p - nameStart);

        // attrs start at current p, end before closing '>' (and before trailing '/')
        int attrStart = p;

        int attrEnd = tag.Length - 1; // index of '>'
        int q = attrEnd - 1;
        while (q > attrStart && char.IsWhiteSpace(tag[q])) q--;
        if (!isEndTag && q > attrStart && tag[q] == '/')
            attrEnd = q;

        attrs = tag.Slice(attrStart, attrEnd - attrStart);
        return true;
    }

    private static bool EqualsIgnoreCase(ReadOnlySpan<char> a, string b)
        => a.Equals(b.AsSpan(), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------
    // Text handling (normalize + optional entity decode, zero per-chunk strings)
    // ------------------------------------------------------------

    private static void AppendText(StringBuilder outSb, ReadOnlySpan<char> raw, ref bool lastWasNewline)
    {
        if (raw.Length == 0) return;

        int before = outSb.Length;

        bool wroteAny = false;
        bool pendingSpace = false;

        // emit char with whitespace collapsing + trim-start semantics
        void EmitChar(char c)
        {
            if (c == '\r') return;

            if (c == ' ' || c == '\t' || c == '\n' || c == '\f' || c == '\v')
            {
                pendingSpace = true;
                return;
            }

            if (pendingSpace)
            {
                if (wroteAny)
                {
                    if (outSb.Length > 0 && !char.IsWhiteSpace(outSb[outSb.Length - 1]))
                        outSb.Append(' ');
                }
                pendingSpace = false;
            }

            outSb.Append(c);
            wroteAny = true;
        }

        bool hadOutputBefore = before > 0;
        char prevChar = hadOutputBefore ? outSb[before - 1] : '\0';
        bool prevIsWs = hadOutputBefore && char.IsWhiteSpace(prevChar);

        // Scan raw span. Decode entities only if we see '&'.
        int i = 0;
        while (i < raw.Length)
        {
            char c = raw[i];

            if (c == '&')
            {
                if (TryDecodeEntity(raw, ref i, out var decodedChar, out var decodedString))
                {
                    if (decodedString != null)
                    {
                        for (int k = 0; k < decodedString.Length; k++)
                            EmitChar(decodedString[k]);
                    }
                    else
                    {
                        EmitChar(decodedChar);
                    }
                    continue;
                }

                // failed decode -> literal '&'
                EmitChar('&');
                i++;
                continue;
            }

            EmitChar(c);
            i++;
        }

        if (!wroteAny)
            return;

        // If previous output ends with non-ws and this appended chunk starts with non-ws, insert a space.
        if (hadOutputBefore && !prevIsWs)
        {
            int first = before;
            while (first < outSb.Length && char.IsWhiteSpace(outSb[first]))
                first++;

            if (first < outSb.Length && !char.IsWhiteSpace(outSb[first]))
                outSb.Insert(before, ' ');
        }

        lastWasNewline = outSb.Length > 0 && outSb[outSb.Length - 1] == '\n';
    }

    private static void AppendNewline(StringBuilder sb, ref bool lastWasNewline)
    {
        if (!lastWasNewline)
            sb.Append('\n');
        lastWasNewline = true;
    }

    private static void EnsureParagraphBreak(StringBuilder sb, ref bool lastWasNewline)
    {
        if (sb.Length == 0)
        {
            lastWasNewline = false;
            return;
        }

        while (sb.Length > 0 && (sb[^1] == ' ' || sb[^1] == '\t' || sb[^1] == '\r'))
            sb.Length--;

        int trailingNewlines = 0;
        for (int i = sb.Length - 1; i >= 0 && sb[i] == '\n'; i--)
            trailingNewlines++;

        if (trailingNewlines == 0) sb.Append("\n\n");
        else if (trailingNewlines == 1) sb.Append('\n');

        lastWasNewline = true;
    }

    /// <summary>
    /// Decodes &amp; &lt; &gt; &quot; &apos; plus numeric: &#123; and hex: &#x1F600;
    /// Advances i to the character after ';' on success.
    /// On failure, leaves i unchanged and returns false.
    /// </summary>
    private static bool TryDecodeEntity(ReadOnlySpan<char> s, ref int i, out char ch, out string? str)
    {
        ch = default;
        str = null;

        int start = i;
        if (i >= s.Length || s[i] != '&') return false;

        int semiRel = s.Slice(i).IndexOf(';');
        if (semiRel < 0) return false;
        int semi = i + semiRel;

        var ent = s.Slice(i + 1, semi - (i + 1));

        // named
        if (ent.SequenceEqual("amp".AsSpan())) { ch = '&'; i = semi + 1; return true; }
        if (ent.SequenceEqual("lt".AsSpan())) { ch = '<'; i = semi + 1; return true; }
        if (ent.SequenceEqual("gt".AsSpan())) { ch = '>'; i = semi + 1; return true; }
        if (ent.SequenceEqual("quot".AsSpan())) { ch = '"'; i = semi + 1; return true; }
        if (ent.SequenceEqual("apos".AsSpan())) { ch = '\''; i = semi + 1; return true; }

        // numeric: &#...; or &#x...;
        if (ent.Length >= 2 && ent[0] == '#')
        {
            bool hex = ent.Length >= 3 && (ent[1] == 'x' || ent[1] == 'X');
            int value = 0;

            try
            {
                if (hex)
                {
                    for (int k = 2; k < ent.Length; k++)
                    {
                        int d = HexVal(ent[k]);
                        if (d < 0) { i = start; return false; }
                        value = checked(value * 16 + d);
                    }
                }
                else
                {
                    for (int k = 1; k < ent.Length; k++)
                    {
                        char c = ent[k];
                        if (c < '0' || c > '9') { i = start; return false; }
                        value = checked(value * 10 + (c - '0'));
                    }
                }
            }
            catch (OverflowException)
            {
                i = start;
                return false;
            }

            if (value <= 0) { i = start; return false; }

            if (value <= 0xFFFF)
            {
                ch = (char)value;
                i = semi + 1;
                return true;
            }

            if (value <= 0x10FFFF)
            {
                value -= 0x10000;
                char high = (char)((value >> 10) + 0xD800);
                char low = (char)((value & 0x3FF) + 0xDC00);
                str = new string(new[] { high, low });
                i = semi + 1;
                return true;
            }

            i = start;
            return false;
        }

        i = start;
        return false;
    }

    private static int HexVal(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
        if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
        return -1;
    }

    // ------------------------------------------------------------
    // Segment keys + attribute parsing (no regex)
    // ------------------------------------------------------------

    // NOTE: const string is safest/fastest here (no illegal readonly properties, no static span lifetime concerns)
    private const string AttrN = "n";
    private const string AttrEd = "ed";
    private const string AttrXmlId = "xml:id";
    private const string AttrFun = "fun";

    private static bool TryMakeSyncKey(ReadOnlySpan<char> tagName, ReadOnlySpan<char> attrs, out string key)
    {
        key = "";

        if (EqualsIgnoreCase(tagName, "lb"))
        {
            var n = Attr(attrs, AttrN);
            var ed = Attr(attrs, AttrEd);
            key = MakeKey("lb", n, ed);
            return true;
        }

        if (EqualsIgnoreCase(tagName, "pb"))
        {
            var id = Attr(attrs, AttrXmlId) ?? Attr(attrs, AttrN);
            var ed = Attr(attrs, AttrEd);
            key = MakeKey("pb", id, ed);
            return true;
        }

        if (EqualsIgnoreCase(tagName, "p"))
        {
            var id = Attr(attrs, AttrXmlId);
            if (string.IsNullOrWhiteSpace(id)) return false;
            key = MakeKey("p", id);
            return true;
        }

        if (EqualsIgnoreCase(tagName, "anchor"))
        {
            var id = Attr(attrs, AttrXmlId) ?? Attr(attrs, AttrN);
            if (string.IsNullOrWhiteSpace(id)) return false;
            key = MakeKey("anchor", id);
            return true;
        }

        if (EqualsIgnoreCase(tagName, "cb:juan"))
        {
            var n = Attr(attrs, AttrN);
            var fun = Attr(attrs, AttrFun);
            key = MakeKey("cb:juan", n, fun);
            return true;
        }

        return false;
    }

    private static string MakeKey(string baseName, params string?[] parts)
    {
        var filtered = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList();
        if (filtered.Count == 0) return baseName;
        return $"{baseName}|{string.Join("|", filtered)}";
    }

    /// <summary>
    /// Extract attribute value from an attribute span.
    /// Supports double quotes only (which matches CBETA TEI).
    /// Returns null if not found.
    /// </summary>
    private static string? Attr(ReadOnlySpan<char> attrs, string attrName)
    {
        if (attrs.Length == 0 || string.IsNullOrEmpty(attrName))
            return null;

        ReadOnlySpan<char> needle = attrName.AsSpan();

        int i = 0;
        while (i < attrs.Length)
        {
            while (i < attrs.Length && char.IsWhiteSpace(attrs[i])) i++;
            if (i >= attrs.Length) break;

            int eqRel = attrs.Slice(i).IndexOf('=');
            if (eqRel < 0) break;
            int eq = i + eqRel;

            int nameEnd = eq - 1;
            while (nameEnd >= i && char.IsWhiteSpace(attrs[nameEnd])) nameEnd--;
            if (nameEnd < i) { i = eq + 1; continue; }

            var candName = attrs.Slice(i, nameEnd - i + 1);

            int j = eq + 1;
            while (j < attrs.Length && char.IsWhiteSpace(attrs[j])) j++;
            if (j >= attrs.Length || attrs[j] != '"')
            {
                i = eq + 1;
                continue;
            }

            j++;
            int start = j;
            int endRel = attrs.Slice(start).IndexOf('"');
            if (endRel < 0) return null;
            int end = start + endRel;

            if (candName.Equals(needle, StringComparison.Ordinal))
                return attrs.Slice(start, end - start).ToString();

            i = end + 1;
        }

        return null;
    }

    private static string? InferNoteKindFromId(string id)
    {
        // examples:
        // nkr_note_mod_0535011
        // nkr_note_orig_0535011
        // nkr_note_add_0528b0901
        if (id.StartsWith("nkr_note_mod_", StringComparison.Ordinal)) return "mod";
        if (id.StartsWith("nkr_note_orig_", StringComparison.Ordinal)) return "orig";
        if (id.StartsWith("nkr_note_add_", StringComparison.Ordinal)) return "add";
        return null;
    }
}
