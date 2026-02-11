using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CbetaTranslator.App.Services;

public interface IGitRepoService
{
    Task<bool> CheckGitAvailableAsync(CancellationToken ct);

    Task<GitCommandResult> CloneAsync(
        string repoUrl,
        string targetDir,
        IProgress<string> progress,
        CancellationToken ct);

    /// <summary>
    /// Ensures the repo is safe to update.
    /// - Auto-cleans known app artifacts (cache/index/debug log).
    /// - If anything else is dirty, returns a blocking reason and sample lines.
    /// </summary>
    Task<GitCleanCheckResult> EnsureCleanForUpdateAsync(
        string repoDir,
        IProgress<string> progress,
        CancellationToken ct);

    Task<GitCommandResult> FetchAsync(
        string repoDir,
        IProgress<string> progress,
        CancellationToken ct);

    Task<GitCommandResult> PullFfOnlyAsync(
        string repoDir,
        IProgress<string> progress,
        CancellationToken ct);

    void TryCancelRunningProcess();
}

public sealed record GitCommandResult(bool Success, int ExitCode, string? Error);

public sealed record GitCleanCheckResult(
    bool CanProceed,
    string? BlockReason,
    IReadOnlyList<string> EvidenceLines);
