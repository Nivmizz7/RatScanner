using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RatScanner.FetchModels.TarkovTracker;

namespace RatScanner;

internal enum TokenValidationResult
{
    Valid,
    Invalid,
    Unavailable,
}

// Storing information about progression from TarkovTracker API
public class TarkovTrackerDB
{
    private readonly Func<string, string?, string> _get;
    private readonly object _stateLock = new();
    private TokenResponse? _token;
    private bool _badToken;
    private string? _configuredToken;
    private string _configuredEndpoint;
    private long _configurationGeneration;
    private List<UserProgress> _progress = new();
    private string _self = "";

    private readonly record struct Configuration(string? Token, string Endpoint, long Generation);

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
        set => Configure(value, RatConfig.Tracking.TarkovTracker.Endpoint);
    }

    public TarkovTrackerDB()
        : this(APIClient.Get) { }

    internal TarkovTrackerDB(Func<string, string?, string> get)
    {
        _get = get ?? throw new ArgumentNullException(nameof(get));
        _configuredEndpoint = RatConfig.Tracking.TarkovTracker.Endpoint;
    }

    internal long Configure(string? token, string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        lock (_stateLock)
        {
            if (
                string.Equals(_configuredToken, token, StringComparison.Ordinal)
                && string.Equals(_configuredEndpoint, endpoint, StringComparison.Ordinal)
            )
                return _configurationGeneration;

            _configuredToken = token;
            _configuredEndpoint = endpoint;
            _configurationGeneration++;
            ClearRuntimeStateLocked();
            return _configurationGeneration;
        }
    }

    internal bool IsCurrentConfiguration(long generation)
    {
        lock (_stateLock)
            return generation == _configurationGeneration;
    }

    // Set up the TarkovTracker DB
    public bool Init()
    {
        Configuration configuration = GetConfiguration();
        if (string.IsNullOrWhiteSpace(configuration.Token))
        {
            ClearRuntimeState(configuration);
            return false;
        }

        if (!ValidToken(configuration))
            return !IsCurrent(configuration);
        UpdateProgression(configuration);
        return true;
    }

    private bool ValidToken(Configuration configuration)
    {
        bool requiresValidation;
        lock (_stateLock)
        {
            if (!IsCurrentLocked(configuration))
                return false;
            requiresValidation = _token?.Id != configuration.Token;
        }

        if (requiresValidation)
            UpdateToken(configuration);

        lock (_stateLock)
        {
            // Retryable API failures must not cause callers to discard a configured token.
            return IsCurrentLocked(configuration) && !_badToken;
        }
    }

    internal TokenValidationResult UpdateToken() => UpdateToken(GetConfiguration());

    private TokenValidationResult UpdateToken(Configuration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Token))
        {
            ClearRuntimeState(configuration);
            return TokenValidationResult.Invalid;
        }

        try
        {
            TokenResponse newToken = GetToken(configuration.Token, configuration.Endpoint);
            lock (_stateLock)
            {
                if (!IsCurrentLocked(configuration))
                    return TokenValidationResult.Unavailable;
                _token = newToken;
                _badToken = false;
            }
            return TokenValidationResult.Valid;
        }
        catch (RateLimitExceededException e)
        {
            Logger.LogWarning("TarkovTracker token validation was rate limited; it will be retried.", e);
            return TokenValidationResult.Unavailable;
        }
        catch (UnauthorizedTokenException e)
        {
            lock (_stateLock)
            {
                if (!IsCurrentLocked(configuration))
                    return TokenValidationResult.Unavailable;
                ClearProgressStateLocked();
                _badToken = true;
                _token = new TokenResponse { Id = configuration.Token };
            }
            Logger.LogWarning("TarkovTracker rejected the configured token.", e);
            return TokenValidationResult.Invalid;
        }
        catch (Exception e)
        {
            // Network, timeout, status-code, and malformed-response failures are retryable.
            Logger.LogWarning("Unable to validate the TarkovTracker token; it will be retried.", e);
            return TokenValidationResult.Unavailable;
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
                return _token?.Permissions?.Contains("TP");
        }
    }

    public bool? SoloProgressAvailable
    {
        get
        {
            lock (_stateLock)
                return _token?.Permissions?.Contains("GP");
        }
    }

    private void UpdateProgression(Configuration configuration)
    {
        bool teamProgressAvailable;
        bool soloProgressAvailable;
        lock (_stateLock)
        {
            if (!IsCurrentLocked(configuration))
                return;
            teamProgressAvailable = _token?.Permissions?.Contains("TP") == true;
            soloProgressAvailable = _token?.Permissions?.Contains("GP") == true;
        }

        try
        {
            if (teamProgressAvailable)
            {
                TeamProgressResponse response = GetTeamProgress(configuration);
                List<UserProgress> progress = response
                    .TeamProgress.Where(x => !response.Meta.HiddenTeammates.Contains(x.UserId))
                    .ToList();
                CommitProgress(configuration, response.Meta.Self, progress);
            }
            else if (soloProgressAvailable)
            {
                ProgressResponse response = GetProgress(configuration);
                CommitProgress(configuration, response.Meta.Self, new List<UserProgress> { response.UserProgress });
            }
        }
        catch (RateLimitExceededException e)
        {
            Logger.LogWarning("TarkovTracker progression refresh was rate limited; existing data was retained.", e);
        }
        catch (UnauthorizedTokenException e)
        {
            // Drop the cached token so the next refresh re-validates instead of reusing a
            // token the server just rejected (which would otherwise repeat the 401).
            lock (_stateLock)
                _token = null;
            Logger.LogWarning("TarkovTracker rejected a progression request; existing data was retained.", e);
        }
        catch (Exception e)
        {
            // Preserve the last known-good snapshot when the remote service is unavailable or malformed.
            Logger.LogWarning("Unable to refresh TarkovTracker progression; existing data was retained.", e);
        }
    }

    private TeamProgressResponse GetTeamProgress(Configuration configuration)
    {
        string responseText = _get($"{configuration.Endpoint}/team/progress", configuration.Token);
        TeamProgressResponse response = DeserializeResponse<TeamProgressResponse>(responseText, "team progress");
        if (
            response.Meta is null
            || response.TeamProgress is null
            || response.Meta.HiddenTeammates is null
            || string.IsNullOrWhiteSpace(response.Meta.Self)
        )
        {
            throw new JsonSerializationException("TarkovTracker team progress response is missing required fields.");
        }
        return response;
    }

    private ProgressResponse GetProgress(Configuration configuration)
    {
        string responseText = _get($"{configuration.Endpoint}/progress", configuration.Token);
        ProgressResponse response = DeserializeResponse<ProgressResponse>(responseText, "progress");
        if (response.Meta is null || response.UserProgress is null || string.IsNullOrWhiteSpace(response.Meta.Self))
            throw new JsonSerializationException("TarkovTracker progress response is missing required fields.");
        return response;
    }

    public bool TestToken(string testToken)
    {
        return ValidateToken(testToken) == TokenValidationResult.Valid;
    }

    internal TokenValidationResult ValidateToken(string testToken)
    {
        if (string.IsNullOrWhiteSpace(testToken))
            return TokenValidationResult.Invalid;

        try
        {
            GetToken(testToken, GetConfiguration().Endpoint);
            return TokenValidationResult.Valid;
        }
        catch (UnauthorizedTokenException)
        {
            return TokenValidationResult.Invalid;
        }
        catch (Exception)
        {
            return TokenValidationResult.Unavailable;
        }
    }

    private TokenResponse GetToken(string? workingToken, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(workingToken))
            throw new UnauthorizedTokenException("Token is empty");

        string responseText = _get($"{endpoint}/token", workingToken);
        TokenResponse response = DeserializeResponse<TokenResponse>(responseText, "token");
        if (!string.Equals(response.Id, workingToken, StringComparison.Ordinal) || response.Permissions is null)
        {
            throw new JsonSerializationException("TarkovTracker token response is missing required fields.");
        }
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

    private Configuration GetConfiguration()
    {
        lock (_stateLock)
            return new Configuration(_configuredToken, _configuredEndpoint, _configurationGeneration);
    }

    private bool IsCurrentLocked(Configuration configuration) =>
        configuration.Generation == _configurationGeneration
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
        }
    }

    private void ClearRuntimeState(Configuration configuration)
    {
        lock (_stateLock)
        {
            if (IsCurrentLocked(configuration))
                ClearRuntimeStateLocked();
        }
    }

    private void ClearRuntimeStateLocked()
    {
        _token = null;
        _badToken = false;
        ClearProgressStateLocked();
    }

    private void ClearProgressStateLocked()
    {
        _self = "";
        _progress = new List<UserProgress>();
    }
}
