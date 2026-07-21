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
            try
            {
                SettingSaveResult restored = await restorePrevious().ConfigureAwait(false);
                if (!restored.Succeeded)
                    Logger.LogWarning("Unable to restore a credential after the replacement was canceled.");
            }
            catch (Exception exception)
            {
                // The original cancellation must still propagate; log rollback
                // failures instead of masking the OperationCanceledException.
                Logger.LogWarning("Unable to restore a credential after the replacement was canceled.", exception);
            }
            throw;
        }
    }
}
