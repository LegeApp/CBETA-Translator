using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CbetaTranslator.App.Infrastructure;
using CbetaTranslator.App.Models;
using CbetaTranslator.App.Services;
using CbetaTranslator.App.Text;

namespace CbetaTranslator.App.Views;

public partial class MainWindow : Window
{
    // Named controls
    private Button? _btnToggleNav;
    private Button? _btnOpenRoot;
    private Button? _btnSave;

    private Border? _navPanel;
    private ListBox? _filesList;

    private TextBlock? _txtRoot;
    private TextBlock? _txtCurrentFile;
    private TextBlock? _txtStatus;

    private TabControl? _tabs;

    // Child views
    private ReadableTabView? _readableView;
    private TranslationTabView? _translationView;

    // Services
    private readonly IFileService _fileService = new FileService();
    private readonly AppConfigService _configService = new AppConfigService();
    private readonly IndexCacheService _indexCacheService = new IndexCacheService();

    // State
    private string? _root;
    private string? _originalDir;
    private string? _translatedDir;

    private List<string> _relativeFiles = new();
    private string? _currentRelPath;

    // Raw XML
    private string _rawOrigXml = "";
    private string _rawTranXml = "";

    // Cancel background work when user switches files quickly
    private CancellationTokenSource? _renderCts;

