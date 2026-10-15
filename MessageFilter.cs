using System.Runtime.InteropServices;

namespace VisualStudioMcpServer;

/// <summary>
/// Visual StudioのCOMビジー状態に対する上限付き再試行を提供します。
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
/// COM呼び出し拒否時に最大8秒だけ再試行します。
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
    /// STAスレッドにCOMメッセージフィルターを登録します。
    /// </summary>
    public static void Register()
    {
        Marshal.ThrowExceptionForHR(CoRegisterMessageFilter(new MessageFilter(), out _));
    }

    /// <summary>
    /// 現在のSTAスレッドからCOMメッセージフィルターを解除します。
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
