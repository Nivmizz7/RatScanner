using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RatScanner.FetchModels.TarkovTracker;
using GameMode = RatScanner.TarkovDev.GameMode;

namespace RatScanner.Runtime;

internal sealed record TrackerStateSnapshot(
    IReadOnlyList<UserProgress> Progress,
    string Self,
    string? Token,
    TrackerConnectionState ConnectionState
)
{
    internal UserProgress CurrentUser =>
        Progress.FirstOrDefault(progress => progress.UserId == Self) ?? new UserProgress();
}

internal interface ITrackerService
{
    TrackerStateSnapshot State { get; }

    Task ActivateModeAsync(GameMode mode, CancellationToken cancellationToken = default);

    Task<TrackerValidationResult> ValidateOrgKeyAsync(
        GameMode mode,
        string token,
        CancellationToken cancellationToken = default
    );
}
