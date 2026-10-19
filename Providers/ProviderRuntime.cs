using VisualStudioMcpServer.Observation;

namespace VisualStudioMcpServer.Providers;

/// <summary>
/// 現在の provider epoch の channel と debugger 観測 store を保持し、VSIX からの event を振り分ける。
/// channel の喪失は終端であり、store を閉じて待機を provider_disconnected で終える。
/// </summary>
internal static class ProviderRuntime
{
    private static readonly object Sync = new();
    private static ProviderChannel? _channel;
    private static DebuggerObservationStore? _store;
    private static bool _gapMarkedSinceLastPublish;

    /// <summary>execution.observed を受けたときの通知先（execution coordinator への反映）。</summary>
    internal static Action<ProviderChannel, ExecutionObservedEventBody>? ExecutionObserved { get; set; }

    /// <summary>execution 要求の遅延応答で記録が確定したときの通知先。</summary>
    internal static Action<ProviderChannel, ProviderExecutionRecord>? ExecutionSettled { get; set; }

    internal static ProviderChannel? Channel
    {
        get { lock (Sync) return _channel; }
    }

    internal static DebuggerObservationStore? ObservationStore
    {
        get { lock (Sync) return _store; }
    }

    /// <summary>ProviderClient の hook に登録する。channel の読み取り開始前に呼ばれる。</summary>
    internal static void Install()
    {
        ProviderClient.ChannelAttached = Attach;
        ProviderClient.ChannelDetached = Detach;
    }

    internal static void Attach(ProviderChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var store = new DebuggerObservationStore(channel.Identity.ProviderEpoch);
        DebuggerObservationStore? previous;
        lock (Sync)
        {
            previous = _store;
            _channel = channel;
            _store = store;
            _gapMarkedSinceLastPublish = false;
        }
        previous?.Close();
        channel.EventReceived = notification => OnEvent(channel, store, notification);
        channel.ExecutionSettled = record => ExecutionSettled?.Invoke(channel, record);
    }

    internal static void Detach(ProviderChannel channel)
    {
        DebuggerObservationStore? store = null;
        lock (Sync)
        {
            if (!ReferenceEquals(_channel, channel)) return;
            store = _store;
            _channel = null;
            _store = null;
        }
        store?.Close();
    }

    /// <summary>
    /// event を振り分ける。mode 変化は store へ公開し、providerSequence の欠落と eventsDropped は履歴 gap として記録する。
    /// </summary>
    internal static void OnEvent(ProviderChannel channel, DebuggerObservationStore store, ProviderEventNotification notification)
    {
        switch (notification.Body)
        {
            case DebuggerModeChangedEventBody mode:
            {
                bool markGap;
                lock (Sync)
                {
                    markGap = notification.HistoryGap && !_gapMarkedSinceLastPublish;
                    _gapMarkedSinceLastPublish = false;
                }
                if (markGap) store.MarkHistoryGap();
                var state = mode.State switch
                {
                    "running" => DebuggerObservedState.Running,
                    "break" => DebuggerObservedState.Break,
                    _ => DebuggerObservedState.Design
                };
                store.Publish(ProviderProtocol.IsUuidN(mode.DebugSessionId) ? mode.DebugSessionId : null,
                    ProviderProtocol.IsUuidN(mode.LaunchOperationId) ? mode.LaunchOperationId : null, state, mode.ProviderSequence);
                break;
            }
            case ProviderEventsDroppedEventBody:
                lock (Sync) _gapMarkedSinceLastPublish = true;
                store.MarkHistoryGap();
                break;
            case ExecutionObservedEventBody observed:
                ExecutionObserved?.Invoke(channel, observed);
                break;
        }
    }
}
