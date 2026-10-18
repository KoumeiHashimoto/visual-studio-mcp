using ModelContextProtocol.Server;
using Newtonsoft.Json;
using VisualStudioMcpServer.Capabilities;
using VisualStudioMcpServer.Workspace;

/**
  呼出先IDEに関する能力情報を読み取り専用で返します
*/
namespace VisualStudioMcpServer.Tools;

/**
  MCP経由で能力情報を公開します
*/
[McpServerToolType]
public static class GetVsCapabilitiesTool
{
    /**
      現在のtransportとprovider状態を含む能力文書を返します
    */
    [McpServerTool]
    [System.ComponentModel.Description("Reports bounded Visual Studio, transport, provider, workspace, feature, permission, and limit capabilities. Unknown support remains unknown; this tool performs no IDE mutations.")]
    public static string GetVsCapabilities()
    {
        var targetPid = VisualStudioConnector.TryGetTargetProcessIdentity(out var pid, out var ticks) ? pid : (int?)null;
        var targetTicks = targetPid.HasValue ? ticks : (long?)null;
        var workspace = ActiveWorkspaceSnapshotStoreAccessor.Get()?.GetCurrent();
        var workspaceObservation = workspace is null
            ? new WorkspaceCapabilityObservation("unknown", null, null, "No workspace snapshot is available.")
            : new WorkspaceCapabilityObservation("observed", workspace.WorkspaceKind, workspace.Generation,
                workspace.WorkspaceKind == "openFolder" ? workspace.CMakeMetadataReason : null);
        var document = CapabilityRegistry.Create(CapabilityRuntimeContext.Capture(targetPid, targetTicks, workspaceObservation));
        var json = JsonConvert.SerializeObject(document, Formatting.None);
        if (json.Length <= 64 * 1024) return json;
        throw new InvalidOperationException("Capability document exceeded its response size limit.");
    }
}
