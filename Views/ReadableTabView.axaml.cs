using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CbetaTranslator.App.Models;
using CbetaTranslator.App.Services;

namespace CbetaTranslator.App.Views;

public partial class ReadableTabView : UserControl
{
    private TextBox? _editorOriginal;
    private TextBox? _editorTranslated;

    private ScrollViewer? _svOriginal;
    private ScrollViewer? _svTranslated;

    private readonly ISelectionSyncService _selectionSync = new SelectionSyncService();

    private RenderedDocument _renderOrig = RenderedDocument.Empty;
    private RenderedDocument _renderTran = RenderedDocument.Empty;

    private bool _syncingSelection;

    private DateTime _ignoreProgrammaticUntilUtc = DateTime.MinValue;
    private const int IgnoreProgrammaticWindowMs = 180;

    private DateTime _suppressPollingUntilUtc = DateTime.MinValue;
    private const int SuppressPollingAfterUserActionMs = 220;

    private DispatcherTimer? _selTimer;
    private int _lastOrigSelStart = -1, _lastOrigSelEnd = -1;
    private int _lastTranSelStart = -1, _lastTranSelEnd = -1;
    private int _lastOrigCaret = -1, _lastTranCaret = -1;

    private DateTime _lastUserInputUtc = DateTime.MinValue;
    private TextBox? _lastUserInputEditor;
    private const int UserInputPriorityWindowMs = 250;

    private const int JumpFallbackExtraLines = 8;

    public ReadableTabView()
    {
        InitializeComponent();
        FindControls();
        WireEvents();

        AttachedToVisualTree += (_, _) =>
        {
            _svOriginal = FindScrollViewer(_editorOriginal);
            _svTranslated = FindScrollViewer(_editorTranslated);
            StartSelectionTimer();
        };

        DetachedFromVisualTree += (_, _) => StopSelectionTimer();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void FindControls()
    {
        _editorOriginal = this.FindControl<TextBox>("EditorOriginal");
        _editorTranslated = this.FindControl<TextBox>("EditorTranslated");

        if (_editorOriginal != null) _editorOriginal.IsReadOnly = true;
        if (_editorTranslated != null) _editorTranslated.IsReadOnly = true;
    }

    private void WireEvents()
    {
        HookUserInputTracking(_editorOriginal);
        HookUserInputTracking(_editorTranslated);
    }

    public void Clear()
    {
        _renderOrig = RenderedDocument.Empty;
        _renderTran = RenderedDocument.Empty;

        if (_editorOriginal != null) _editorOriginal.Text = "";
        if (_editorTranslated != null) _editorTranslated.Text = "";

        _lastOrigSelStart = _lastOrigSelEnd = -1;
        _lastTranSelStart = _lastTranSelEnd = -1;
        _lastOrigCaret = _lastTranCaret = -1;

        ResetScroll(_svOriginal);
        ResetScroll(_svTranslated);
    }

    public void SetRendered(RenderedDocument orig, RenderedDocument tran)
    {
        _renderOrig = orig ?? RenderedDocument.Empty;
        _renderTran = tran ?? RenderedDocument.Empty;

        if (_editorOriginal != null) _editorOriginal.Text = _renderOrig.Text;
        if (_editorTranslated != null) _editorTranslated.Text = _renderTran.Text;

        ResetScroll(_svOriginal);
        ResetScroll(_svTranslated);

        if (_editorOriginal != null)
        {
            _lastOrigSelStart = _editorOriginal.SelectionStart;
            _lastOrigSelEnd = _editorOriginal.SelectionEnd;
            _lastOrigCaret = _editorOriginal.CaretIndex;
        }

        if (_editorTranslated != null)
        {
            _lastTranSelStart = _editorTranslated.SelectionStart;
            _lastTranSelEnd = _editorTranslated.SelectionEnd;
            _lastTranCaret = _editorTranslated.CaretIndex;
        }
    }

    private void HookUserInputTracking(TextBox? tb)
    {
        if (tb == null) return;

        tb.PointerPressed += OnEditorUserInput;
        tb.PointerReleased += OnEditorPointerReleased;
        tb.KeyDown += OnEditorUserInput;
        tb.KeyUp += OnEditorKeyUp;
    }

    private void OnEditorUserInput(object? sender, EventArgs e)
    {
        _lastUserInputUtc = DateTime.UtcNow;
        _lastUserInputEditor = sender as TextBox;
    }

    private void OnEditorPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _lastUserInputUtc = DateTime.UtcNow;
        _lastUserInputEditor = sender as TextBox;

        bool sourceIsTranslated = ReferenceEquals(_lastUserInputEditor, _editorTranslated);

        _suppressPollingUntilUtc = DateTime.UtcNow.AddMilliseconds(SuppressPollingAfterUserActionMs);
        RequestMirrorFromUserAction(sourceIsTranslated);
    }

