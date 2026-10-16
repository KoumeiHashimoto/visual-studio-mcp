using System.Runtime.InteropServices;

namespace VisualStudioMcpServer;

/// <summary>
/// Provides bounded retries for rejected COM calls while Visual Studio is busy.
/// </summary>
[ComImport]
[Guid("00000016-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleMessageFilter
{
    [PreserveSig]
    int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);

    [PreserveSig]
    int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);

    [PreserveSig]
    int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
}

/// <summary>
/// Retries rejected COM calls for up to eight seconds.
/// </summary>
public sealed class MessageFilter : IOleMessageFilter
{
    private const int ServerCallIsHandled = 0;
    private const int PendingMessageWaitDefault = 2;
    private const int ServerCallRetryLater = 2;
    private const int MaximumRetryDurationMilliseconds = 8000;

    [DllImport("Ole32.dll")]
    private static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);

    /// <summary>
    /// Registers the COM message filter on the current STA thread.
    /// </summary>
    public static void Register()
    {
        Marshal.ThrowExceptionForHR(CoRegisterMessageFilter(new MessageFilter(), out _));
    }

    /// <summary>
    /// Revokes the COM message filter from the current STA thread.
    /// </summary>
    public static void Revoke()
    {
        Marshal.ThrowExceptionForHR(CoRegisterMessageFilter(null, out _));
    }

    int IOleMessageFilter.HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
        => ServerCallIsHandled;

    int IOleMessageFilter.RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
        => dwRejectType == ServerCallRetryLater && dwTickCount < MaximumRetryDurationMilliseconds ? 100 : -1;

    int IOleMessageFilter.MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType)
        => PendingMessageWaitDefault;
}
