using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CbetaTranslator.App.Models;
using CbetaTranslator.App.Services;

namespace CbetaTranslator.App.Views;

public partial class SearchTabView : UserControl
{
    private TextBox? _txtQuery;
    private Button? _btnSearch;
    private Button? _btnCancel;
    private Button? _btnBuildIndex;
    private TextBlock? _txtProgress;

    private CheckBox? _chkOriginal;
    private CheckBox? _chkTranslated;
    private ComboBox? _cmbStatus;
    private ComboBox? _cmbContext;

    private TextBlock? _txtSummary;
    private Button? _btnExportTsv;

    private TreeView? _resultsTree;

    private readonly SearchIndexService _svc = new();

    private string? _root;
    private string? _originalDir;
    private string? _translatedDir;

    private List<FileNavItem> _fileIndex = new();

    private Func<string, (string display, string tooltip, TranslationStatus? status)>? _meta;

    private CancellationTokenSource? _cts;

    private readonly List<SearchResultGroup> _groups = new();

    public event EventHandler<string>? Status;
    public event EventHandler<string>? OpenFileRequested;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);


    public void SetRootContext(string root, string originalDir, string translatedDir)
    {
        _root = root;
        _originalDir = originalDir;
        _translatedDir = translatedDir;
    }

    public void SetFileIndex(List<FileNavItem> items)
    {
        _fileIndex = items ?? new List<FileNavItem>();
    }

    public SearchTabView()
    {
        InitializeComponent();
        FindControls();
        WireEvents();
        InitCombos();

        SetProgress("Index not loaded.");
        SetSummary("Ready.");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void FindControls()
    {
        _txtQuery = this.FindControl<TextBox>("TxtQuery");
        _btnSearch = this.FindControl<Button>("BtnSearch");
        _btnCancel = this.FindControl<Button>("BtnCancel");
        _btnBuildIndex = this.FindControl<Button>("BtnBuildIndex");
        _txtProgress = this.FindControl<TextBlock>("TxtProgress");

        _chkOriginal = this.FindControl<CheckBox>("ChkOriginal");
        _chkTranslated = this.FindControl<CheckBox>("ChkTranslated");
        _cmbStatus = this.FindControl<ComboBox>("CmbStatus");
        _cmbContext = this.FindControl<ComboBox>("CmbContext");

        _txtSummary = this.FindControl<TextBlock>("TxtSummary");
        _btnExportTsv = this.FindControl<Button>("BtnExportTsv");

        _resultsTree = this.FindControl<TreeView>("ResultsTree");
    }

    private void WireEvents()
    {
        if (_btnSearch != null) _btnSearch.Click += async (_, _) => await StartSearchAsync();
        if (_btnCancel != null) _btnCancel.Click += (_, _) => Cancel();
        if (_btnBuildIndex != null) _btnBuildIndex.Click += async (_, _) => await BuildIndexAsync();
        if (_btnExportTsv != null) _btnExportTsv.Click += async (_, _) => await ExportTsvAsync();

        if (_txtQuery != null)
        {
            _txtQuery.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    await StartSearchAsync();
                    e.Handled = true;
                }
            };
        }

        if (_resultsTree != null)
        {
            _resultsTree.DoubleTapped += (_, _) =>
            {
                var sel = _resultsTree.SelectedItem;
                if (sel is SearchResultGroup g)
                {
                    if (!string.IsNullOrWhiteSpace(g.RelPath))
                        OpenFileRequested?.Invoke(this, g.RelPath);
                }
                else if (sel is SearchResultChild c)
                {
                    if (!string.IsNullOrWhiteSpace(c.RelPath))
                        OpenFileRequested?.Invoke(this, c.RelPath);
                }
            };
        }
    }

    private void InitCombos()
    {
        if (_cmbStatus != null)
        {
            _cmbStatus.ItemsSource = new[]
            {
                "All",
                "Red (untranslated)",
                "Yellow (WIP)",
                "Green (done)"
            };
            _cmbStatus.SelectedIndex = 0;
        }

        if (_cmbContext != null)
        {
            _cmbContext.ItemsSource = new[]
            {
                "20 chars",
                "40 chars",
                "80 chars"
            };
            _cmbContext.SelectedIndex = 1; // 40
        }
    }

    public void Clear()
    {
        Cancel();

        _root = null;
        _originalDir = null;
        _translatedDir = null;
        _fileIndex.Clear();
        _meta = null;

        _groups.Clear();
        if (_resultsTree != null) _resultsTree.ItemsSource = null;

        SetProgress("No root loaded.");
        SetSummary("Ready.");
        if (_btnExportTsv != null) _btnExportTsv.IsEnabled = false;
    }

    public void SetContext(
        string root,
        string originalDir,
        string translatedDir,
        Func<string, (string display, string tooltip, TranslationStatus? status)> fileMeta)
    {
        _root = root;
        _originalDir = originalDir;
        _translatedDir = translatedDir;
        _meta = fileMeta;

        SetProgress("Ready. (Index will load automatically on first search if present.)");
        SetSummary("Ready.");
    }

    private int GetContextWidth()
    {
        var s = _cmbContext?.SelectedItem as string ?? "40 chars";
        if (s.StartsWith("20")) return 20;
        if (s.StartsWith("80")) return 80;
        return 40;
    }

    private TranslationStatus? GetStatusFilter()
    {
        int i = _cmbStatus?.SelectedIndex ?? 0;
        return i switch
        {
            1 => TranslationStatus.Red,
            2 => TranslationStatus.Yellow,
            3 => TranslationStatus.Green,
            _ => null
        };
    }

    private void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;

        if (_btnCancel != null) _btnCancel.IsEnabled = false;
    }

    private async Task BuildIndexAsync()
    {
        if (_root == null || _originalDir == null || _translatedDir == null)
        {
            Status?.Invoke(this, "Search tab has no root context yet.");
            return;
        }

        Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            if (_btnCancel != null) _btnCancel.IsEnabled = true;
            SetProgress("Building index...");
            SetSummary("Indexing... (this is one-time; later searches are fast)");

            var prog = new Progress<(int done, int total, string phase)>(p =>
            {
                SetProgress($"{p.phase} {p.done:n0}/{p.total:n0}");
            });

            await _svc.BuildAsync(_root, _originalDir, _translatedDir, prog, ct);

            SetProgress("Index built.");
            SetSummary("Index built. Ready to search.");
            Status?.Invoke(this, "Search index built.");
        }
        catch (OperationCanceledException)
        {
            SetProgress("Canceled.");
            SetSummary("Canceled.");
        }
        catch (Exception ex)
        {
            SetProgress("Index build failed: " + ex.Message);
            SetSummary("Index build failed.");
            Status?.Invoke(this, "Index build failed: " + ex.Message);
        }
        finally
        {
            if (_btnCancel != null) _btnCancel.IsEnabled = false;
        }
    }

    private async Task StartSearchAsync()
    {
        if (_root == null || _originalDir == null || _translatedDir == null || _meta == null)
        {
            Status?.Invoke(this, "Search tab has no root context yet.");
            return;
        }

        string q = (_txtQuery?.Text ?? "").Trim();
        if (q.Length == 0)
        {
            Status?.Invoke(this, "Enter a search query.");
            return;
        }

        bool includeO = _chkOriginal?.IsChecked == true;
        bool includeT = _chkTranslated?.IsChecked == true;
        if (!includeO && !includeT)
        {
            Status?.Invoke(this, "Select Original and/or Translated.");
            return;
        }

        Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _groups.Clear();
        if (_resultsTree != null) _resultsTree.ItemsSource = null;
        if (_btnExportTsv != null) _btnExportTsv.IsEnabled = false;

        try
        {
            if (_btnCancel != null) _btnCancel.IsEnabled = true;
            SetSummary($"Searching for: {q}");
            SetProgress("Loading index...");

            // Load existing index or instruct user to build it
            var manifest = await _svc.TryLoadAsync(_root);
            if (manifest == null)
            {
                SetProgress("No index found. Build it first.");
                Status?.Invoke(this, "No search index found. Click 'Build/Update Index' first.");
                return;
            }

            var statusFilter = GetStatusFilter();
            int contextWidth = GetContextWidth();

            int totalHits = 0;
            int totalGroups = 0;

            var prog = new Progress<SearchIndexService.SearchProgress>(p =>
            {
                SetProgress($"{p.Phase}  verified {p.VerifiedDocs:n0}/{p.TotalDocsToVerify:n0}  groups={p.Groups:n0}  hits={p.TotalHits:n0}");
            });

            await foreach (var g in _svc.SearchAllAsync(
                               _root,
                               _originalDir,
                               _translatedDir,
                               manifest,
                               q,
                               includeO,
                               includeT,
                               fileMeta: rel =>
                               {
                                   // Apply status filtering at the group level (based on canonical file status)
                                   // If statusFilter is set and this rel doesn't match, we return blank meta but
                                   // the caller will filter groups after emission. We'll filter earlier here:
                                   var m = _meta(rel);
                                   if (statusFilter.HasValue && m.status.HasValue && m.status.Value != statusFilter.Value)
                                       return ("", "", m.status); // we'll drop later by checking display empty
                                   if (statusFilter.HasValue && !m.status.HasValue)
                                       return ("", "", null);
                                   return m;
                               },
                               contextWidth: contextWidth,
                               progress: prog,
                               ct: ct))
            {
                ct.ThrowIfCancellationRequested();

                // Drop groups that were filtered by status (we mark them with empty display)
                if (string.IsNullOrWhiteSpace(g.DisplayName))
                    continue;

                _groups.Add(g);
                totalGroups++;
                totalHits += g.Children.Count;

                // Live UI update (don’t spam too hard)
                if (totalGroups <= 10 || totalGroups % 10 == 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (_resultsTree != null)
                            _resultsTree.ItemsSource = _groups.ToList();

                        SetSummary($"Results: files={totalGroups:n0}, hits={totalHits:n0}");
                    });
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_resultsTree != null)
                    _resultsTree.ItemsSource = _groups.ToList();

                SetSummary($"Done. files={_groups.Count:n0}, hits={_groups.Sum(x => x.Children.Count):n0}");
                if (_btnExportTsv != null) _btnExportTsv.IsEnabled = _groups.Count > 0;
            });
        }
        catch (OperationCanceledException)
        {
            SetProgress("Canceled.");
            SetSummary("Canceled.");
        }
        catch (Exception ex)
        {
            SetProgress("Search failed: " + ex.Message);
            SetSummary("Search failed.");
            Status?.Invoke(this, "Search failed: " + ex.Message);
        }
        finally
        {
            if (_btnCancel != null) _btnCancel.IsEnabled = false;
        }
    }

    private async Task ExportTsvAsync()
    {
        try
        {
            if (_groups.Count == 0)
            {
                Status?.Invoke(this, "No results to export.");
                return;
            }

            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner?.StorageProvider == null)
            {
                Status?.Invoke(this, "Storage provider not available.");
                return;
            }

            var file = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Export search results (TSV)",
                SuggestedFileName = "search-results.tsv"
            });

            if (file == null) return;

            var sb = new StringBuilder(1024 * 16);
            sb.AppendLine("relPath\tside\tmatchIndex\tleft\tmatch\tright");

            foreach (var g in _groups)
            {
                foreach (var c in g.Children)
                {
                    string side = c.Side == SearchSide.Original ? "O" : "T";
                    sb.Append(g.RelPath).Append('\t')
                      .Append(side).Append('\t')
                      .Append(c.Hit.Index).Append('\t')
                      .Append(EscapeTsv(c.Hit.Left)).Append('\t')
                      .Append(EscapeTsv(c.Hit.Match)).Append('\t')
                      .Append(EscapeTsv(c.Hit.Right)).AppendLine();
                }
            }

            await using var s = await file.OpenWriteAsync();
            var bytes = Utf8NoBom.GetBytes(sb.ToString());
            await s.WriteAsync(bytes, 0, bytes.Length);

            Status?.Invoke(this, "Exported TSV.");
        }
        catch (Exception ex)
        {
            Status?.Invoke(this, "Export failed: " + ex.Message);
        }
    }

    private static string EscapeTsv(string s)
    {
        s = s ?? "";
        s = s.Replace("\t", " ").Replace("\r", "").Replace("\n", " ");
        return s;
    }

    private void SetProgress(string msg)
    {
        if (_txtProgress != null) _txtProgress.Text = msg;
    }

    private void SetSummary(string msg)
    {
        if (_txtSummary != null) _txtSummary.Text = msg;
    }
}