    private void OnEditorKeyUp(object? sender, KeyEventArgs e)
    {
        _lastUserInputUtc = DateTime.UtcNow;
        _lastUserInputEditor = sender as TextBox;

        bool sourceIsTranslated = ReferenceEquals(_lastUserInputEditor, _editorTranslated);

        _suppressPollingUntilUtc = DateTime.UtcNow.AddMilliseconds(SuppressPollingAfterUserActionMs);
        RequestMirrorFromUserAction(sourceIsTranslated);
    }

    // -------------------------
    // Polling + mirroring
    // -------------------------

    private void StartSelectionTimer()
    {
        if (_selTimer != null) return;

        _selTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _selTimer.Tick += (_, _) => PollSelectionChanges();
        _selTimer.Start();
    }

    private void StopSelectionTimer()
    {
        if (_selTimer == null) return;
        _selTimer.Stop();
        _selTimer = null;
    }

    private void PollSelectionChanges()
    {
        if (DateTime.UtcNow <= _suppressPollingUntilUtc) return;
        if (_syncingSelection) return;
        if (DateTime.UtcNow <= _ignoreProgrammaticUntilUtc) return;

        if (_editorOriginal == null || _editorTranslated == null) return;
        if (_renderOrig.IsEmpty || _renderTran.IsEmpty) return;

        int oS = _editorOriginal.SelectionStart;
        int oE = _editorOriginal.SelectionEnd;
        int tS = _editorTranslated.SelectionStart;
        int tE = _editorTranslated.SelectionEnd;
        int oC = _editorOriginal.CaretIndex;
        int tC = _editorTranslated.CaretIndex;

        bool origSelChanged = (oS != _lastOrigSelStart) || (oE != _lastOrigSelEnd);
        bool tranSelChanged = (tS != _lastTranSelStart) || (tE != _lastTranSelEnd);
        bool origCaretChanged = (oC != _lastOrigCaret);
        bool tranCaretChanged = (tC != _lastTranCaret);

        if (!origSelChanged && !tranSelChanged && !origCaretChanged && !tranCaretChanged)
            return;

        _lastOrigSelStart = oS;
        _lastOrigSelEnd = oE;
        _lastTranSelStart = tS;
        _lastTranSelEnd = tE;
        _lastOrigCaret = oC;
        _lastTranCaret = tC;

        bool sourceIsTranslated = DetermineSourcePane(origSelChanged || origCaretChanged, tranSelChanged || tranCaretChanged);
        MirrorSelectionOneWay(sourceIsTranslated);
    }

    private bool DetermineSourcePane(bool origChanged, bool tranChanged)
    {
        if (_editorOriginal == null || _editorTranslated == null)
            return true;

        bool origFocused = _editorOriginal.IsFocused || _editorOriginal.IsKeyboardFocusWithin;
        bool tranFocused = _editorTranslated.IsFocused || _editorTranslated.IsKeyboardFocusWithin;

        bool recentInput = (DateTime.UtcNow - _lastUserInputUtc).TotalMilliseconds <= UserInputPriorityWindowMs;

        if (origFocused && !tranFocused) return false;
        if (tranFocused && !origFocused) return true;

        if (origChanged && !tranChanged) return false;
        if (tranChanged && !origChanged) return true;

        if (recentInput && _lastUserInputEditor != null)
            return ReferenceEquals(_lastUserInputEditor, _editorTranslated);

        if (tranFocused) return true;
        if (origFocused) return false;

        return true;
    }

