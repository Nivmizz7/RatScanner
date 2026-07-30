using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RatScanner.FetchModels.TarkovTracker;
using GameMode = RatScanner.TarkovDev.GameMode;

namespace RatScanner;

internal enum TrackerConnectionState
{
    NotConfigured,
    Untested,
    Testing,
    Connected,
    MissingPermissions,
    InvalidKey,
    ConnectionError,
}

internal enum TrackerValidationFailure
{
    None,
    InvalidKey,
    WrongGameMode,
    MissingPermissions,
    RateLimited,
    ServiceUnavailable,
    Network,
    UnexpectedResponse,
}

internal sealed record TrackerValidationResult(
    bool Succeeded,
    TrackerValidationFailure Failure,
    IReadOnlyList<string> MissingPermissions,
    string? Detail = null
)
{
    internal static TrackerValidationResult Success { get; } =
        new(true, TrackerValidationFailure.None, Array.Empty<string>());
}

internal static class TarkovTrackerPermissions
{
    internal const string ReadProgress = "GP";
    internal const string ReadTeamProgress = "TP";
    internal const string WriteProgress = "WP";

    internal static IReadOnlyList<string> RequiredForRatScanner { get; } = [ReadProgress];

    internal static IReadOnlyList<string> MissingRequired(IEnumerable<string>? permissions)
    {
        HashSet<string> available = new(permissions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return RequiredForRatScanner.Where(permission => !available.Contains(permission)).ToArray();
    }
}

// Storing information about progression from TarkovTracker API.
public class TarkovTrackerDB : IDisposable
{
    private readonly Func<string, string?, CancellationToken, Task<string>> _get;
    private readonly object _stateLock = new();
    private readonly Dictionary<GameMode, ProgressSnapshot> _cachedProgress = new();
    private TokenResponse? _token;
    private string? _configuredToken;
    private string _configuredEndpoint;
    private GameMode _configuredMode;
    private long _configurationGeneration;
    private CancellationTokenSource _configurationCancellation = new();
    private List<UserProgress> _progress = new();
    private string _self = "";
    private TrackerConnectionState _connectionState = TrackerConnectionState.NotConfigured;
    private DateTimeOffset? _lastSuccessfulValidationUtc;

    private readonly record struct Configuration(
        string? Token,
        string Endpoint,
        GameMode Mode,
        long Generation,
        CancellationToken CancellationToken,
        CancellationTokenSource? OwnedCancellation = null
    );

    private sealed record ProgressSnapshot(string Self, List<UserProgress> Progress);

    public List<UserProgress> Progress
    {
        get
        {
            lock (_stateLock)
                return _progress.ToList();
        }
    }

    public string Self
    {
        get
        {
            lock (_stateLock)
                return _self;
        }
    }

    public string? Token
    {
        get
        {
            lock (_stateLock)
                return _configuredToken;
        }
    }

    internal TrackerConnectionState ConnectionState
    {
        get
        {
            lock (_stateLock)
                return _connectionState;
        }
    }

    internal void MarkConnectionState(TrackerConnectionState state)
    {
        lock (_stateLock)
            _connectionState = state;
    }

    internal DateTimeOffset? LastSuccessfulValidationUtc
    {
        get
        {
            lock (_stateLock)
                return _lastSuccessfulValidationUtc;
        }
    }

    public TarkovTrackerDB()
        : this(APIClient.GetAsync) { }

    internal TarkovTrackerDB(Func<string, string?, string> get)
        : this((url, token, _) => Task.FromResult(get(url, token))) { }

    internal TarkovTrackerDB(Func<string, string?, CancellationToken, Task<string>> get)
    {
        _get = get ?? throw new ArgumentNullException(nameof(get));
        _configuredEndpoint = RatConfig.Tracking.TarkovTracker.OrgEndpoint;
        _configuredMode = RatConfig.GameMode;
    }

