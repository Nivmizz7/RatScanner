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
            token => Task.Run(RatConfig.SaveConfig, token),
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
        CancellationToken lifetimeToken;
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
            // Capture while provably undisposed; Dispose() may dispose _lifetime
            // between this lock and the enqueue below, after which reading
            // _lifetime.Token would throw ObjectDisposedException.
            lifetimeToken = _lifetime.Token;
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
        bool disposed;
        lock (_gate)
        {
            disposed = _disposed;
            if (!disposed)
            {
                _tail = _tail
                    .ContinueWith(
                        _ =>
                            RunSaveAsync(
                                key,
                                generation,
                                value,
                                description,
                                apply,
                                applyRuntime,
                                completion,
                                lifetimeToken
                            ),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default
                    )
                    .Unwrap();
            }
        }

        if (disposed)
        {
            // Guard the rollback: if apply(previous) throws, the returned task
            // must still complete so the caller does not hang awaiting it.
            try
            {
                apply(previous);
                TryApplyRuntime(description, previous, applyRuntime);
            }
            catch (Exception exception)
            {
                _logFailure(description, exception);
            }
            completion.TrySetResult(new SettingSaveResult(false));
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
        T persistedValue,
        string description,
        Action<T> apply,
        Action<T>? applyRuntime,
        TaskCompletionSource<SettingSaveResult> completion,
        CancellationToken cancellationToken
    )
    {
        if (cancellationToken.IsCancellationRequested)
        {
            TryRestoreAfterFailure(key, generation, description, apply, applyRuntime, allowDuringDispose: true);
            completion.TrySetResult(new SettingSaveResult(false));
            return;
        }

        try
        {
            await _persist(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                // Saves are serialized, so this operation is the last value known to
                // have reached disk until a later generation completes. Do not call
                // read() here: a newer optimistic UI change may already be in memory.
                if (_states.TryGetValue(key, out SettingState? state))
                    state.LastPersisted = persistedValue;
            }
            completion.TrySetResult(new SettingSaveResult(true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryRestoreAfterFailure(key, generation, description, apply, applyRuntime, allowDuringDispose: true);
            completion.TrySetResult(new SettingSaveResult(false));
        }
        catch (Exception exception)
        {
            TryRestoreAfterFailure(key, generation, description, apply, applyRuntime, allowDuringDispose: false);
            _logFailure(description, exception);
            completion.TrySetResult(new SettingSaveResult(false));
        }
    }

    private void TryRestoreAfterFailure<T>(
        string key,
        long generation,
        string description,
        Action<T> apply,
        Action<T>? applyRuntime,
        bool allowDuringDispose
    )
    {
        T? persisted = default;
        bool restore = false;
        lock (_gate)
        {
            if (
                (allowDuringDispose || !_disposed)
                && _states.TryGetValue(key, out SettingState? state)
                && state.Generation == generation
            )
            {
                // `is T` misses persisted nulls (e.g. a string? saved as null): a
                // failed save would then leave the new runtime value applied
                // instead of restoring the persisted null. Restore whenever the
                // stored value is a T, or is null and T permits it.
                if (state.LastPersisted is T value)
                {
                    persisted = value;
                    restore = true;
                }
                else if (state.LastPersisted is null && (default(T) is null || !typeof(T).IsValueType))
                {
                    persisted = default;
                    restore = true;
                }
            }
        }

        if (!restore)
            return;

        try
        {
            apply(persisted!);
            TryApplyRuntime(description, persisted!, applyRuntime);
        }
        catch (Exception exception)
        {
            _logFailure($"previous {description} value", exception);
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
