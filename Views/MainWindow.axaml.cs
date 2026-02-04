using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using CbetaTranslator.App.Infrastructure;
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

    // State
    private string? _root;
    private string? _originalDir;
    private string? _translatedDir;
    private List<string> _relativeFiles = new();
    private string? _currentRelPath;

    // Raw XML
    private string _rawOrigXml = "";
    private string _rawTranXml = "";

    public MainWindow()
    {
        InitializeComponent();
        FindControls();
        WireEvents();

        SetStatus("Ready. Click Open Root.");
        UpdateSaveButtonState();
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

        // Tell translation view how to save (it doesn't know paths itself)
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

            _root = folder.Path.LocalPath;
            _originalDir = AppPaths.GetOriginalDir(_root);
            _translatedDir = AppPaths.GetTranslatedDir(_root);

            if (_txtRoot != null) _txtRoot.Text = _root;

            if (!System.IO.Directory.Exists(_originalDir))
            {
                SetStatus($"Original folder missing: {_originalDir}");
                return;
            }

            AppPaths.EnsureTranslatedDirExists(_root);
            await LoadFileListAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Open root failed: " + ex.Message);
        }
    }

    private async Task LoadFileListAsync()
    {
        if (_originalDir == null || _filesList == null)
            return;

        SetStatus("Scanning xml-p5…");

        _relativeFiles = await _fileService.EnumerateXmlRelativePathsAsync(_originalDir);
        _filesList.ItemsSource = _relativeFiles;

        _filesList.SelectedItem = null;
        _currentRelPath = null;

        ClearViews();
        SetStatus($"Found {_relativeFiles.Count:n0} XML files. Click one to load.");
    }

    private void ClearViews()
    {
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

        _currentRelPath = relPath;

        if (_txtCurrentFile != null)
            _txtCurrentFile.Text = relPath;

        SetStatus("Loading: " + relPath);

        var (orig, tran) = await _fileService.ReadPairAsync(_originalDir, _translatedDir, relPath);

        _rawOrigXml = orig ?? "";
        _rawTranXml = tran ?? "";

        // Readable render
        SetStatus("Rendering readable view…");
        var renderOrig = CbetaTeiRenderer.Render(_rawOrigXml);
        var renderTran = CbetaTeiRenderer.Render(_rawTranXml);

        _readableView?.SetRendered(renderOrig, renderTran);

        // Raw XML tab
        _translationView?.SetXml(_rawOrigXml, _rawTranXml);

        UpdateSaveButtonState();

        SetStatus($"Loaded. Readable segments: O={renderOrig.Segments.Count:n0}, T={renderTran.Segments.Count:n0}.");
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        // Save button is meant for translation tab
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

            // Optional: refresh readable tab too so it reflects latest translation
            _rawTranXml = xml ?? "";
            var renderTran = CbetaTeiRenderer.Render(_rawTranXml);
            var renderOrig = CbetaTeiRenderer.Render(_rawOrigXml);
            _readableView?.SetRendered(renderOrig, renderTran);
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
