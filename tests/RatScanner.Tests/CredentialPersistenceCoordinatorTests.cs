using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RatScanner.Tests;

public sealed class CredentialPersistenceCoordinatorTests
{
    [Fact]
    public async Task Cancellation_after_candidate_save_restores_the_previous_value()
    {
        bool candidateSaved = false;
        bool previousRestored = false;
        using CancellationTokenSource cancellation = new();

        Task<SettingSaveResult> operation = CredentialPersistenceCoordinator.PersistCandidateAsync(
            () =>
            {
                candidateSaved = true;
                cancellation.Cancel();
                return Task.FromResult(new SettingSaveResult(true));
            },
            () =>
            {
                previousRestored = true;
                return Task.FromResult(new SettingSaveResult(true));
            },
            cancellation.Token
        );

        await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        Assert.True(candidateSaved);
        Assert.True(previousRestored);
    }

    [Fact]
    public async Task Failed_candidate_save_does_not_restore_or_throw()
    {
        bool previousRestored = false;

        SettingSaveResult result = await CredentialPersistenceCoordinator.PersistCandidateAsync(
            () => Task.FromResult(new SettingSaveResult(false)),
            () =>
            {
                previousRestored = true;
                return Task.FromResult(new SettingSaveResult(true));
            },
            CancellationToken.None
        );

        Assert.False(result.Succeeded);
        Assert.False(previousRestored);
    }
}
