using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace CbetaTranslator.App.Views;

public partial class TranslationTabView : UserControl
{
    private Button? _btnCopyPrompt;
    private Button? _btnPasteReplace;
    private Button? _btnSaveTranslated;
    private Button? _btnSelectNext50Tags;
    private Button? _btnCheckXml;

    private TextBox? _orig;
    private TextBox? _tran;
    private TextBlock? _txtHint;

    // Remember last "copy selection" range (so paste can work even if selection got lost)
    private int _lastCopyStart = -1;
    private int _lastCopyEnd = -1;

    public event EventHandler? SaveRequested;
    public event EventHandler<string>? Status;

    public TranslationTabView()
    {
        InitializeComponent();
        FindControls();
        WireEvents();
        UpdateHint("Select XML in Translated XML → Copy selection + prompt.");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void FindControls()
    {
        _btnCopyPrompt = this.FindControl<Button>("BtnCopyPrompt");
        _btnPasteReplace = this.FindControl<Button>("BtnPasteReplace");
        _btnSaveTranslated = this.FindControl<Button>("BtnSaveTranslated");
        _btnSelectNext50Tags = this.FindControl<Button>("BtnSelectNext50Tags");
        _btnCheckXml = this.FindControl<Button>("BtnCheckXml");

        _orig = this.FindControl<TextBox>("EditorOrigXml");
        _tran = this.FindControl<TextBox>("EditorTranXml");

        _txtHint = this.FindControl<TextBlock>("TxtHint");

        if (_orig != null) _orig.IsReadOnly = true;
        if (_tran != null) _tran.IsReadOnly = false;
    }

    private void WireEvents()
    {
        if (_btnCopyPrompt != null) _btnCopyPrompt.Click += async (_, _) => await CopySelectionWithPromptAsync();
        if (_btnPasteReplace != null) _btnPasteReplace.Click += async (_, _) => await PasteReplaceSelectionAsync();
        if (_btnSaveTranslated != null) _btnSaveTranslated.Click += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty);
        if (_btnSelectNext50Tags != null) _btnSelectNext50Tags.Click += async (_, _) => await SelectNextTagsAsync(50);
        if (_btnCheckXml != null) _btnCheckXml.Click += async (_, _) => await CheckXmlHackyAsync();

        // Track selection range using user input events (Avalonia TextBox has no SelectionChanged event)
        if (_tran != null)
        {
            _tran.PointerReleased += (_, _) => RememberSelectionIfAny();
            _tran.KeyUp += (_, _) => RememberSelectionIfAny();
        }
    }

    private void RememberSelectionIfAny()
    {
        if (_tran == null) return;

        int s = _tran.SelectionStart;
        int e = _tran.SelectionEnd;

        if (e > s)
        {
            _lastCopyStart = s;
            _lastCopyEnd = e;
        }
    }

    public void Clear()
    {
        if (_orig != null) _orig.Text = "";
        if (_tran != null) _tran.Text = "";

        _lastCopyStart = -1;
        _lastCopyEnd = -1;
        ResetNavigationState();
        UpdateHint("Select a file to edit XML.");
    }

    public void SetXml(string originalXml, string translatedXml)
    {
        if (_orig != null) _orig.Text = originalXml ?? "";
        if (_tran != null) _tran.Text = translatedXml ?? "";

        _lastCopyStart = -1;
        _lastCopyEnd = -1;
        ResetNavigationState();
        UpdateHint("Tip: select a chunk in Translated XML → Copy selection + prompt.");
    }

    public string GetTranslatedXml()
        => _tran?.Text ?? string.Empty;

    // --------------------------
    // Clipboard workflow
    // --------------------------

