using System.Diagnostics;

namespace VisualStudioMcpServer.ConnectionKeys;

/// <summary>
/// UTC wall time and monotonic elapsed time used by connection-key authorization.
/// </summary>
public interface IConnectionKeyClock
{
    /// <summary>Gets the current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
    /// <summary>Gets elapsed time from a monotonic origin.</summary>
    TimeSpan MonotonicElapsed { get; }
}

/// <summary>
/// Cryptographically secure random byte source used to issue connection credentials.
/// </summary>
public interface IConnectionKeyRandom
{
    /// <summary>Fills the destination with fresh random bytes.</summary>
    void Fill(Span<byte> destination);
}

/// <summary>
/// System clock and CSPRNG adapters for production use.
/// </summary>
public sealed class SystemConnectionKeyClock : IConnectionKeyClock, IConnectionKeyRandom
{
    private readonly long _origin = Stopwatch.GetTimestamp();
    /// <summary>Gets current UTC time.</summary>
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    /// <summary>Gets elapsed monotonic time.</summary>
    public TimeSpan MonotonicElapsed => Stopwatch.GetElapsedTime(_origin);
    /// <summary>Fills bytes from the platform CSPRNG.</summary>
    public void Fill(Span<byte> destination) => System.Security.Cryptography.RandomNumberGenerator.Fill(destination);
}