    internal long Configure(string? token, string endpoint, GameMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        lock (_stateLock)
        {
            if (IsCurrentConfiguration(token, endpoint, mode))
                return _configurationGeneration;

            _configurationCancellation.Cancel();
            _configurationCancellation.Dispose();
            _configurationCancellation = new CancellationTokenSource();
            _configuredToken = token;
            _configuredEndpoint = endpoint;
            _configuredMode = mode;
            _configurationGeneration++;
            _token = null;
            _connectionState = string.IsNullOrWhiteSpace(token)
                ? TrackerConnectionState.NotConfigured
                : TrackerConnectionState.Untested;
            if (string.IsNullOrWhiteSpace(token))
                ClearProgressStateLocked();
            else
                LoadCachedProgressLocked(mode);
            return _configurationGeneration;
        }
    }

    internal bool IsCurrentConfiguration(long generation)
    {
        lock (_stateLock)
            return generation == _configurationGeneration;
    }

    private bool IsCurrentConfiguration(string? token, string endpoint, GameMode mode) =>
        string.Equals(_configuredToken, token, StringComparison.Ordinal)
        && string.Equals(_configuredEndpoint, endpoint, StringComparison.Ordinal)
        && _configuredMode == mode;

    public bool Init() => InitAsync().GetAwaiter().GetResult();

    internal async Task<bool> InitAsync(CancellationToken cancellationToken = default)
    {
        Configuration configuration = GetConfiguration(cancellationToken);
        try
        {
            return await InitCoreAsync(configuration).ConfigureAwait(false);
        }
        finally
        {
            configuration.OwnedCancellation?.Dispose();
        }
    }

    /// <summary>
    /// Steady-state periodic refresh. Skips the redundant <c>/token</c>
    /// re-validation when the key was already validated and only fetches
    /// <c>/progress</c> (or <c>/team/progress</c>). Falls back to a full
    /// <see cref="InitAsync"/> flow when the token was never validated or was
    /// invalidated since the last refresh, so the connection state always
    /// reflects the current truth.
    /// </summary>
    internal async Task<bool> RefreshProgressAsync(CancellationToken cancellationToken = default)
    {
        Configuration configuration = GetConfiguration(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(configuration.Token))
            {
                ClearRuntimeState(configuration);
                return false;
            }

            bool hasValidatedToken;
            lock (_stateLock)
                hasValidatedToken = _token is not null && IsCurrentLocked(configuration);

            if (hasValidatedToken)
            {
                // Token already validated: skip /token and only fetch progress.
                // UpdateProgressionAsync handles auth failures by clearing
                // _token and flipping state to InvalidKey, so the next refresh
                // falls through to the full init below.
                await UpdateProgressionAsync(configuration).ConfigureAwait(false);
                return true;
            }

            // Token was never validated or was invalidated since the last
            // refresh — do a full init so the connection state is corrected.
            return await InitCoreAsync(configuration).ConfigureAwait(false);
        }
        finally
        {
            configuration.OwnedCancellation?.Dispose();
        }
    }