    public MainWindow()
    {
        InitializeComponent();
        FindControls();
        WireEvents();

        SetStatus("Ready.");

        UpdateSaveButtonState();

        // ✅ Phase 0: auto-load last root (no UI blocking)
        _ = TryAutoLoadRootFromConfigAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void FindControls()
    {
        _btnToggleNav = this.FindControl<Button>("BtnToggleNav");
        _btnOpenRoot = this.FindControl<Button>("BtnOpenRoot");
        _btnSave = this.FindControl<Button>("BtnSave");

        _navPanel = this.FindControl<Border>("NavPanel");
        _filesList = this.FindControl<ListBox>("FilesList");

        _txtRoot = this.FindControl<TextBlock>("TxtRoot");
        _txtCurrentFile = this.FindControl<TextBlock>("TxtCurrentFile");
        _txtStatus = this.FindControl<TextBlock>("TxtStatus");

        _tabs = this.FindControl<TabControl>("MainTabs");

        _readableView = this.FindControl<ReadableTabView>("ReadableView");
        _translationView = this.FindControl<TranslationTabView>("TranslationView");

        if (_translationView != null)
        {
            _translationView.SaveRequested += async (_, _) => await SaveTranslatedFromTabAsync();
            _translationView.Status += (_, msg) => SetStatus(msg);
        }
    }

    private void WireEvents()
    {
        if (_btnToggleNav != null) _btnToggleNav.Click += ToggleNav_Click;
        if (_btnOpenRoot != null) _btnOpenRoot.Click += OpenRoot_Click;
        if (_btnSave != null) _btnSave.Click += Save_Click;

        if (_filesList != null) _filesList.SelectionChanged += FilesList_SelectionChanged;

        if (_tabs != null) _tabs.SelectionChanged += (_, _) => UpdateSaveButtonState();
    }

    private void ToggleNav_Click(object? sender, RoutedEventArgs e)
    {
        if (_navPanel == null) return;
        _navPanel.IsVisible = !_navPanel.IsVisible;
    }

    private async void OpenRoot_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (StorageProvider is null)
            {
                SetStatus("StorageProvider not available.");
                return;
            }

            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select CBETA root folder (contains xml-p5; xml-p5t will be created if missing)"
            });

            var folder = picked.FirstOrDefault();
            if (folder is null) return;

            await LoadRootAsync(folder.Path.LocalPath, saveToConfig: true);
        }
        catch (Exception ex)
        {
            SetStatus("Open root failed: " + ex.Message);
        }
    }

    private async Task TryAutoLoadRootFromConfigAsync()
    {
        var cfg = await _configService.TryLoadAsync();
        if (cfg?.TextRootPath is null) return;

        try
        {
            if (!System.IO.Directory.Exists(cfg.TextRootPath))
                return;

            SetStatus("Auto-loading last root…");
            await LoadRootAsync(cfg.TextRootPath, saveToConfig: false);

            // optionally restore last selected file later (not part of phase 0)
        }
        catch
        {
            // ignore on startup
        }
    }

    private async Task LoadRootAsync(string rootPath, bool saveToConfig)
    {
        _root = rootPath;
        _originalDir = AppPaths.GetOriginalDir(_root);
        _translatedDir = AppPaths.GetTranslatedDir(_root);

        if (_txtRoot != null) _txtRoot.Text = _root;

        if (!System.IO.Directory.Exists(_originalDir))
        {
            SetStatus($"Original folder missing: {_originalDir}");
            return;
        }

        AppPaths.EnsureTranslatedDirExists(_root);

        if (saveToConfig)
        {
            await _configService.SaveAsync(new AppConfig
            {
                TextRootPath = _root
            });
        }

        await LoadFileListFromCacheOrBuildAsync();
    }

    // ✅ Phase 0: cache file list in <root>/index.cache.json
    private async Task LoadFileListFromCacheOrBuildAsync()
    {
        if (_root == null || _originalDir == null || _filesList == null)
            return;

        ClearViews();

        // Try cache
        var cache = await _indexCacheService.TryLoadAsync(_root);
        if (cache != null && cache.RelativePaths.Count > 0)
        {
            _relativeFiles = cache.RelativePaths;
            _filesList.ItemsSource = _relativeFiles;
            SetStatus($"Loaded index cache: {_relativeFiles.Count:n0} files.");
            return;
        }

        // Build cache with progress (async; UI responsive)
        SetStatus("Building index cache… (first run will take a moment)");

        var progress = new Progress<(int done, int total)>(p =>
        {
            SetStatus($"Indexing files… {p.done:n0}/{p.total:n0}");
        });

        IndexCache built;
        try
        {
            built = await _indexCacheService.BuildAsync(_originalDir, _root, progress);
        }
        catch (Exception ex)
        {
            SetStatus("Index build failed: " + ex.Message);
            // fallback to old behavior
            _relativeFiles = await _fileService.EnumerateXmlRelativePathsAsync(_originalDir);
            _filesList.ItemsSource = _relativeFiles;
            SetStatus($"Fallback scan done: {_relativeFiles.Count:n0} files.");
            return;
        }

        await _indexCacheService.SaveAsync(_root, built);

        _relativeFiles = built.RelativePaths;
        _filesList.ItemsSource = _relativeFiles;

        SetStatus($"Index cache created: {_relativeFiles.Count:n0} files.");
    }

    private void ClearViews()
    {
        _renderCts?.Cancel();
        _renderCts = null;

        _rawOrigXml = "";
        _rawTranXml = "";
        _currentRelPath = null;

        if (_txtCurrentFile != null) _txtCurrentFile.Text = "";

        _readableView?.Clear();
        _translationView?.Clear();

        UpdateSaveButtonState();
    }

    private async void FilesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_filesList?.SelectedItem is not string rel)
            return;

        await LoadPairAsync(rel);
    }

    private async Task LoadPairAsync(string relPath)
    {
        if (_originalDir == null || _translatedDir == null)
            return;

        // cancel previous render task if any
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;

        _currentRelPath = relPath;

        if (_txtCurrentFile != null)
            _txtCurrentFile.Text = relPath;

        SetStatus("Loading: " + relPath);

        var (orig, tran) = await _fileService.ReadPairAsync(_originalDir, _translatedDir, relPath);

        _rawOrigXml = orig ?? "";
        _rawTranXml = tran ?? "";

        // ✅ Immediately populate XML tab (fast)
        _translationView?.SetXml(_rawOrigXml, _rawTranXml);
        UpdateSaveButtonState();

        // ✅ Render readable view in background (no UI freeze)
        SetStatus("Rendering readable view…");

        try
        {
            var renderTask = Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var ro = CbetaTeiRenderer.Render(_rawOrigXml);
                ct.ThrowIfCancellationRequested();
                var rt = CbetaTeiRenderer.Render(_rawTranXml);
                return (ro, rt);
            }, ct);

            var (renderOrig, renderTran) = await renderTask;

            if (ct.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _readableView?.SetRendered(renderOrig, renderTran);
                SetStatus($"Loaded. Readable segments: O={renderOrig.Segments.Count:n0}, T={renderTran.Segments.Count:n0}.");
            });
        }
        catch (OperationCanceledException)
        {
            // user clicked another file; ignore
        }
        catch (Exception ex)
        {
            SetStatus("Render failed: " + ex.Message);
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        _ = SaveTranslatedFromTabAsync();
    }

    private async Task SaveTranslatedFromTabAsync()
    {
        try
        {
            if (_translatedDir == null || _currentRelPath == null)
            {
                SetStatus("Nothing to save (no file selected).");
                return;
            }

            if (_translationView == null)
            {
                SetStatus("Translation view not available.");
                return;
            }

            var xml = _translationView.GetTranslatedXml();

            await _fileService.WriteTranslatedAsync(_translatedDir, _currentRelPath, xml);
            SetStatus("Saved translated XML: " + _currentRelPath);

            // Re-render translated readable in background (don’t freeze)
            _rawTranXml = xml ?? "";

            _renderCts?.Cancel();
            _renderCts = new CancellationTokenSource();
            var ct = _renderCts.Token;

            SetStatus("Re-rendering readable view…");

            var renderTask = Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var ro = CbetaTeiRenderer.Render(_rawOrigXml);
                ct.ThrowIfCancellationRequested();
                var rt = CbetaTeiRenderer.Render(_rawTranXml);
                return (ro, rt);
            }, ct);

            var (renderOrig, renderTran) = await renderTask;

            if (!ct.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _readableView?.SetRendered(renderOrig, renderTran);
                    SetStatus("Saved + readable view updated.");
                });
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            SetStatus("Save failed: " + ex.Message);
        }
    }

    private void UpdateSaveButtonState()
    {
        if (_btnSave == null) return;

        bool hasFile = _currentRelPath != null;
        bool translationTabSelected = _tabs?.SelectedIndex == 1;

        _btnSave.IsEnabled = hasFile && translationTabSelected;
    }

    private void SetStatus(string msg)
    {
        if (_txtStatus != null)
            _txtStatus.Text = msg;
    }
}