    private async Task CopySelectionWithPromptAsync()
    {
        if (_tran == null)
        {
            Status?.Invoke(this, "Translated XML editor not available.");
            return;
        }

        var text = _tran.Text ?? "";
        if (text.Length == 0)
        {
            Status?.Invoke(this, "Translated XML is empty.");
            return;
        }

        // Prefer current selection; if it vanished due to button focus, fallback to last remembered range.
        int start = _tran.SelectionStart;
        int end = _tran.SelectionEnd;

        if (end <= start)
        {
            if (_lastCopyEnd > _lastCopyStart)
            {
                start = _lastCopyStart;
                end = _lastCopyEnd;
            }
        }

        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, 0, text.Length);
        if (end <= start)
        {
            Status?.Invoke(this, "No selection. Select an XML fragment first.");
            return;
        }

        string selectionXml = text.Substring(start, end - start);

        // Keep memory updated (important if clamp changed things)
        _lastCopyStart = start;
        _lastCopyEnd = end;

        string clipboardPayload = BuildChatGptPrompt(selectionXml);

        var clipboard = GetClipboard();
        if (clipboard == null)
        {
            Status?.Invoke(this, "Clipboard not available (TopLevel.Clipboard is null).");
            return;
        }

        await clipboard.SetTextAsync(clipboardPayload);
        Status?.Invoke(this, $"Copied selection + prompt ({selectionXml.Length:n0} chars) to clipboard.");
    }

    private async Task PasteReplaceSelectionAsync()
    {
        if (_tran == null)
        {
            Status?.Invoke(this, "Translated XML editor not available.");
            return;
        }

        var clipboard = GetClipboard();
        if (clipboard == null)
        {
            Status?.Invoke(this, "Clipboard not available (TopLevel.Clipboard is null).");
            return;
        }

        // Avalonia 11: prefer TryGetTextAsync
        var clipText = await clipboard.TryGetTextAsync() ?? "";
        clipText = clipText.Trim();

        if (clipText.Length == 0)
        {
            Status?.Invoke(this, "Clipboard is empty.");
            return;
        }

        string pastedXml = ExtractXmlFromClipboard(clipText);
        if (string.IsNullOrWhiteSpace(pastedXml))
        {
            Status?.Invoke(this, "Could not find XML in clipboard text (no ```xml``` block, and content wasn't raw XML).");
            return;
        }

        // Prefer current selection, else fallback to last copied range
        int start = _tran.SelectionStart;
        int end = _tran.SelectionEnd;

        if (end <= start)
        {
            if (_lastCopyEnd > _lastCopyStart)
            {
                start = _lastCopyStart;
                end = _lastCopyEnd;
            }
            else
            {
                Status?.Invoke(this, "No active selection (and no remembered copy range). Select where to paste.");
                return;
            }
        }

        var all = _tran.Text ?? "";
        start = Math.Clamp(start, 0, all.Length);
        end = Math.Clamp(end, 0, all.Length);
        if (end < start) (start, end) = (end, start);

        var sb = new StringBuilder(all.Length - (end - start) + pastedXml.Length);
        sb.Append(all, 0, start);
        sb.Append(pastedXml);
        sb.Append(all, end, all.Length - end);

        _tran.Text = sb.ToString();

        // Reselect inserted text
        _tran.SelectionStart = start;
        _tran.SelectionEnd = start + pastedXml.Length;
        try { _tran.CaretIndex = start; } catch { /* ignore */ }

        // Update memory so "Select next 50 tags" continues from here smoothly
        _lastCopyStart = start;
        _lastCopyEnd = start + pastedXml.Length;

        Status?.Invoke(this, $"Pasted & replaced selection with {pastedXml.Length:n0} chars.");
    }

    // --------------------------
    // Helpers
    // --------------------------

    private IClipboard? GetClipboard()
    {
        var top = TopLevel.GetTopLevel(this);
        return top?.Clipboard;
    }

    private void UpdateHint(string s)
    {
        if (_txtHint != null) _txtHint.Text = s;
    }

    private static string BuildChatGptPrompt(string selectionXml)
    {
        return
$@"You are an XML-preserving translator.

ABSOLUTE TARGET LANGUAGE:
- Translate into ENGLISH ONLY.
- Do NOT rewrite or paraphrase text that is already in English; keep existing English EXACTLY as-is.

NON-NEGOTIABLE RULES (STRICT SPEC):

1) XML STRUCTURE MUST BE PRESERVED EXACTLY
   - Every start tag, end tag, self-closing tag, attribute name/value, namespace prefix,
     entity reference, CDATA marker, processing instruction, and tag order must remain IDENTICAL.
   - Do NOT add, remove, reorder, rename, or reformat any tags or attributes.
   - Do NOT move text across tag boundaries.
   - Do NOT duplicate the fragment or append a second copy.
   - Output must be well-formed XML.