    private void MirrorSelectionOneWay(bool sourceIsTranslated)
    {
        if (_editorOriginal == null || _editorTranslated == null) return;
        if (_renderOrig.IsEmpty || _renderTran.IsEmpty) return;

        var srcEditor = sourceIsTranslated ? _editorTranslated : _editorOriginal;
        var dstEditor = sourceIsTranslated ? _editorOriginal : _editorTranslated;

        var srcDoc = sourceIsTranslated ? _renderTran : _renderOrig;
        var dstDoc = sourceIsTranslated ? _renderOrig : _renderTran;

        int caret = srcEditor.CaretIndex;

        if (!_selectionSync.TryGetDestinationSegment(srcDoc, dstDoc, caret, out var dstSeg))
            return;

        bool destinationIsChinese = ReferenceEquals(dstEditor, _editorOriginal);

        try
        {
            _syncingSelection = true;

            ApplyDestinationSelection(dstEditor, dstSeg.Start, dstSeg.EndExclusive, center: true, destinationIsChinese);

            if (ReferenceEquals(dstEditor, _editorOriginal))
            {
                _lastOrigSelStart = dstEditor.SelectionStart;
                _lastOrigSelEnd = dstEditor.SelectionEnd;
                _lastOrigCaret = dstEditor.CaretIndex;
            }
            else
            {
                _lastTranSelStart = dstEditor.SelectionStart;
                _lastTranSelEnd = dstEditor.SelectionEnd;
                _lastTranCaret = dstEditor.CaretIndex;
            }

            _ignoreProgrammaticUntilUtc = DateTime.UtcNow.AddMilliseconds(IgnoreProgrammaticWindowMs);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void ApplyDestinationSelection(TextBox dst, int start, int endExclusive, bool center, bool destinationIsChinese)
    {
        int len = dst.Text?.Length ?? 0;
        start = Math.Max(0, Math.Min(start, len));
        endExclusive = Math.Max(0, Math.Min(endExclusive, len));
        if (endExclusive < start) (start, endExclusive) = (endExclusive, start);

        dst.SelectionStart = start;
        dst.SelectionEnd = endExclusive;

        try { dst.CaretIndex = start; } catch { /* ignore */ }

        if (!center) return;

        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                if (destinationIsChinese) CenterByNewlines(dst, start);
                else CenterByCaretRect(dst, start);
            }
            catch { /* ignore */ }
        }, TimeSpan.FromMilliseconds(20));

        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                if (destinationIsChinese) CenterByNewlines(dst, start);
                else CenterByCaretRect(dst, start);
            }
            catch { /* ignore */ }
        }, TimeSpan.FromMilliseconds(55));
    }

    // -------------------------
    // Scroll helpers
    // -------------------------

    private static void ResetScroll(ScrollViewer? sv)
    {
        if (sv == null) return;
        sv.Offset = new Vector(0, 0);
    }

    private static ScrollViewer? FindScrollViewer(Control? c)
    {
        if (c == null) return null;
        return c.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    }

    private static bool IsFinitePositive(double v)
        => !double.IsNaN(v) && !double.IsInfinity(v) && v > 0;

    private static double ClampY(double extentH, double viewportH, double y)
    {
        if (IsFinitePositive(extentH) && IsFinitePositive(viewportH))
        {
            double maxY = Math.Max(0, extentH - viewportH);
            return Math.Max(0, Math.Min(y, maxY));
        }
        return Math.Max(0, y);
    }

    private static void CenterByNewlines(TextBox tb, int caretIndex)
    {
        var sv = FindScrollViewer(tb);
        if (sv == null) return;

        double viewportH = sv.Viewport.Height;
        double extentH = sv.Extent.Height;

        double lineH = Math.Max(12.0, tb.FontSize * 1.35);

        string text = tb.Text ?? "";
        int lineIndex = CountNewlinesUpTo(text, caretIndex);

        if (!IsFinitePositive(viewportH))
        {
            double y = Math.Max(0, (lineIndex - JumpFallbackExtraLines) * lineH);
            sv.Offset = new Vector(sv.Offset.X, y);
            return;
        }

        double visibleLines = viewportH / lineH;
        double topLine = Math.Max(0, lineIndex - (visibleLines / 2.0));
        double desiredY = topLine * lineH;

        desiredY = ClampY(extentH, viewportH, desiredY);
        sv.Offset = new Vector(sv.Offset.X, desiredY);
    }

    private static void CenterByCaretRect(TextBox tb, int caretIndex)
    {
        var sv = FindScrollViewer(tb);
        if (sv == null) return;

        double viewportH = sv.Viewport.Height;
        double extentH = sv.Extent.Height;

        if (!IsFinitePositive(viewportH))
            return;

        if (!TryGetCaretYInScrollViewer(tb, sv, caretIndex, out double caretY))
            return;

        double targetY = viewportH / 2.0;
        double delta = caretY - targetY;

        if (Math.Abs(delta) <= Math.Max(6.0, viewportH * 0.03))
            return;

        double desiredY = sv.Offset.Y + delta;
        desiredY = ClampY(extentH, viewportH, desiredY);

        sv.Offset = new Vector(sv.Offset.X, desiredY);
    }

    private static int CountNewlinesUpTo(string text, int index)
    {
        if (string.IsNullOrEmpty(text) || index <= 0) return 0;
        if (index > text.Length) index = text.Length;

        int count = 0;
        for (int i = 0; i < index; i++)
        {
            if (text[i] == '\n')
                count++;
        }
        return count;
    }

    private static bool TryGetCaretYInScrollViewer(TextBox tb, ScrollViewer sv, int charIndex, out double caretY)
    {
        caretY = 0;

        int len = tb.Text?.Length ?? 0;
        if (len <= 0) return false;
        charIndex = Math.Clamp(charIndex, 0, len);

        try
        {
            var presenter = tb.GetVisualDescendants().FirstOrDefault(v => v.GetType().Name == "TextPresenter");
            if (presenter != null)
            {
                var mPr = presenter.GetType().GetMethod("GetRectFromCharacterIndex", new[] { typeof(int) });
                if (mPr != null)
                {
                    var val = mPr.Invoke(presenter, new object[] { charIndex });
                    if (val is Rect r2)
                    {
                        var p = presenter.TranslatePoint(new Point(r2.X, r2.Y), sv);
                        if (p != null)
                        {
                            caretY = p.Value.Y;
                            return true;
                        }
                    }
                }
            }
        }
        catch { /* ignore */ }

        var mTb = tb.GetType().GetMethod("GetRectFromCharacterIndex", new[] { typeof(int) });
        if (mTb != null)
        {
            try
            {
                var val = mTb.Invoke(tb, new object[] { charIndex });
                if (val is Rect r)
                {
                    var p = tb.TranslatePoint(new Point(r.X, r.Y), sv);
                    if (p != null)
                    {
                        caretY = p.Value.Y;
                        return true;
                    }
                }
            }
            catch { /* ignore */ }
        }

        return false;
    }

    private void RequestMirrorFromUserAction(bool sourceIsTranslated)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_syncingSelection) return;
            if (_renderOrig.IsEmpty || _renderTran.IsEmpty) return;
            MirrorSelectionOneWay(sourceIsTranslated);
        }, DispatcherPriority.Background);

        DispatcherTimer.RunOnce(() =>
        {
            if (_syncingSelection) return;
            if (_renderOrig.IsEmpty || _renderTran.IsEmpty) return;
            MirrorSelectionOneWay(sourceIsTranslated);
        }, TimeSpan.FromMilliseconds(25));
    }
}
