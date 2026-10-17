using System.Security.Cryptography;
using System.Text;

namespace VisualStudioMcpServer.ConnectionKeys;

/// <summary>Terminal and live states for an issued connection grant.</summary>
public enum ConnectionKeyState { Pending, Reserved, Active, Closed, Expired, Revoked, Canceled, FailedClosed }
/// <summary>Machine-readable store outcomes.</summary>
public enum ConnectionKeyResult { Success, Invalid, Expired, AlreadyUsed, CapacityExceeded, RateLimited, NotFound, IdentityMismatch, SessionMismatch, SessionRequired }
/// <summary>Sanitized result of reserving a new MCP session.</summary>
public sealed record ConnectionKeyReservation(ConnectionKeyResult Result, Guid ReservationId)
{
    /// <inheritdoc />
    public override string ToString() => $"ConnectionKeyReservation({Result})";
}
/// <summary>Non-secret store summary.</summary>
public sealed record ConnectionKeyStoreStatus(int PendingCount, int ActiveCount, DateTimeOffset? NearestExpiry);
/// <summary>Sanitized result of issuing a pending connection key.</summary>
public sealed record ConnectionKeyIssueResult(Guid IssuanceId, ConnectionKeyResult Result, string? Key, DateTimeOffset? ExpiresAt)
{
    /// <inheritdoc />
    public override string ToString() => $"ConnectionKeyIssueResult({Result}, credential-redacted)";
}

