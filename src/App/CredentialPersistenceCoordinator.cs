using System;
using System.Threading;
using System.Threading.Tasks;

namespace RatScanner;

internal static class CredentialPersistenceCoordinator
{
    internal static async Task<SettingSaveResult> PersistCandidateAsync(
        Func<Task<SettingSaveResult>> persistCandidate,
        Func<Task<SettingSaveResult>> restorePrevious,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(persistCandidate);
        ArgumentNullException.ThrowIfNull(restorePrevious);

        SettingSaveResult result = await persistCandidate().ConfigureAwait(false);
        if (!result.Succeeded)
            return result;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SettingSaveResult restored = await restorePrevious().ConfigureAwait(false);
            if (!restored.Succeeded)
                Logger.LogWarning("Unable to restore a credential after the replacement was canceled.");
            throw;
        }
    }
}
