using System.Collections.Concurrent;
using System.Management;

namespace RemoteOpsTool.Helpers;

/// <summary>
/// Bounded WMI/DCOM connection pool. Every scope is leased to one caller at a
/// time because <see cref="ManagementScope"/> is not safe to share across
/// concurrent operations. Independent callers can use separate scopes up to
/// <see cref="MaxConcurrentScopes"/> for the same host/credential/namespace.
/// </summary>
internal sealed class RemoteWmiConnectionPool
{
    internal const int MaxConcurrentScopes = 4;
    private static readonly TimeSpan ConnectFailureCooldown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan QueryFailureCooldown = TimeSpan.FromSeconds(1);

    private readonly Func<ManagementScope> _scopeFactory;
    private readonly SemaphoreSlim _capacity = new(MaxConcurrentScopes, MaxConcurrentScopes);
    private readonly ConcurrentBag<ManagementScope> _idleScopes = new();
    private readonly object _cooldownSync = new();
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

    public RemoteWmiConnectionPool(Func<ManagementScope> scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<RemoteWmiScopeLease> AcquireAsync(CancellationToken ct)
    {
        await _capacity.WaitAsync(ct).ConfigureAwait(false);

        ManagementScope? scope = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfCoolingDown();

            if (!_idleScopes.TryTake(out scope))
                scope = _scopeFactory();

            if (!scope.IsConnected)
                scope.Connect();

            return new RemoteWmiScopeLease(this, scope);
        }
        catch (OperationCanceledException)
        {
            _capacity.Release();
            throw;
        }
        catch
        {
            RecordFailure(ConnectFailureCooldown);
            _capacity.Release();
            throw;
        }
    }

    public void Release(ManagementScope scope, bool reusable)
    {
        try
        {
            if (reusable && scope.IsConnected)
                _idleScopes.Add(scope);
        }
        finally
        {
            _capacity.Release();
        }
    }

    public void RecordQueryFailure() => RecordFailure(QueryFailureCooldown);

    public void Clear()
    {
        while (_idleScopes.TryTake(out _))
        {
        }
    }

    private void RecordFailure(TimeSpan cooldown)
    {
        lock (_cooldownSync)
        {
            var candidate = DateTimeOffset.UtcNow + cooldown;
            if (candidate > _cooldownUntil)
                _cooldownUntil = candidate;
        }
    }

    private void ThrowIfCoolingDown()
    {
        DateTimeOffset cooldownUntil;
        lock (_cooldownSync)
            cooldownUntil = _cooldownUntil;

        if (DateTimeOffset.UtcNow < cooldownUntil)
            throw new RemoteWmiConnectionCooldownException(
                $"WMI/DCOM connection is cooling down until {cooldownUntil:O}.");
    }
}

internal sealed class RemoteWmiScopeLease : IDisposable
{
    private readonly RemoteWmiConnectionPool _pool;
    private ManagementScope? _scope;
    private bool _disposed;
    private bool _reusable = true;

    public RemoteWmiScopeLease(RemoteWmiConnectionPool pool, ManagementScope scope)
    {
        _pool = pool;
        _scope = scope;
    }

    public ManagementScope Scope =>
        _scope ?? throw new ObjectDisposedException(nameof(RemoteWmiScopeLease));

    public void MarkHealthy() => _reusable = true;

    public void MarkFailed()
    {
        _reusable = false;
        _pool.RecordQueryFailure();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        var scope = _scope;
        _scope = null;
        if (scope != null)
            _pool.Release(scope, _reusable);
    }
}

internal sealed class RemoteWmiConnectionCooldownException : InvalidOperationException
{
    public RemoteWmiConnectionCooldownException(string message) : base(message)
    {
    }
}