    private async Task<bool> InitCoreAsync(Configuration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Token))
        {
            ClearRuntimeState(configuration);
            return false;
        }

        TrackerValidationResult validation = await ValidateConfiguredTokenAsync(configuration).ConfigureAwait(false);
        if (!validation.Succeeded)
            return !IsCurrent(configuration);

        await UpdateProgressionAsync(configuration).ConfigureAwait(false);
        return true;
    }

    internal Task<TrackerValidationResult> ValidateCandidateAsync(
        string token,
        string endpoint,
        GameMode mode,
        CancellationToken cancellationToken = default
    ) => ValidateCandidateCoreAsync(token, endpoint, mode, cancellationToken);

    private async Task<TrackerValidationResult> ValidateConfiguredTokenAsync(Configuration configuration)
    {
        lock (_stateLock)
        {
            if (!IsCurrentLocked(configuration))
                return new TrackerValidationResult(false, TrackerValidationFailure.Network, Array.Empty<string>());
            _connectionState = TrackerConnectionState.Testing;
        }

        TrackerValidationResult result = await ValidateCandidateCoreAsync(
                configuration.Token!,
                configuration.Endpoint,
                configuration.Mode,
                configuration.CancellationToken
            )
            .ConfigureAwait(false);

        lock (_stateLock)
        {
            if (!IsCurrentLocked(configuration))
                return result;

            _connectionState = ConnectionStateFor(result);
            if (result.Succeeded)
                _lastSuccessfulValidationUtc = DateTimeOffset.UtcNow;
        }
        return result;
    }

    private async Task<TrackerValidationResult> ValidateCandidateCoreAsync(
        string token,
        string endpoint,
        GameMode mode,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(token))
            return new TrackerValidationResult(false, TrackerValidationFailure.InvalidKey, Array.Empty<string>());

        try
        {
            TokenResponse response = await GetTokenAsync(token, endpoint, cancellationToken).ConfigureAwait(false);
            if (!MatchesGameMode(response.GameMode, token, mode))
            {
                return new TrackerValidationResult(
                    false,
                    TrackerValidationFailure.WrongGameMode,
                    Array.Empty<string>(),
                    response.GameMode
                );
            }

            IReadOnlyList<string> missing = TarkovTrackerPermissions.MissingRequired(response.Permissions);
            if (missing.Count > 0)
            {
                return new TrackerValidationResult(false, TrackerValidationFailure.MissingPermissions, missing);
            }

            lock (_stateLock)
            {
                if (IsCurrentConfiguration(token, endpoint, mode))
                    _token = response;
            }
            return TrackerValidationResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedTokenException exception)
        {
            Logger.LogWarning("TarkovTracker rejected an API key.", exception);
            return new TrackerValidationResult(false, TrackerValidationFailure.InvalidKey, Array.Empty<string>());
        }
        catch (MissingPermissionException exception)
        {
            string? permission = ExtractPermission(exception.Message);
            IReadOnlyList<string> missing = permission is null ? Array.Empty<string>() : [permission];
            Logger.LogWarning("A TarkovTracker API key is missing a required permission.", exception);
            return new TrackerValidationResult(
                false,
                TrackerValidationFailure.MissingPermissions,
                missing,
                exception.Message
            );
        }
        catch (RateLimitExceededException exception)
        {
            Logger.LogWarning("TarkovTracker API-key validation was rate limited.", exception);
            return new TrackerValidationResult(false, TrackerValidationFailure.RateLimited, Array.Empty<string>());
        }
        catch (TaskCanceledException exception)
        {
            Logger.LogWarning("TarkovTracker API-key validation timed out.", exception);
            return new TrackerValidationResult(false, TrackerValidationFailure.Network, Array.Empty<string>());
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode
                    is System.Net.HttpStatusCode.BadGateway
                        or System.Net.HttpStatusCode.ServiceUnavailable
                        or System.Net.HttpStatusCode.GatewayTimeout
            )
        {
            Logger.LogWarning("TarkovTracker was unavailable while validating an API key.", exception);
            return new TrackerValidationResult(
                false,
                TrackerValidationFailure.ServiceUnavailable,
                Array.Empty<string>()
            );
        }
        catch (HttpRequestException exception)
        {
            Logger.LogWarning("Unable to reach TarkovTracker while validating an API key.", exception);
            return new TrackerValidationResult(false, TrackerValidationFailure.Network, Array.Empty<string>());
        }
        catch (JsonException exception)
        {
            Logger.LogWarning("TarkovTracker returned an unexpected API-key response.", exception);
            return new TrackerValidationResult(
                false,
                TrackerValidationFailure.UnexpectedResponse,
                Array.Empty<string>()
            );
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Unable to validate a TarkovTracker API key.", exception);
            return new TrackerValidationResult(
                false,
                TrackerValidationFailure.UnexpectedResponse,
                Array.Empty<string>()
            );
        }
    }

    public int TeammateCount
    {
        get
        {
            lock (_stateLock)
                return _progress.Count(x => x.UserId != _self);
        }
    }

    public bool? TeamProgressAvailable
    {
        get
        {
            lock (_stateLock)
                return _token?.Permissions?.Contains(TarkovTrackerPermissions.ReadTeamProgress);
        }
    }

    public bool? SoloProgressAvailable
    {
        get
        {
            lock (_stateLock)
                return _token?.Permissions?.Contains(TarkovTrackerPermissions.ReadProgress);
        }
    }

    private async Task UpdateProgressionAsync(Configuration configuration)
    {
        bool teamProgressAvailable;
        lock (_stateLock)
        {
            if (!IsCurrentLocked(configuration))
                return;
            teamProgressAvailable =
                RatConfig.Tracking.TarkovTracker.ShowTeam
                && _token?.Permissions?.Contains(TarkovTrackerPermissions.ReadTeamProgress) == true;
        }

        try
        {
            if (teamProgressAvailable)
            {
                TeamProgressResponse response = await GetTeamProgressAsync(configuration).ConfigureAwait(false);
                TeamProgressResponse.Metadata metadata = response.Meta!;
                List<UserProgress> teamProgress = response.TeamProgress!;
                List<UserProgress> progress = teamProgress
                    .Where(x => !metadata.HiddenTeammates!.Contains(x.UserId))
                    .ToList();
                CommitProgress(configuration, metadata.Self!, progress);
            }
            else
            {
                ProgressResponse response = await GetProgressAsync(configuration).ConfigureAwait(false);
                ProgressResponse.Metadata metadata = response.Meta!;
                UserProgress userProgress = response.UserProgress!;
                if (
                    !string.IsNullOrWhiteSpace(metadata.GameMode)
                    && !MatchesGameMode(metadata.GameMode, "", configuration.Mode)
                )
                    throw new JsonSerializationException("TarkovTracker progress response used the wrong game mode.");
                CommitProgress(configuration, metadata.Self!, new List<UserProgress> { userProgress });
            }
        }
        catch (OperationCanceledException) when (configuration.CancellationToken.IsCancellationRequested) { }
        catch (UnauthorizedTokenException exception)
        {
            lock (_stateLock)
            {
                if (IsCurrentLocked(configuration))
                {
                    _token = null;
                    _connectionState = TrackerConnectionState.InvalidKey;
                    ClearProgressStateLocked();
                }
            }
            Logger.LogWarning("TarkovTracker rejected a progression request.", exception);
        }
        catch (MissingPermissionException exception)
        {
            lock (_stateLock)
            {
                if (IsCurrentLocked(configuration))
                    _connectionState = TrackerConnectionState.MissingPermissions;
            }
            Logger.LogWarning("TarkovTracker progression is missing a required permission.", exception);
        }
        catch (RateLimitExceededException exception)
        {
            Logger.LogWarning(
                "TarkovTracker progression refresh was rate limited; cached data was retained.",
                exception
            );
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Unable to refresh TarkovTracker progression; cached data was retained.", exception);
        }
    }

    private async Task<TeamProgressResponse> GetTeamProgressAsync(Configuration configuration)
    {
        string responseText = await _get(
                $"{configuration.Endpoint}/team/progress",
                configuration.Token,
                configuration.CancellationToken
            )
            .ConfigureAwait(false);
        TeamProgressResponse response = DeserializeResponse<TeamProgressResponse>(responseText, "team progress");
        TeamProgressResponse.Metadata? metadata = response.Meta;
        List<UserProgress>? teamProgress = response.TeamProgress;
        if (
            metadata?.HiddenTeammates is null
            || teamProgress is null
            || string.IsNullOrWhiteSpace(metadata.Self)
            || teamProgress.Any(x => x is null || string.IsNullOrWhiteSpace(x.UserId))
        )
        {
            throw new JsonSerializationException("TarkovTracker team progress response is missing required fields.");
        }
        return response;
    }

    private async Task<ProgressResponse> GetProgressAsync(Configuration configuration)
    {
        string responseText = await _get(
                $"{configuration.Endpoint}/progress",
                configuration.Token,
                configuration.CancellationToken
            )
            .ConfigureAwait(false);
        ProgressResponse response = DeserializeResponse<ProgressResponse>(responseText, "progress");
        ProgressResponse.Metadata? metadata = response.Meta;
        UserProgress? userProgress = response.UserProgress;
        if (
            metadata is null
            || userProgress is null
            || string.IsNullOrWhiteSpace(metadata.Self)
            || string.IsNullOrWhiteSpace(userProgress.UserId)
        )
            throw new JsonSerializationException("TarkovTracker progress response is missing required fields.");
        return response;
    }

    private async Task<TokenResponse> GetTokenAsync(string token, string endpoint, CancellationToken cancellationToken)
    {
        string responseText = await _get($"{endpoint}/token", token, cancellationToken).ConfigureAwait(false);
        TokenResponse response = DeserializeResponse<TokenResponse>(responseText, "token");
        if (!string.Equals(response.Id, token, StringComparison.Ordinal) || response.Permissions is null)
            throw new JsonSerializationException("TarkovTracker token response is missing required fields.");
        return response;
    }

    private static T DeserializeResponse<T>(string responseText, string responseName)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(responseText))
            throw new JsonSerializationException($"TarkovTracker {responseName} response was empty.");

        return JsonConvert.DeserializeObject<T>(responseText)
            ?? throw new JsonSerializationException($"TarkovTracker {responseName} response was null.");
    }

    private Configuration GetConfiguration(CancellationToken externalCancellation = default)
    {
        lock (_stateLock)
        {
            if (!externalCancellation.CanBeCanceled)
            {
                return new Configuration(
                    _configuredToken,
                    _configuredEndpoint,
                    _configuredMode,
                    _configurationGeneration,
                    _configurationCancellation.Token
                );
            }

            // Linked CTS is owned by the Configuration and disposed by the async caller.
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                _configurationCancellation.Token,
                externalCancellation
            );
            return new Configuration(
                _configuredToken,
                _configuredEndpoint,
                _configuredMode,
                _configurationGeneration,
                linked.Token,
                linked
            );
        }
    }

    private bool IsCurrentLocked(Configuration configuration) =>
        configuration.Generation == _configurationGeneration
        && configuration.Mode == _configuredMode
        && string.Equals(configuration.Token, _configuredToken, StringComparison.Ordinal)
        && string.Equals(configuration.Endpoint, _configuredEndpoint, StringComparison.Ordinal);

    private bool IsCurrent(Configuration configuration)
    {
        lock (_stateLock)
            return IsCurrentLocked(configuration);
    }

    private void CommitProgress(Configuration configuration, string self, List<UserProgress> progress)
    {
        lock (_stateLock)
        {
            if (!IsCurrentLocked(configuration))
                return;
            _self = self;
            _progress = progress;
            _cachedProgress[configuration.Mode] = new ProgressSnapshot(self, progress.ToList());
        }
    }

    private void ClearRuntimeState(Configuration configuration)
    {
        lock (_stateLock)
        {
            if (!IsCurrentLocked(configuration))
                return;
            _token = null;
            _connectionState = TrackerConnectionState.NotConfigured;
            ClearProgressStateLocked();
        }
    }

    private void LoadCachedProgressLocked(GameMode mode)
    {
        if (_cachedProgress.TryGetValue(mode, out ProgressSnapshot? cached))
        {
            _self = cached.Self;
            _progress = cached.Progress.ToList();
        }
        else
        {
            ClearProgressStateLocked();
        }
    }

    private void ClearProgressStateLocked()
    {
        _self = "";
        _progress = new List<UserProgress>();
    }

    private static bool MatchesGameMode(string? apiMode, string token, GameMode expected)
    {
        string expectedApiMode = expected == GameMode.Pve ? "pve" : "pvp";
        if (!string.IsNullOrWhiteSpace(apiMode))
            return string.Equals(apiMode, expectedApiMode, StringComparison.OrdinalIgnoreCase);

        if (token.StartsWith("PVE_", StringComparison.OrdinalIgnoreCase))
            return expected == GameMode.Pve;
        if (token.StartsWith("PVP_", StringComparison.OrdinalIgnoreCase))
            return expected == GameMode.Regular;
        return expected == GameMode.Regular;
    }

    private static string? ExtractPermission(string message)
    {
        const string marker = "Missing required permission:";
        int index = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;
        string permission = message[(index + marker.Length)..].Trim();
        return permission.Length == 0 ? null : permission;
    }

    private static TrackerConnectionState ConnectionStateFor(TrackerValidationResult result) =>
        result.Succeeded
            ? TrackerConnectionState.Connected
            : result.Failure switch
            {
                TrackerValidationFailure.InvalidKey or TrackerValidationFailure.WrongGameMode =>
                    TrackerConnectionState.InvalidKey,
                TrackerValidationFailure.MissingPermissions => TrackerConnectionState.MissingPermissions,
                _ => TrackerConnectionState.ConnectionError,
            };

    public void Dispose()
    {
        lock (_stateLock)
        {
            _configurationCancellation.Cancel();
            _configurationCancellation.Dispose();
        }
    }
}
