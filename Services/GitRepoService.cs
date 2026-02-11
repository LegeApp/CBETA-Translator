using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CbetaTranslator.App.Services;

public sealed class GitRepoService : IGitRepoService
{
    private Process? _running;

    // App-generated artifacts we are allowed to throw away to make update possible.
    // These MUST remain conservative.
    private static readonly HashSet<string> SafeTrackedReset = new(StringComparer.OrdinalIgnoreCase)
    {
        "index.cache.json",
        "search.index.manifest.json",
    };

    private static readonly HashSet<string> SafeUntrackedDelete = new(StringComparer.OrdinalIgnoreCase)
    {
        "index.debug.log",
    };

    // This one is *not* safe to delete automatically; it's too big / too risky.
    private static readonly HashSet<string> DangerousUntrackedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "CbetaZenTexts"
    };

    public async Task<bool> CheckGitAvailableAsync(CancellationToken ct)
    {
        var r = await RunAsync("git", "--version", workingDir: null,
            progress: new Progress<string>(_ => { }), ct: ct);

        return r.Success;
    }

    public async Task<GitCommandResult> CloneAsync(
        string repoUrl,
        string targetDir,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(targetDir);
        if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
            Directory.CreateDirectory(parent);

        // git clone --progress writes to stderr; capture both.
        string args = $"clone --progress \"{repoUrl}\" \"{targetDir}\"";
        return await RunAsync("git", args, workingDir: parent, progress: progress, ct: ct);
    }

    public async Task<GitCleanCheckResult> EnsureCleanForUpdateAsync(
        string repoDir,
        IProgress<string> progress,
        CancellationToken ct)
    {
        // Get porcelain status
        var lines = await GetStatusPorcelainAsync(repoDir, ct);
        if (lines.Count == 0)
            return new GitCleanCheckResult(true, null, Array.Empty<string>());

        // Categorize
        var unsafeLines = new List<string>();
        var evidence = new List<string>();
        var trackedToReset = new List<string>();
        var untrackedToDelete = new List<string>();
        var dangerousDirs = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            evidence.Add(line);

            // Formats:
            // " M file"
            // "M  file"
            // "?? file"
            // We just look at prefix and path.
            if (line.StartsWith("?? "))
            {
                var path = line.Substring(3).Trim();
                var norm = NormalizeRel(path);

                // directory?
                if (norm.EndsWith("/"))
                {
                    var dir = norm.TrimEnd('/');
                    if (DangerousUntrackedDirs.Contains(dir))
                    {
                        dangerousDirs.Add(dir);
                        continue;
                    }

                    // unknown directory => unsafe
                    unsafeLines.Add(line);
                    continue;
                }

                if (SafeUntrackedDelete.Contains(norm))
                {
                    untrackedToDelete.Add(norm);
                    continue;
                }

                // unknown untracked file => unsafe
                unsafeLines.Add(line);
                continue;
            }
            else
            {
                // tracked change (index + path starts at position 3)
                // porcelain can be " M path" or "M  path"
                if (line.Length < 4)
                {
                    unsafeLines.Add(line);
                    continue;
                }

                var path = line.Substring(3).Trim();
                var norm = NormalizeRel(path);

                if (SafeTrackedReset.Contains(norm))
                {
                    trackedToReset.Add(norm);
                    continue;
                }

                unsafeLines.Add(line);
            }
        }

        // Block immediately on dangerous dirs (repo-in-repo / wrong output path)
        if (dangerousDirs.Count > 0)
        {
            progress.Report("[block] found a suspicious folder inside the repo:");
            foreach (var d in dangerousDirs)
                progress.Report("  ?? " + d + "/");

            var reason =
                "There is an untracked folder named 'CbetaZenTexts' inside the repo.\n" +
                "This usually means you accidentally created a repo-in-repo (or picked a wrong folder).\n\n" +
                "Fix:\n" +
                "1) Open the repo folder shown in the Git tab.\n" +
                "2) Delete the inner 'CbetaZenTexts' folder.\n" +
                "3) Click 'Get / Update files' again.\n\n" +
                "We refuse to auto-delete that folder because it could contain important data.";

            return new GitCleanCheckResult(false, reason, evidence);
        }

        // If only safe artifacts are dirty, auto-clean them now.
        if (unsafeLines.Count == 0)
        {
            if (trackedToReset.Count > 0)
            {
                progress.Report("[clean] discarding app cache changes (safe)...");
                foreach (var p in trackedToReset)
                    progress.Report("  reset: " + p);

                // git checkout -- <files>
                var r = await RunAsync("git",
                    $"-C \"{repoDir}\" checkout -- {JoinArgs(trackedToReset)}",
                    workingDir: repoDir,
                    progress: progress,
                    ct: ct);

                if (!r.Success)
                {
                    return new GitCleanCheckResult(false,
                        "Failed to reset app cache files. " + (r.Error ?? "unknown error"),
                        evidence);
                }
            }

            if (untrackedToDelete.Count > 0)
            {
                progress.Report("[clean] deleting app debug files (safe)...");
                foreach (var p in untrackedToDelete)
                    progress.Report("  delete: " + p);

                foreach (var rel in untrackedToDelete)
                {
                    try
                    {
                        var abs = Path.Combine(repoDir, rel.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(abs))
                            File.Delete(abs);
                    }
                    catch
                    {
                        // ignore; git clean can catch leftovers
                    }
                }

                // Also try git clean for those files (best-effort)
                await RunAsync("git",
                    $"-C \"{repoDir}\" clean -f -- {JoinArgs(untrackedToDelete)}",
                    workingDir: repoDir,
                    progress: progress,
                    ct: ct);
            }

            // Re-check status
            var after = await GetStatusPorcelainAsync(repoDir, ct);
            if (after.Count == 0)
            {
                progress.Report("[ok] repo is clean after safe cleanup.");
                return new GitCleanCheckResult(true, null, evidence);
            }

            // Something else still dirty -> block with those lines
            var afterEv = new List<string>(after);
            return new GitCleanCheckResult(false,
                "Repo still has local changes after safe cleanup. Please resolve them manually.",
                afterEv);
        }

        // Unsafe stuff exists -> block and show sample
        progress.Report("[block] working tree has changes we won't touch:");
        for (int i = 0; i < Math.Min(12, unsafeLines.Count); i++)
            progress.Report("  " + unsafeLines[i]);
        if (unsafeLines.Count > 12)
            progress.Report($"  ... ({unsafeLines.Count - 12} more)");

        return new GitCleanCheckResult(false,
            "Update blocked: you have local changes that are not app cache files.",
            evidence);
    }

    public async Task<GitCommandResult> FetchAsync(
        string repoDir,
        IProgress<string> progress,
        CancellationToken ct)
    {
        string args = $"-C \"{repoDir}\" fetch --prune --progress";
        return await RunAsync("git", args, workingDir: repoDir, progress: progress, ct: ct);
    }

    public async Task<GitCommandResult> PullFfOnlyAsync(
        string repoDir,
        IProgress<string> progress,
        CancellationToken ct)
    {
        string args = $"-C \"{repoDir}\" pull --ff-only --progress";
        return await RunAsync("git", args, workingDir: repoDir, progress: progress, ct: ct);
    }

    public void TryCancelRunningProcess()
    {
        try
        {
            if (_running == null) return;

            try { _running.CloseMainWindow(); } catch { }

            try
            {
                if (!_running.HasExited)
                    _running.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }
        }
        finally
        {
            _running = null;
        }
    }

    // -------------------------
    // Helpers
    // -------------------------

    private static string NormalizeRel(string p)
        => (p ?? "").Replace('\\', '/').TrimStart('/');

    private static string JoinArgs(List<string> relPaths)
    {
        // quote each
        var sb = new StringBuilder();
        for (int i = 0; i < relPaths.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append('"').Append(relPaths[i].Replace("\"", "\\\"")).Append('"');
        }
        return sb.ToString();
    }

    private async Task<List<string>> GetStatusPorcelainAsync(string repoDir, CancellationToken ct)
    {
        var lines = new List<string>();

        var r = await RunCaptureAsync("git",
            $"-C \"{repoDir}\" status --porcelain",
            workingDir: repoDir,
            ct: ct,
            onLine: line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            });

        if (!r.Success)
        {
            // if we cannot read status, treat as dirty
            lines.Add("!! status-check-failed");
        }

        return lines;
    }

    private async Task<GitCommandResult> RunAsync(
        string fileName,
        string args,
        string? workingDir,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? Environment.CurrentDirectory : workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
        }

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _running = p;

        string? lastErr = null;
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        p.Exited += (_, _) =>
        {
            try { tcs.TrySetResult(p.ExitCode); }
            catch { tcs.TrySetResult(-1); }
        };

        try
        {
            if (!p.Start())
                return new GitCommandResult(false, -1, "Failed to start process.");

            ct.Register(() => TryCancelRunningProcess());

            var stdoutTask = Task.Run(async () =>
            {
                while (!p.StandardOutput.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await p.StandardOutput.ReadLineAsync();
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    progress.Report(line);
                }
            }, ct);

            var stderrTask = Task.Run(async () =>
            {
                while (!p.StandardError.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await p.StandardError.ReadLineAsync();
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    lastErr = line;
                    progress.Report(line);
                }
            }, ct);

            int exit = await tcs.Task;

            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }

            bool ok = exit == 0;
            return new GitCommandResult(ok, exit, ok ? null : (lastErr ?? $"git exited with code {exit}"));
        }
        catch (OperationCanceledException)
        {
            return new GitCommandResult(false, -2, "Canceled.");
        }
        catch (Exception ex)
        {
            return new GitCommandResult(false, -1, ex.Message);
        }
        finally
        {
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }

            _running = null;
        }
    }

    private async Task<GitCommandResult> RunCaptureAsync(
        string fileName,
        string args,
        string? workingDir,
        CancellationToken ct,
        Action<string> onLine)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? Environment.CurrentDirectory : workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
        }

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _running = p;

        string? lastErr = null;
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        p.Exited += (_, _) =>
        {
            try { tcs.TrySetResult(p.ExitCode); }
            catch { tcs.TrySetResult(-1); }
        };

        try
        {
            if (!p.Start())
                return new GitCommandResult(false, -1, "Failed to start process.");

            ct.Register(() => TryCancelRunningProcess());

            var stdoutTask = Task.Run(async () =>
            {
                while (!p.StandardOutput.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await p.StandardOutput.ReadLineAsync();
                    if (line == null) break;
                    onLine(line);
                }
            }, ct);

            var stderrTask = Task.Run(async () =>
            {
                while (!p.StandardError.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await p.StandardError.ReadLineAsync();
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    lastErr = line;
                }
            }, ct);

            int exit = await tcs.Task;

            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }

            bool ok = exit == 0;
            return new GitCommandResult(ok, exit, ok ? null : (lastErr ?? $"git exited with code {exit}"));
        }
        catch (OperationCanceledException)
        {
            return new GitCommandResult(false, -2, "Canceled.");
        }
        catch (Exception ex)
        {
            return new GitCommandResult(false, -1, ex.Message);
        }
        finally
        {
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }

            _running = null;
        }
    }
}
