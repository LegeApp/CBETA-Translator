using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CbetaTranslator.App.Services;

namespace CbetaTranslator.App.Views;

public partial class GitTabView : UserControl
{
    private const string RepoUrl = "https://github.com/Fabulu/CbetaZenTexts.git";
    private const string RepoFolderName = "CbetaZenTexts";

    private Button? _btnPickDest;
    private Button? _btnGetFiles;
    private Button? _btnCancel;

    private TextBlock? _txtDest;
    private TextBlock? _txtProgress;
    private TextBox? _txtLog;

    // The "base folder" where we clone the repo folder into (base + RepoFolderName).
    private string? _baseDestFolder;

    // If the app already opened a repo root, we want to lock onto it by default.
    private string? _currentRepoRoot;

    private CancellationTokenSource? _cts;

    private readonly IGitRepoService _git = new GitRepoService();

    public event EventHandler<string>? Status;
    public event EventHandler<string>? RootCloned;

    public GitTabView()
    {
        InitializeComponent();
        FindControls();
        WireEvents();

        // Initial default (only used until MainWindow tells us a loaded root).
        _baseDestFolder = GetDefaultBaseFolder();
        UpdateDestLabel();
        SetProgress("Ready.");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void FindControls()
    {
        _btnPickDest = this.FindControl<Button>("BtnPickDest");
        _btnGetFiles = this.FindControl<Button>("BtnGetFiles");
        _btnCancel = this.FindControl<Button>("BtnCancel");

        _txtDest = this.FindControl<TextBlock>("TxtDest");
        _txtProgress = this.FindControl<TextBlock>("TxtProgress");
        _txtLog = this.FindControl<TextBox>("TxtLog");
    }

    private void WireEvents()
    {
        if (_btnPickDest != null) _btnPickDest.Click += async (_, _) => await PickDestAsync();
        if (_btnGetFiles != null) _btnGetFiles.Click += async (_, _) => await GetOrUpdateFilesAsync();
        if (_btnCancel != null) _btnCancel.Click += (_, _) => Cancel();

        // When user opens the tab, refresh the destination label to reflect latest root.
        AttachedToVisualTree += (_, _) => UpdateDestLabel();
    }

    /// <summary>
    /// MainWindow calls this whenever the app loads a root folder successfully.
    /// We use it to default Git operations to the currently opened repo.
    /// </summary>
    public void SetCurrentRepoRoot(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return;

        // rootPath is expected to be the CBETA root (contains xml-p5 + xml-p5t).
        // If it is inside a repo clone, it should also contain ".git".
        var root = rootPath.Trim();

        // If the caller hands us a path inside the repo, normalize to repo root.
        // But in your app, LoadRootAsync sets _root to the repo root already.
        if (Directory.Exists(root) && Directory.Exists(Path.Combine(root, ".git")))
        {
            _currentRepoRoot = root;
            _baseDestFolder = Path.GetDirectoryName(root); // parent folder containing RepoFolderName
            UpdateDestLabel();
            return;
        }

        // If root isn't a git repo (user opened a manual corpus folder), still make Git tab
        // default to that folder's parent so clone lands near it.
        if (Directory.Exists(root))
        {
            _currentRepoRoot = null;
            _baseDestFolder = root;
            UpdateDestLabel();
        }
    }

    private static string GetDefaultBaseFolder()
    {
        try
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(docs))
                docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            return Path.Combine(docs, "CbetaTranslator");
        }
        catch
        {
            return AppContext.BaseDirectory;
        }
    }

    private string GetTargetRepoDir()
    {
        // If we already have a repo open in the app, update THAT repo directly.
        if (!string.IsNullOrWhiteSpace(_currentRepoRoot) &&
            Directory.Exists(_currentRepoRoot) &&
            Directory.Exists(Path.Combine(_currentRepoRoot, ".git")))
        {
            return _currentRepoRoot!;
        }

        // Otherwise: base folder + fixed repo folder name
        var baseDir = _baseDestFolder ?? GetDefaultBaseFolder();
        return Path.Combine(baseDir, RepoFolderName);
    }

    private void UpdateDestLabel()
    {
        var target = GetTargetRepoDir();
        if (_txtDest != null)
            _txtDest.Text = "Location: " + target;
    }

    private async Task PickDestAsync()
    {
        try
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner?.StorageProvider == null)
            {
                SetProgress("Storage provider not available.");
                return;
            }

            var picked = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select a folder where the repo will be stored"
            });

            var folder = picked.Count > 0 ? picked[0] : null;
            if (folder == null) return;

            _baseDestFolder = folder.Path.LocalPath;
            _currentRepoRoot = null; // user explicitly chose a new place; stop locking to current repo
            UpdateDestLabel();

            Status?.Invoke(this, "Location updated.");
        }
        catch (Exception ex)
        {
            SetProgress("Pick folder failed: " + ex.Message);
            Status?.Invoke(this, "Pick folder failed: " + ex.Message);
        }
    }

    private async Task GetOrUpdateFilesAsync()
    {
        Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        SetButtonsBusy(true);
        ClearLog();

        var repoDir = GetTargetRepoDir();
        var baseDir = Path.GetDirectoryName(repoDir) ?? (_baseDestFolder ?? GetDefaultBaseFolder());

        try
        {
            AppendLog($"[repo] {RepoUrl}");
            AppendLog($"[path] {repoDir}");

            SetProgress("Checking git…");
            var gitOk = await _git.CheckGitAvailableAsync(ct);
            if (!gitOk)
            {
                SetProgress("Git not found. Install Git first.");
                AppendLog("[error] git not found in PATH");
                Status?.Invoke(this, "Git not found. Install Git first.");
                return;
            }

            // If repo exists and is a git working tree -> UPDATE
            if (Directory.Exists(repoDir) && Directory.Exists(Path.Combine(repoDir, ".git")))
            {
                SetProgress("Preparing update…");
                var cleanProg = new Progress<string>(line => Dispatcher.UIThread.Post(() => AppendLog(line)));
                var clean = await _git.EnsureCleanForUpdateAsync(repoDir, cleanProg, ct);

                if (!clean.CanProceed)
                {
                    SetProgress("Update blocked.");
                    if (!string.IsNullOrWhiteSpace(clean.BlockReason))
                        AppendLog("[reason] " + clean.BlockReason.Replace("\r", "").Replace("\n", " / "));
                    Status?.Invoke(this, "Update blocked.");
                    return;
                }

                SetProgress("Fetching…");
                var fetchProg = new Progress<string>(line => Dispatcher.UIThread.Post(() => AppendLog(line)));
                var fetch = await _git.FetchAsync(repoDir, fetchProg, ct);

                if (!fetch.Success)
                {
                    SetProgress("Fetch failed.");
                    AppendLog("[error] " + (fetch.Error ?? "unknown error"));
                    Status?.Invoke(this, "Fetch failed: " + (fetch.Error ?? "unknown error"));
                    return;
                }

                SetProgress("Pulling…");
                var pullProg = new Progress<string>(line => Dispatcher.UIThread.Post(() => AppendLog(line)));
                var pull = await _git.PullFfOnlyAsync(repoDir, pullProg, ct);

                if (!pull.Success)
                {
                    SetProgress("Pull failed.");
                    AppendLog("[error] " + (pull.Error ?? "unknown error"));
                    AppendLog("If this says 'not possible to fast-forward', delete the folder and re-clone.");
                    Status?.Invoke(this, "Pull failed: " + (pull.Error ?? "unknown error"));
                    return;
                }

                SetProgress("Up to date.");
                AppendLog("[ok] update complete: " + repoDir);

                // Lock onto this repo for next time
                _currentRepoRoot = repoDir;
                _baseDestFolder = Path.GetDirectoryName(repoDir);
                UpdateDestLabel();

                RootCloned?.Invoke(this, repoDir);
                Status?.Invoke(this, "Repo updated.");
                return;
            }

            // Otherwise -> CLONE into repoDir (baseDir/RepoFolderName)
            if (!Directory.Exists(baseDir))
                Directory.CreateDirectory(baseDir);

            if (Directory.Exists(repoDir) && Directory.EnumerateFileSystemEntries(repoDir).Any())
            {
                SetProgress("Folder exists but is not a Git repo: " + repoDir);
                AppendLog("[error] target folder exists and is not a git repo");
                AppendLog("Pick a different location or delete that folder.");
                Status?.Invoke(this, "Target folder exists but is not a Git repo.");
                return;
            }

            SetProgress("Cloning…");
            var prog = new Progress<string>(line => Dispatcher.UIThread.Post(() => AppendLog(line)));
            var clone = await _git.CloneAsync(RepoUrl, repoDir, prog, ct);

            if (!clone.Success)
            {
                SetProgress("Clone failed.");
                AppendLog("[error] " + (clone.Error ?? "unknown error"));
                Status?.Invoke(this, "Clone failed: " + (clone.Error ?? "unknown error"));
                return;
            }

            SetProgress("Done. Repo is ready.");
            AppendLog("[ok] clone complete: " + repoDir);

            // Lock onto this repo immediately
            _currentRepoRoot = repoDir;
            _baseDestFolder = Path.GetDirectoryName(repoDir);
            UpdateDestLabel();

            RootCloned?.Invoke(this, repoDir);
            Status?.Invoke(this, "Repo cloned.");
        }
        catch (OperationCanceledException)
        {
            SetProgress("Canceled.");
            AppendLog("[cancel] canceled");
            Status?.Invoke(this, "Canceled.");
        }
        catch (Exception ex)
        {
            SetProgress("Failed: " + ex.Message);
            AppendLog("[error] " + ex);
            Status?.Invoke(this, "Failed: " + ex.Message);
        }
        finally
        {
            SetButtonsBusy(false);
        }
    }

    private void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _git.TryCancelRunningProcess();
        SetButtonsBusy(false);
    }

    private void SetButtonsBusy(bool busy)
    {
        if (_btnCancel != null) _btnCancel.IsEnabled = busy;
        if (_btnGetFiles != null) _btnGetFiles.IsEnabled = !busy;
        if (_btnPickDest != null) _btnPickDest.IsEnabled = !busy;
    }

    private void SetProgress(string msg)
    {
        if (_txtProgress != null)
            _txtProgress.Text = msg;
    }

    private void ClearLog()
    {
        if (_txtLog != null)
            _txtLog.Text = "";
    }

    private void AppendLog(string line)
    {
        if (_txtLog == null) return;

        if (_txtLog.Text?.Length > 200_000)
            _txtLog.Text = _txtLog.Text.Substring(_txtLog.Text.Length - 120_000);

        _txtLog.Text += line + Environment.NewLine;

        try { _txtLog.CaretIndex = _txtLog.Text.Length; } catch { }
    }
}