2) TRANSLATE TEXT NODES ONLY (HUMAN-READABLE NATURAL LANGUAGE)
   - Translate ONLY natural-language text inside text nodes.
   - NEVER translate or modify:
     • tag names
     • attribute names or values
     • IDs, codes, catalog numbers, dates/times, line numbers, refs, witness marks (e.g., 【CB】), or other non-prose tokens
     • any text that is already English (leave it exactly unchanged)
   - You MUST translate ALL non-empty Chinese/Japanese/Korean (CJK) natural-language text that appears in text nodes.
   - You MAY smooth sentence flow across line-break tags (e.g. <lb/>, <pb/>) ONLY by choosing English wording that reads naturally,
     but you may NOT move text across tags and may NOT change the punctuation structure (do not add/remove sentence-ending punctuation).

3) WHITESPACE / PUNCTUATION PRESERVATION
   - Keep whitespace/newlines as close as possible to the input (do NOT rewrap or normalize).
   - Preserve the existing punctuation structure: do NOT add or remove punctuation marks (.,;:!?、。 etc.).
   - Do NOT add explanatory parentheses, glosses, or extra words like ""(i.e.)"".

4) SILENT INTERNAL SELF-CHECK (MANDATORY)
   - Check that the sequence and count of '<...>' XML tokens in your output EXACTLY matches the input.
   - Check that all attributes, namespaces, and processing instructions are unchanged.
   - Check that NO CJK characters remain in text nodes that should have been translated.
   - Check that you did NOT translate any existing English text.
   - If ANY check fails, output the ORIGINAL XML UNCHANGED (verbatim).

OUTPUT REQUIREMENTS:
- Output ONLY the XML fragment (exactly one copy).
- Put the entire output in ONE single ```xml code block``` and NOTHING ELSE.

