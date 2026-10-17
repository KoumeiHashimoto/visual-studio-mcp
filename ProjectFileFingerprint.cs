using System.Security.Cryptography;

namespace VisualStudioMcpServer.Tools;

/**
  プロジェクト登録解除前後の物理ファイル内容を上限付きで照合します。
*/
internal sealed record ProjectFileFingerprint(long Length, string Sha256)
{
    internal const long MaxVerifiedFileBytes = 256L * 1024 * 1024;

    internal static ProjectFileFingerprint Read(string path, DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            if (stream.Length > MaxVerifiedFileBytes)
                throw new ProjectFileFingerprintException("unsupported_item", "The physical file is too large for bounded preservation verification.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                CheckDeadline(deadline, cancellationToken);
                hash.AppendData(buffer, 0, read);
            }
            CheckDeadline(deadline, cancellationToken);
            return new ProjectFileFingerprint(stream.Length, Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch (ProjectFileFingerprintException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ProjectFileFingerprintException("unsupported_item", "The physical file could not be read for bounded preservation verification.");
        }
    }

    private static void CheckDeadline(DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("The physical file verification exceeded its deadline.");
    }
}

internal sealed class ProjectFileFingerprintException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