/// <summary>
/// Thread-safe credential store. Only hashes of bearer credentials are retained.
/// </summary>
public sealed class ConnectionKeyStore
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(8);
    private static readonly TimeSpan TombstoneLifetime = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);
    private const int MaximumLive = 8;
    private const int MaximumTombstones = 64;
    private readonly object _gate = new();
    private readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly IConnectionKeyClock _clock;
    private readonly IConnectionKeyRandom _random;
    private readonly Queue<long> _authFailures = new();
    private TimeSpan _lastSweep;
    private string? _pendingKeyId;

    /// <summary>Creates an instance-bound connection-key store.</summary>
    public ConnectionKeyStore(IConnectionKeyClock clock, IConnectionKeyRandom random, int visualStudioPid, long visualStudioStartTicks, int serverPid, string serverInstanceId, string url)
    {
        _clock = clock; _random = random;
        Identity = new ConnectionKeyIdentity(visualStudioPid, visualStudioStartTicks, serverPid, serverInstanceId, url);
    }

    /// <summary>Gets immutable instance identity associated with issued keys.</summary>
    public ConnectionKeyIdentity Identity { get; }

    /// <summary>Issues one fresh pending key, canceling only the previous pending key.</summary>
    public ConnectionKeyIssueResult Issue() => Issue(Guid.NewGuid());
    /// <summary>Issues a pending key correlated to an owner-generated issuance ID.</summary>
    public ConnectionKeyIssueResult Issue(Guid issuanceId)
    {
        if (issuanceId == Guid.Empty) return new(issuanceId, ConnectionKeyResult.Invalid, null, null);
        lock (_gate)
        {
            SweepLocked();
            if (_grants.Values.Count(g => g.State is not (ConnectionKeyState.Pending or ConnectionKeyState.Reserved or ConnectionKeyState.Active)) >= MaximumTombstones) return new(issuanceId, ConnectionKeyResult.CapacityExceeded, null, null);
            if (_grants.Values.Count(g => g.State is ConnectionKeyState.Active or ConnectionKeyState.Reserved) >= MaximumLive)
                return new(issuanceId, ConnectionKeyResult.CapacityExceeded, null, null);
            if (_pendingKeyId is not null && _grants.TryGetValue(_pendingKeyId, out var old) && old.State == ConnectionKeyState.Pending) Terminalize(old, ConnectionKeyState.Canceled);
            var keyId = NewUniqueId(16);
            var token = NewSecret(); var otp = NewSecret();
            var issuedAt = _clock.UtcNow;
            var expiry = issuedAt.Add(PendingLifetime);
            var payload = new ConnectionKeyPayload(1, keyId, Identity.VisualStudioPid, Identity.VisualStudioStartTicks, Identity.ServerPid, Identity.ServerInstanceId, Identity.Url, token, otp, expiry.ToUnixTimeSeconds());
            var bearer = payload.ToBearerValue();
            var hash = SHA256.HashData(Encoding.ASCII.GetBytes(bearer));
            var grant = new Grant(issuanceId, keyId, hash, issuedAt, _clock.MonotonicElapsed, expiry, _clock.MonotonicElapsed + PendingLifetime);
            _grants.Add(keyId, grant); _pendingKeyId = keyId;
            var key = ConnectionKeyCodec.Encode(payload);
            return new(issuanceId, ConnectionKeyResult.Success, key, expiry);
        }
    }

    /// <summary>Checks owner-supplied instance identity against the store.</summary>
    public bool MatchesIdentity(ConnectionKeyIdentity identity) => Identity == identity;

    /// <summary>Validates that an encoded key exactly describes this store's pending or active grant.</summary>
    internal ConnectionKeyResult ValidateEnvelope(ConnectionKeyPayload payload, ConnectionKeyIdentity identity)
    {
        lock (_gate)
        {
            SweepLocked();
            if (IsRateLimitedLocked()) return ConnectionKeyResult.RateLimited;
            if (!Identity.Equals(identity) || payload.VisualStudioPid != Identity.VisualStudioPid ||
                payload.VisualStudioStartTicks != Identity.VisualStudioStartTicks || payload.ServerPid != Identity.ServerPid ||
                payload.ServerInstanceId != Identity.ServerInstanceId || payload.Url != Identity.Url)
                return FailResult(ConnectionKeyResult.IdentityMismatch);
            if (!_grants.TryGetValue(payload.KeyId, out var grant) ||
                payload.ExpiresAt != grant.PendingExpiry.ToUnixTimeSeconds() ||
                !FixedTimeMatch(grant.BearerHash, payload.ToBearerValue()))
                return FailResult(ConnectionKeyResult.Invalid);
            return grant.State switch
            {
                ConnectionKeyState.Pending when IsPendingExpired(grant) => ExpireAndFail(grant),
                ConnectionKeyState.Pending => ConnectionKeyResult.Success,
                ConnectionKeyState.Active when IsActiveExpired(grant) => ExpireAndFail(grant),
                ConnectionKeyState.Active => ConnectionKeyResult.Success,
                ConnectionKeyState.Expired or ConnectionKeyState.Revoked => FailResult(ConnectionKeyResult.Expired),
                _ => FailResult(ConnectionKeyResult.AlreadyUsed)
            };
        }
    }

    private ConnectionKeyResult ExpireAndFail(Grant grant)
    {
        Terminalize(grant, ConnectionKeyState.Expired);
        return FailResult(ConnectionKeyResult.Expired);
    }

    /// <summary>Checks whether a session ID has belonged to a key grant, including retained terminal IDs.</summary>
    public bool IsKeyedSession(string sessionId)
    {
        lock (_gate)
        {
            SweepLocked();
            return _grants.Values.Any(grant => string.Equals(grant.SessionId, sessionId, StringComparison.Ordinal));
        }
    }

    /// <summary>Atomically reserves the one permitted new session for a bearer and matching instance.</summary>
    public ConnectionKeyReservation Reserve(string bearer, ConnectionKeyIdentity identity)
    {
        lock (_gate)
        {
            SweepLocked();
            if (IsRateLimitedLocked()) return new(ConnectionKeyResult.RateLimited, Guid.Empty);
            if (!ConnectionKeyCodec.TryParseBearer(bearer, out var keyId, out _, out _)) return Fail(ConnectionKeyResult.Invalid);
            if (!Identity.Equals(identity)) return Fail(ConnectionKeyResult.IdentityMismatch);
            if (!_grants.TryGetValue(keyId, out var grant)) return Fail(ConnectionKeyResult.Invalid);
            if (!FixedTimeMatch(grant.BearerHash, bearer)) return Fail(ConnectionKeyResult.Invalid);
            if (grant.State == ConnectionKeyState.Expired) return Fail(ConnectionKeyResult.Expired);
            if (IsPendingExpired(grant)) { Terminalize(grant, ConnectionKeyState.Expired); return Fail(ConnectionKeyResult.Expired); }
            if (grant.State != ConnectionKeyState.Pending) return Fail(ConnectionKeyResult.AlreadyUsed);
            grant.State = ConnectionKeyState.Reserved;
            grant.ReservationId = Guid.NewGuid();
            grant.ReservedMonotonic = _clock.MonotonicElapsed;
            grant.ReservedAt = _clock.UtcNow;
            _pendingKeyId = null;
            return new(ConnectionKeyResult.Success, grant.ReservationId);
        }
    }

    /// <summary>Validates a live pending bearer without consuming its one-time admission.</summary>
    public ConnectionKeyResult ValidatePending(string bearer, ConnectionKeyIdentity identity)
    {
        lock (_gate)
        {
            SweepLocked();
            if (IsRateLimitedLocked()) return ConnectionKeyResult.RateLimited;
            if (!ConnectionKeyCodec.TryParseBearer(bearer, out var keyId, out _, out _)) return FailResult(ConnectionKeyResult.Invalid);
            if (!Identity.Equals(identity)) return FailResult(ConnectionKeyResult.IdentityMismatch);
            if (!_grants.TryGetValue(keyId, out var grant) || !FixedTimeMatch(grant.BearerHash, bearer)) return FailResult(ConnectionKeyResult.Invalid);
            if (grant.State == ConnectionKeyState.Expired) return FailResult(ConnectionKeyResult.Expired);
            if (IsPendingExpired(grant)) { Terminalize(grant, ConnectionKeyState.Expired); return FailResult(ConnectionKeyResult.Expired); }
            return grant.State == ConnectionKeyState.Pending ? ConnectionKeyResult.Success : ConnectionKeyResult.AlreadyUsed;
        }
    }

    /// <summary>Marks initialization successful and binds the reservation to exactly one SDK session.</summary>
    public ConnectionKeyResult Activate(Guid reservationId, string sessionId)
    {
        lock (_gate)
        {
            SweepLocked();
            var grant = FindReservation(reservationId);
            if (grant is null || grant.State != ConnectionKeyState.Reserved) return ConnectionKeyResult.AlreadyUsed;
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 256 || sessionId.Any(c => c > 0x7f)) { Terminalize(grant, ConnectionKeyState.FailedClosed); return ConnectionKeyResult.Invalid; }
            if (_grants.Values.Any(g => g.State == ConnectionKeyState.Active && g.SessionId == sessionId)) { Terminalize(grant, ConnectionKeyState.FailedClosed); return ConnectionKeyResult.SessionMismatch; }
            grant.State = ConnectionKeyState.Active; grant.SessionId = sessionId; grant.ActivatedAt = _clock.MonotonicElapsed; grant.ActivatedUtc = _clock.UtcNow; grant.LastActivity = grant.ActivatedAt;
            return ConnectionKeyResult.Success;
        }
    }
    /// <summary>Fails a pending or reserved initialization closed; it can never reopen.</summary>
    public ConnectionKeyResult FailClosed(Guid reservationId)
    {
        lock (_gate)
        {
            var grant = FindReservation(reservationId);
            if (grant is null || grant.State != ConnectionKeyState.Reserved) return ConnectionKeyResult.NotFound;
            Terminalize(grant, ConnectionKeyState.FailedClosed);
            return ConnectionKeyResult.Success;
        }
    }

    /// <summary>Authenticates an active grant against both its bearer and exact SDK session.</summary>
    public ConnectionKeyResult AuthenticateSession(string bearer, string sessionId, ConnectionKeyIdentity identity)
    {
        lock (_gate)
        {
            SweepLocked();
            if (IsRateLimitedLocked()) return ConnectionKeyResult.RateLimited;
            if (!Identity.Equals(identity)) return FailResult(ConnectionKeyResult.IdentityMismatch);
            if (!ConnectionKeyCodec.TryParseBearer(bearer, out var keyId, out _, out _)) return FailResult(ConnectionKeyResult.Invalid);
            if (!_grants.TryGetValue(keyId, out var grant) || !FixedTimeMatch(grant.BearerHash, bearer)) return FailResult(ConnectionKeyResult.Invalid);
            if (grant.State is ConnectionKeyState.Expired or ConnectionKeyState.Revoked) return FailResult(ConnectionKeyResult.Expired);
            if (IsActiveExpired(grant)) { Terminalize(grant, ConnectionKeyState.Expired); return FailResult(ConnectionKeyResult.Expired); }
            if (grant.State != ConnectionKeyState.Active) return FailResult(ConnectionKeyResult.AlreadyUsed);
            if (!string.Equals(grant.SessionId, sessionId, StringComparison.Ordinal)) return FailResult(ConnectionKeyResult.SessionMismatch);
            grant.LastActivity = _clock.MonotonicElapsed;
            return ConnectionKeyResult.Success;
        }
    }

    /// <summary>Gets the lifetime token for a reserved initialization request.</summary>
    public bool TryGetReservationCancellationToken(Guid reservationId, out CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var grant = FindReservation(reservationId);
            cancellationToken = grant?.Lifetime.Token ?? CancellationToken.None;
            return grant is not null && grant.State == ConnectionKeyState.Reserved;
        }
    }

    /// <summary>Gets the lifetime token for requests bound to an active SDK session.</summary>
    public bool TryGetSessionCancellationToken(string sessionId, out CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var grant = _grants.Values.FirstOrDefault(g => g.State == ConnectionKeyState.Active && g.SessionId == sessionId);
            cancellationToken = grant?.Lifetime.Token ?? CancellationToken.None;
            return grant is not null;
        }
    }
    /// <summary>Closes an active session before forwarding a client DELETE.</summary>
    public ConnectionKeyResult CloseSession(string sessionId)
    {
        lock (_gate)
        {
            var grant = _grants.Values.FirstOrDefault(g => g.State == ConnectionKeyState.Active && g.SessionId == sessionId);
            if (grant is null) return ConnectionKeyResult.NotFound;
            Terminalize(grant, ConnectionKeyState.Closed);
            return ConnectionKeyResult.Success;
        }
    }

    /// <summary>Cancels only the currently pending issuance when its key ID matches.</summary>
    public ConnectionKeyResult CancelPending(Guid issuanceId)
    {
        lock (_gate)
        {
            SweepLocked();
            if (_pendingKeyId is null || !_grants.TryGetValue(_pendingKeyId, out var grant) || grant.State != ConnectionKeyState.Pending || grant.IssuanceId != issuanceId) return ConnectionKeyResult.NotFound;
            Terminalize(grant, ConnectionKeyState.Canceled);
            return ConnectionKeyResult.Success;
        }
    }

    /// <summary>Revokes all key grants while leaving the owning server and legacy auth untouched.</summary>
    public void RevokeAll()
    {
        lock (_gate)
        {
            foreach (var grant in _grants.Values.Where(g => g.State is ConnectionKeyState.Pending or ConnectionKeyState.Reserved or ConnectionKeyState.Active)) Terminalize(grant, ConnectionKeyState.Revoked);
            _pendingKeyId = null;
        }
    }

    /// <summary>Runs a bounded periodic sweep and returns a non-secret summary.</summary>
    public ConnectionKeyStoreStatus GetStatus()
    {
        lock (_gate)
        {
            SweepLocked();
            var live = _grants.Values.Where(g => g.State is ConnectionKeyState.Pending or ConnectionKeyState.Reserved or ConnectionKeyState.Active).ToArray();
            var expiry = live.Select(DeadlineUtc).MinNullable();
            return new(live.Count(g => g.State == ConnectionKeyState.Pending), live.Count(g => g.State is ConnectionKeyState.Active or ConnectionKeyState.Reserved), expiry);
        }
    }

    /// <summary>Returns true when failed/malformed key-auth attempts are at the per-instance limit.</summary>
    public bool IsKeyAuthRateLimited()
    {
        lock (_gate)
        {
            PruneAuthFailuresLocked();
            return _authFailures.Count >= 10;
        }
    }

    /// <summary>Records a failed key-auth attempt in a fixed-capacity rolling minute window.</summary>
    public void RecordFailedKeyAuth()
    {
        lock (_gate)
        {
            PruneAuthFailuresLocked();
            if (_authFailures.Count >= 10) return;
            _authFailures.Enqueue(_clock.MonotonicElapsed.Ticks);
        }
    }

    private ConnectionKeyReservation Fail(ConnectionKeyResult result)
    {
        PruneAuthFailuresLocked();
        if (_authFailures.Count >= 10) return new(result, Guid.Empty);
        _authFailures.Enqueue(_clock.MonotonicElapsed.Ticks);
        return new(result, Guid.Empty);
    }
    private ConnectionKeyResult FailOutside(ConnectionKeyResult result) { RecordFailedKeyAuth(); return result; }
    private ConnectionKeyResult FailResult(ConnectionKeyResult result) { PruneAuthFailuresLocked(); if (_authFailures.Count < 10) _authFailures.Enqueue(_clock.MonotonicElapsed.Ticks); return result; }
    private Grant? FindReservation(Guid id) => _grants.Values.FirstOrDefault(g => g.ReservationId == id);
    private bool IsPendingExpired(Grant g) => g.State == ConnectionKeyState.Pending && _clock.MonotonicElapsed - g.IssuedMonotonic >= PendingLifetime;
    private bool IsActiveExpired(Grant g) => g.State == ConnectionKeyState.Active && (_clock.MonotonicElapsed - g.LastActivity >= IdleLifetime || _clock.MonotonicElapsed - g.ActivatedAt >= AbsoluteLifetime);
    private string NewUniqueId(int size) { Span<byte> b = stackalloc byte[size]; for (var i = 0; i < 16; i++) { _random.Fill(b); var id = ConnectionKeyCodec.Base64UrlEncode(b); if (!_grants.ContainsKey(id)) return id; } throw new InvalidOperationException("Credential generation failed."); }
    private string NewSecret() { Span<byte> b = stackalloc byte[32]; _random.Fill(b); return ConnectionKeyCodec.Base64UrlEncode(b); }
    private static bool FixedTimeMatch(byte[] stored, string bearer) { var candidate = SHA256.HashData(Encoding.ASCII.GetBytes(bearer)); return CryptographicOperations.FixedTimeEquals(stored, candidate); }
    private DateTimeOffset DeadlineUtc(Grant g) => g.State == ConnectionKeyState.Pending ? g.IssuedUtc + PendingLifetime : g.State == ConnectionKeyState.Reserved ? (g.ReservedAt ?? g.IssuedUtc) + AbsoluteLifetime : Min(g.ActivatedUtc + AbsoluteLifetime, g.ActivatedUtc + (g.LastActivity - g.ActivatedAt) + IdleLifetime);
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
    private void Terminalize(Grant grant, ConnectionKeyState state) { grant.State = state; grant.TombstoneUntil = _clock.MonotonicElapsed + TombstoneLifetime; if (_pendingKeyId == grant.KeyId) _pendingKeyId = null; ThreadPool.QueueUserWorkItem(static state => { try { ((CancellationTokenSource)state!).Cancel(); } catch (AggregateException) { } }, grant.Lifetime); }
    private void SweepLocked()
    {
        var now = _clock.MonotonicElapsed;
        if (now - _lastSweep < TimeSpan.FromSeconds(5)) return;
        _lastSweep = now;
        foreach (var g in _grants.Values.ToArray())
        {
            if (g.State == ConnectionKeyState.Pending && IsPendingExpired(g)) Terminalize(g, ConnectionKeyState.Expired);
            else if (g.State == ConnectionKeyState.Active && IsActiveExpired(g)) Terminalize(g, ConnectionKeyState.Expired);
            else if (g.State == ConnectionKeyState.Reserved && g.ReservedAt is not null && now - g.ReservedMonotonic >= AbsoluteLifetime) Terminalize(g, ConnectionKeyState.FailedClosed);
            if (g.State is not (ConnectionKeyState.Pending or ConnectionKeyState.Reserved or ConnectionKeyState.Active) && now >= g.TombstoneUntil) _grants.Remove(g.KeyId);
        }
        PruneAuthFailuresLocked();
    }
    private void PruneAuthFailuresLocked() { var cutoff = (_clock.MonotonicElapsed - FailureWindow).Ticks; while (_authFailures.Count > 0 && _authFailures.Peek() <= cutoff) _authFailures.Dequeue(); }
    private bool IsRateLimitedLocked() { PruneAuthFailuresLocked(); return _authFailures.Count >= 10; }

    private sealed class Grant(Guid issuanceId, string keyId, byte[] bearerHash, DateTimeOffset issuedUtc, TimeSpan issuedMonotonic, DateTimeOffset pendingExpiry, TimeSpan tombstoneUntil)
    {
        public Guid IssuanceId { get; } = issuanceId;
        public string KeyId { get; } = keyId;
        public byte[] BearerHash { get; } = bearerHash;
        public DateTimeOffset IssuedUtc { get; } = issuedUtc;
        public TimeSpan IssuedMonotonic { get; } = issuedMonotonic;
        public DateTimeOffset PendingExpiry { get; } = pendingExpiry;
        public ConnectionKeyState State { get; set; } = ConnectionKeyState.Pending;
        public Guid ReservationId { get; set; }
        public TimeSpan ReservedMonotonic { get; set; }
        public DateTimeOffset? ReservedAt { get; set; }
        public TimeSpan ActivatedAt { get; set; }
        public TimeSpan LastActivity { get; set; }
        public DateTimeOffset ActivatedUtc { get; set; }
        public string? SessionId { get; set; }
        public TimeSpan TombstoneUntil { get; set; } = tombstoneUntil;
        public CancellationTokenSource Lifetime { get; } = new();
    }
}

/// <summary>Instance identity carried in credentials and checked during authorization.</summary>
public sealed record ConnectionKeyIdentity(int VisualStudioPid, long VisualStudioStartTicks, int ServerPid, string ServerInstanceId, string Url)
{
    /// <inheritdoc />
    public override string ToString() => "ConnectionKeyIdentity(redacted)";
}

internal static class ConnectionKeyEnumerableExtensions
{
    public static DateTimeOffset? MinNullable(this IEnumerable<DateTimeOffset> values) { var a = values.ToArray(); return a.Length == 0 ? null : a.Min(); }
}















