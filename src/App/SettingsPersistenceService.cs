using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RatScanner;

public readonly record struct SettingSaveResult(bool Succeeded, string? ErrorMessage = null);

internal sealed class SettingsPersistenceService : IDisposable
{
    private sealed class SettingState
    {
        internal object? LastPersisted;
        internal long Generation;
        internal bool Initialized;
    }

    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task> _persist;
    private readonly Action<string, Exception> _logFailure;
    private readonly Dictionary<string, SettingState> _states = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private Task _tail = Task.CompletedTask;
    private bool _disposed;

    public SettingsPersistenceService()
        : this(
            _ => Task.Run(RatConfig.SaveConfig),
            static (description, exception) =>
                Logger.LogWarning($"Unable to persist the {description} setting.", exception)
        ) { }

    internal SettingsPersistenceService(
        Func<CancellationToken, Task> persist,
        Action<string, Exception>? logFailure = null
    )
    {
        _persist = persist ?? throw new ArgumentNullException(nameof(persist));
        _logFailure = logFailure ?? ((_, _) => { });
    }

    internal Task<SettingSaveResult> SaveImmediateAsync<T>(
        string key,
        string description,
        T value,
        Func<T> read,
        Action<T> apply,
        Action<T>? applyRuntime = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(apply);

        SettingState state;
        long generation;
        T previous = read();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_states.TryGetValue(key, out state!))
            {
                state = new SettingState();
                _states.Add(key, state);
            }
            if (!state.Initialized)
            {
                state.LastPersisted = previous;
                state.Initialized = true;
            }
            generation = ++state.Generation;
        }

        if (EqualityComparer<T>.Default.Equals(previous, value))
            return Task.FromResult(new SettingSaveResult(true));

        try
        {
            apply(value);
            applyRuntime?.Invoke(value);
        }
        catch (Exception exception)
        {
            apply(previous);
            TryApplyRuntime(description, previous, applyRuntime);
            _logFailure(description, exception);
            return Task.FromResult(new SettingSaveResult(false));
        }

        TaskCompletionSource<SettingSaveResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            CancellationToken token = _lifetime.Token;
            _tail = _tail
                .ContinueWith(
                    _ => RunSaveAsync(key, generation, description, read, apply, applyRuntime, token, completion),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default
                )
                .Unwrap();
        }
        return completion.Task;
    }

    internal Task<SettingSaveResult> SaveValidatedAsync<T>(
        string key,
        string description,
        T value,
        Func<T> read,
        Action<T> apply,
        Func<T, string?> validate,
        Action<T>? applyRuntime = null
    )
    {
        ArgumentNullException.ThrowIfNull(validate);
        string? validationError = validate(value);
        if (validationError is not null)
            return Task.FromResult(new SettingSaveResult(false, validationError));

        return SaveImmediateAsync(key, description, value, read, apply, applyRuntime);
    }

    private async Task RunSaveAsync<T>(
        string key,
        long generation,
        string description,
        Func<T> read,
        Action<T> apply,
        Action<T>? applyRuntime,
        CancellationToken cancellationToken,
        TaskCompletionSource<SettingSaveResult> completion
    )
    {
        if (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
            return;
        }

        try
        {
            await _persist(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_states.TryGetValue(key, out SettingState? state))
                    state.LastPersisted = read();
            }
            completion.TrySetResult(new SettingSaveResult(true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            T? persisted = default;
            bool restore = false;
            lock (_gate)
            {
                if (
                    !_disposed
                    && _states.TryGetValue(key, out SettingState? state)
                    && state.Generation == generation
                    && state.LastPersisted is T value
                )
                {
                    persisted = value;
                    restore = true;
                }
            }

            if (restore)
            {
                apply(persisted!);
                TryApplyRuntime(description, persisted!, applyRuntime);
            }

            _logFailure(description, exception);
            completion.TrySetResult(new SettingSaveResult(false));
        }
    }

    private void TryApplyRuntime<T>(string description, T value, Action<T>? applyRuntime)
    {
        try
        {
            applyRuntime?.Invoke(value);
        }
        catch (Exception exception)
        {
            _logFailure($"previous {description} runtime state", exception);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifetime.Cancel();
        }
        _lifetime.Dispose();
    }
}