XML fragment to translate:
```xml
{selectionXml}
```";
    }

    private static string ExtractXmlFromClipboard(string clipboardText)
    {
        // Extract first ```xml ... ``` block if present
        var m = Regex.Match(
            clipboardText,
            @"```(?:xml)?\s*(?<xml>[\s\S]*?)\s*```",
            RegexOptions.IgnoreCase);

        if (m.Success)
            return m.Groups["xml"].Value.Trim();

        // Fallback: assume raw XML
        return clipboardText.Trim();
    }

    // Precompiled tag regex (fast + consistent)
    private static readonly Regex XmlTagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);

    private async Task SelectNextTagsAsync(int tagCount)
    {
        if (_tran == null)
        {
            Status?.Invoke(this, "Translated XML editor not available.");
            return;
        }

        var text = _tran.Text ?? "";
        if (text.Length == 0)
        {
            Status?.Invoke(this, "Translated XML is empty.");
            return;
        }

        // Smooth workflow start:
        // 1) If there's an active selection: continue after it.
        // 2) Else if we have a remembered range: continue after it.
        // 3) Else: use caret index.
        int start;
        int selStart = _tran.SelectionStart;
        int selEnd = _tran.SelectionEnd;

        if (selEnd > selStart)
        {
            start = selEnd;
            _lastCopyStart = selStart;
            _lastCopyEnd = selEnd;
        }
        else if (_lastCopyEnd > _lastCopyStart)
        {
            start = _lastCopyEnd;
        }
        else
        {
            start = _tran.CaretIndex;
        }

        start = Math.Clamp(start, 0, text.Length);

        // Find tags starting at 'start'
        var matches = XmlTagRegex.Matches(text, start);
        if (matches.Count == 0)
        {
            await ShowInfoPopupAsync("End reached", "No more XML tags found after the current position.");
            return;
        }

        // ALWAYS aim for exactly tagCount tags, unless fewer remain.
        int take = Math.Min(tagCount, matches.Count);
        var last = matches[take - 1];

        int end = last.Index + last.Length;
        end = Math.Clamp(end, 0, text.Length);

        // Extend to a clean boundary: go forward to the next newline (if any)
        end = ExtendToNextNewline(text, end);

        if (end <= start)
        {
            await ShowInfoPopupAsync("Selection failed", "Could not compute a valid selection range.");
            return;
        }

        _tran.Focus();
        _tran.SelectionStart = start;
        _tran.SelectionEnd = end;
        try { _tran.CaretIndex = start; } catch { /* ignore */ }

        _lastCopyStart = start;
        _lastCopyEnd = end;

        Status?.Invoke(this, $"Selected {take} tag(s) + newline boundary ({end - start:n0} chars).");
    }

    private static int ExtendToNextNewline(string text, int end)
    {
        if (string.IsNullOrEmpty(text)) return end;
        end = Math.Clamp(end, 0, text.Length);
        if (end >= text.Length) return text.Length;

        const int MaxScan = 4000; // safety: don't scan the whole file
        int scanLimit = Math.Min(text.Length, end + MaxScan);

        for (int i = end; i < scanLimit; i++)
        {
            if (text[i] == '\n')
                return i + 1; // include newline
        }

        // If we didn't find a newline nearby, but there is whitespace right after end, eat a bit of it
        int j = end;
        while (j < text.Length && (text[j] == ' ' || text[j] == '\t' || text[j] == '\r'))
            j++;

        return j;
    }

    // --------------------------
    // Hacky XML check (no parser)
    // --------------------------

    // We care most about preserving <lb n="..." ed="..."/> pairs.
    // This finds ONLY <lb ...> tags (including <lb .../>).
    private static readonly Regex LbTagRegex = new Regex(
        @"<lb\b(?<attrs>[^>]*)\/?>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Extracts n="..." and ed="..." in any order inside the tag.
    private static readonly Regex AttrRegex = new Regex(
        @"\b(?<name>n|ed)\s*=\s*""(?<val>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static (int totalLb, Dictionary<string, int> sigCounts) CollectLbSignatures(string xml)
    {
        int total = 0;
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match m in LbTagRegex.Matches(xml))
        {
            total++;

            string attrs = m.Groups["attrs"].Value;

            string? nVal = null;
            string? edVal = null;

            foreach (Match am in AttrRegex.Matches(attrs))
            {
                var name = am.Groups["name"].Value;
                var val = am.Groups["val"].Value;

                if (name.Equals("n", StringComparison.OrdinalIgnoreCase)) nVal = val;
                else if (name.Equals("ed", StringComparison.OrdinalIgnoreCase)) edVal = val;
            }

            // Build a stable signature. Missing attrs are still meaningful.
            string sig = $"n={nVal ?? "<missing>"}|ed={edVal ?? "<missing>"}";

            if (dict.TryGetValue(sig, out int c)) dict[sig] = c + 1;
            else dict[sig] = 1;
        }

        return (total, dict);
    }

    private async Task CheckXmlHackyAsync()
    {
        if (_orig == null || _tran == null)
        {
            Status?.Invoke(this, "Editors not available.");
            return;
        }

        var orig = _orig.Text ?? "";
        var tran = _tran.Text ?? "";

        if (orig.Length == 0)
        {
            await ShowInfoPopupAsync("Check XML", "Original XML is empty. Nothing to compare.");
            return;
        }

        // Basic tag count check
        int origTagCount = XmlTagRegex.Matches(orig).Count;
        int tranTagCount = XmlTagRegex.Matches(tran).Count;

        // LB signature check
        var (origLbTotal, origSigs) = CollectLbSignatures(orig);
        var (tranLbTotal, tranSigs) = CollectLbSignatures(tran);

        // Compare signatures (keys)
        var missing = origSigs.Keys.Where(k => !tranSigs.ContainsKey(k)).ToList();
        var extra = tranSigs.Keys.Where(k => !origSigs.ContainsKey(k)).ToList();

        // Compare counts for common keys
        var countDiffs = new List<string>();
        foreach (var k in origSigs.Keys.Intersect(tranSigs.Keys))
        {
            int a = origSigs[k];
            int b = tranSigs[k];
            if (a != b)
                countDiffs.Add($"{k}  original={a}  translated={b}");
        }

        var problems = new List<string>();

        if (origTagCount != tranTagCount)
            problems.Add($"TAG COUNT MISMATCH:\n  original={origTagCount:n0}\n  translated={tranTagCount:n0}");

        if (origLbTotal != tranLbTotal)
            problems.Add($"LB TOTAL MISMATCH:\n  original={origLbTotal:n0}\n  translated={tranLbTotal:n0}");

        if (missing.Count > 0)
            problems.Add($"MISSING <lb> SIGNATURES in translated: {missing.Count:n0}\n(showing up to 15)\n- {string.Join("\n- ", missing.Take(15))}");

        if (extra.Count > 0)
            problems.Add($"EXTRA <lb> SIGNATURES in translated: {extra.Count:n0}\n(showing up to 15)\n- {string.Join("\n- ", extra.Take(15))}");

        if (countDiffs.Count > 0)
            problems.Add($"<lb> SIGNATURE COUNT DIFFERENCES: {countDiffs.Count:n0}\n(showing up to 15)\n- {string.Join("\n- ", countDiffs.Take(15))}");

        if (problems.Count == 0)
        {
            Status?.Invoke(this, $"XML check OK: tags={tranTagCount:n0}, lb={tranLbTotal:n0} (n/ed preserved).");
            await ShowInfoPopupAsync(
                "Check XML",
                $"OK ✅\n\nTag count matches: {tranTagCount:n0}\n<lb> count matches: {tranLbTotal:n0}\nAll <lb n=... ed=...> signatures match.\n\n(Hacky structural check only; not a full XML validator.)");
            return;
        }

        Status?.Invoke(this, "XML check failed (see popup).");
        await ShowInfoPopupAsync("Check XML (hacky)", string.Join("\n\n", problems));
    }

    // --------------------------
    // Popup (awaited)
    // --------------------------

    private async Task ShowInfoPopupAsync(string title, string message)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;

        var ok = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 80
        };

        var text = new TextBox
        {
            Text = message,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Height = 240
        };

        // Use attached properties; no TextBox.VerticalScrollBarVisibility in Avalonia
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(text, ScrollBarVisibility.Disabled);

        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 10
        };

        panel.Children.Add(text);
        panel.Children.Add(ok);

        var win = new Window
        {
            Title = title,
            Width = 700,
            Height = 380,
            Content = panel,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen
        };

        var tcs = new TaskCompletionSource<bool>();
        ok.Click += (_, _) => { win.Close(); tcs.TrySetResult(true); };

        if (owner != null)
            await win.ShowDialog(owner); // ✅ proper await
        else
            win.Show();

        await tcs.Task;
    }

    private void ResetNavigationState()
    {
        if (_tran == null) return;

        _lastCopyStart = -1;
        _lastCopyEnd = -1;

        // Reset caret/selection to start so chunking begins predictably.
        try
        {
            _tran.SelectionStart = 0;
            _tran.SelectionEnd = 0;
            _tran.CaretIndex = 0;
        }
        catch { /* ignore */ }

        // Optional but nice: focus
        try { _tran.Focus(); } catch { /* ignore */ }
    }
}
