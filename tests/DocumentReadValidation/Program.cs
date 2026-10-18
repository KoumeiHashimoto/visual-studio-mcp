using Newtonsoft.Json.Linq;
using VisualStudioMcpServer.Providers;
using VisualStudioMcpServer.Tools;

// P13-04（#58）document read tool の offline 検証。fake transport だけを使い、実 IDE・DTE・pipe には接続しない。
// golden 本文は親 repository の ProviderHostValidation が VSIX 側 DTO から生成する文字列と同一である。
const string ListGolden = "{\"snapshotId\":\"0123456789ab4def8123456789abcdef\",\"scope\":\"workspace\",\"total\":1,\"offset\":0,\"captureComplete\":true,\"documents\":[{\"documentId\":\"0fedcba987654321a0fedcba98765432\",\"path\":\"C:\\\\fixtures\\\\a.cs\",\"name\":\"a.cs\",\"monikerAvailability\":\"available\",\"providerKind\":\"textBuffer\",\"scopeClass\":\"workspace\",\"isDirty\":true,\"isReadOnly\":false,\"bufferGeneration\":\"7\",\"isVisibleTab\":true,\"contentType\":\"CSharp\"}],\"nextCursor\":null}";
const string ContextGolden = "{\"viewStatus\":\"lastCodeView\",\"documentId\":\"0fedcba987654321a0fedcba98765432\",\"path\":\"C:\\\\fixtures\\\\a.cs\",\"scopeClass\":\"workspace\",\"inScope\":true,\"providerKind\":\"textBuffer\",\"bufferGeneration\":\"7\",\"contentType\":\"CSharp\",\"caretLine\":2,\"caretColumn\":5,\"selectionKind\":\"single\",\"selections\":[{\"startLine\":2,\"startColumn\":1,\"endLine\":2,\"endColumn\":5,\"isReversed\":false}],\"selectionsTruncated\":false,\"selectedText\":\"a😀b\",\"selectedTextStatus\":\"included\",\"truncated\":false,\"unavailableReason\":null}";
const string ReadGolden = "{\"documentId\":\"0fedcba987654321a0fedcba98765432\",\"path\":\"C:\\\\fixtures\\\\a.cs\",\"contentSource\":\"editorBuffer\",\"providerKind\":\"textBuffer\",\"isDirty\":true,\"isReadOnly\":false,\"bufferVersion\":\"7\",\"contentType\":\"CSharp\",\"totalLines\":3,\"totalLength\":12,\"startLine\":1,\"returnedLines\":2,\"endLine\":3,\"endColumn\":1,\"text\":\"one\\r\\ntwo\\r\\n\",\"truncated\":false}";
const string DocumentId = "0fedcba987654321a0fedcba98765432";

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

JObject Parse(string json) => JObject.Parse(json);

// ---- provider がない場合 ----
ProviderReadGateway.Transport = null;
foreach (var (name, call) in new (string, Func<Task<string>>)[]
{
    ("list_open_documents", () => DocumentReadTools.ListOpenDocuments()),
    ("get_editor_context", () => DocumentReadTools.GetEditorContext()),
    ("read_document", () => DocumentReadTools.ReadDocument(DocumentId))
})
{
    var missing = Parse(await call());
    Check(missing.Value<bool>("Success") == false && missing.Value<string>("Code") == "requires_vsix_provider" &&
        missing.Value<string>("DispatchState") == "notDispatched" && missing.Value<string>("ProviderReason") == "provider_not_configured" &&
        !string.IsNullOrWhiteSpace(missing.Value<string>("Message")), $"{name} without a provider returns requires_vsix_provider without dispatch");
}

var fake = new FakeTransport();
ProviderReadGateway.Transport = fake;

// ---- 送信前の引数検査（dispatch 0）----
foreach (var (code, call) in new (string, Func<Task<string>>)[]
{
    ("invalid_scope", () => DocumentReadTools.ListOpenDocuments("solution")),
    ("invalid_page_size", () => DocumentReadTools.ListOpenDocuments(pageSize: 0)),
    ("invalid_page_size", () => DocumentReadTools.ListOpenDocuments(pageSize: 101)),
    ("invalid_cursor", () => DocumentReadTools.ListOpenDocuments(cursor: new string('c', 257))),
    ("invalid_max_characters", () => DocumentReadTools.GetEditorContext(true, 4097)),
    ("invalid_max_characters", () => DocumentReadTools.GetEditorContext(true, -1)),
    ("invalid_document_id", () => DocumentReadTools.ReadDocument("C:\\fixtures\\a.cs")),
    ("invalid_document_id", () => DocumentReadTools.ReadDocument("")),
    ("invalid_buffer_version", () => DocumentReadTools.ReadDocument(DocumentId, "7 8")),
    ("invalid_start_line", () => DocumentReadTools.ReadDocument(DocumentId, startLine: 0)),
    ("invalid_line_count", () => DocumentReadTools.ReadDocument(DocumentId, lineCount: 501)),
    ("invalid_max_characters", () => DocumentReadTools.ReadDocument(DocumentId, maxCharacters: 32769))
})
{
    Check(Parse(await call()).Value<string>("Code") == code && fake.Calls.Count == 0, $"{code} is rejected before provider dispatch");
}

// ---- 成功応答と要求本文 ----
fake.Next = Ok(ListGolden);
var list = Parse(await DocumentReadTools.ListOpenDocuments(pageSize: 10));
var listRequest = fake.Calls.Single();
Check(listRequest.Operation == "documents.list" && listRequest.WorkspacePath is null &&
    ProviderBodyRules.TryValidateRequest(listRequest.Operation, listRequest.Body, out var listTarget) &&
    listTarget!.VisualStudioPid == FakeTransport.Pid && listRequest.Body.Contains("\"scope\":\"workspace\"") &&
    listRequest.Body.Contains("\"pageSize\":10"), "list request carries the bound target, scope and page size within the catalog rules");
Check(list.Value<bool>("Success") && list.Value<string>("Provider") == "visualStudioProvider" &&
    list.Value<string>("ProviderEpoch") == FakeTransport.Epoch && list.Value<int>("Returned") == 1 &&
    list["Documents"]![0]!.Value<string>("DocumentId") == DocumentId && list["Documents"]![0]!.Value<bool>("IsDirty") &&
    list["Documents"]![0]!.Value<string>("ScopeClass") == "workspace" && list["NextCursor"]!.Type == JTokenType.Null,
    "list result maps the provider snapshot into PascalCase fields");

fake.Next = Ok(ContextGolden);
var context = Parse(await DocumentReadTools.GetEditorContext(true, 16));
Check(context.Value<bool>("Success") && context.Value<string>("ViewStatus") == "lastCodeView" &&
    context.Value<int>("CaretLine") == 2 && context.Value<int>("CaretColumn") == 5 &&
    context["Selections"]![0]!.Value<int>("EndColumn") == 5 && context.Value<string>("SelectedText") == "a😀b" &&
    fake.Calls[^1].Body.Contains("\"includeSelectedText\":true") && fake.Calls[^1].Body.Contains("\"maxCharacters\":16"),
    "editor context keeps one-based UTF-16 positions and the opt-in selected text");

fake.Next = Ok(ReadGolden);
var read = Parse(await DocumentReadTools.ReadDocument(DocumentId, "7", 1, 2, 100));
Check(read.Value<bool>("Success") && read.Value<string>("ContentSource") == "editorBuffer" &&
    read.Value<string>("Text") == "one\r\ntwo\r\n" && read.Value<int>("EndLine") == 3 && read.Value<int>("EndColumn") == 1 &&
    read.Value<string>("BufferVersion") == "7" && fake.Calls[^1].Body.Contains("\"expectedBufferVersion\":\"7\""),
    "read result returns the editor-buffer range with CRLF preserved and an end-exclusive position");

// ---- 契約外の応答 ----
fake.Next = Ok(ListGolden.Replace("\"nextCursor\":null", "\"nextCursor\":null,\"extra\":1"));
Check(Parse(await DocumentReadTools.ListOpenDocuments()).Value<string>("Code") == "provider_response_invalid",
    "unknown response member is rejected as provider_response_invalid");
fake.Next = Ok(ListGolden.Replace("\"scope\":\"workspace\"", "\"scope\":\"all\""));
Check(Parse(await DocumentReadTools.ListOpenDocuments()).Value<string>("Code") == "provider_response_invalid",
    "response for a different scope is rejected");
fake.Next = Ok(ContextGolden);
Check(Parse(await DocumentReadTools.GetEditorContext(false)).Value<string>("Code") == "provider_response_invalid",
    "selected text returned without opt-in is rejected");
fake.Next = Ok(ContextGolden);
Check(Parse(await DocumentReadTools.GetEditorContext(true, 2)).Value<string>("Code") == "provider_response_invalid",
    "selected text above the requested bound is rejected");
fake.Next = Ok(ReadGolden);
Check(Parse(await DocumentReadTools.ReadDocument(DocumentId, "8", 1, 2, 100)).Value<string>("Code") == "provider_response_invalid",
    "a response for another buffer version is never presented as the expected version");
fake.Next = Ok(ReadGolden);
Check(Parse(await DocumentReadTools.ReadDocument(DocumentId, null, 1, 2, 5)).Value<string>("Code") == "provider_response_invalid",
    "a response above maxCharacters is rejected");
fake.Next = Ok(ReadGolden.Replace("\"contentSource\":\"editorBuffer\"", "\"contentSource\":\"disk\""));
Check(Parse(await DocumentReadTools.ReadDocument(DocumentId)).Value<string>("Code") == "provider_response_invalid",
    "disk content is never accepted as editor-buffer content");
fake.Next = Ok(ListGolden.Replace("{\"snapshotId\"", "{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":1}}}}},\"snapshotId\""));
Check(Parse(await DocumentReadTools.ListOpenDocuments()).Value<string>("Code") == "provider_response_invalid",
    "a response deeper than the body depth limit is rejected");

// ---- provider error の写像 ----
fake.Next = new ProviderReadOutcome("error", "document_changed", "The buffer changed.", null, "dispatched", FakeTransport.Epoch, null);
var changed = Parse(await DocumentReadTools.ReadDocument(DocumentId, "7"));
Check(changed.Value<string>("Code") == "document_changed" && changed.Value<string>("DispatchState") == "dispatched",
    "document_changed is returned as a structured result instead of mixed revisions");
fake.Next = new ProviderReadOutcome("error", "stale_cursor", "Capture a new snapshot.", null, "dispatched", FakeTransport.Epoch, null);
Check(Parse(await DocumentReadTools.ListOpenDocuments(cursor: "dl1.x")).Value<string>("Code") == "stale_cursor",
    "stale cursor is returned as a structured result");
fake.Next = ProviderReadGateway.Unavailable("operation_not_implemented", FakeTransport.Epoch);
var notImplemented = Parse(await DocumentReadTools.ListOpenDocuments());
Check(notImplemented.Value<string>("Code") == "requires_vsix_provider" &&
    notImplemented.Value<string>("ProviderReason") == "operation_not_implemented", "unimplemented provider operation is requires_vsix_provider");
foreach (var code in new[] { "provider_busy", "provider_timeout", "provider_disconnected", "ui_thread_timeout", "target_mismatch", "unsupported" })
{
    fake.Next = new ProviderReadOutcome("error", code, "bounded", null, "notDispatched", FakeTransport.Epoch, null);
    var mapped = Parse(await DocumentReadTools.GetEditorContext());
    Check(mapped.Value<bool>("Success") == false && mapped.Value<string>("Code") == code, $"{code} stays a structured tool result");
}
fake.Throw = true;
var thrown = Parse(await DocumentReadTools.GetEditorContext());
Check(thrown.Value<string>("Code") == "internal_error" && thrown.Value<string>("DispatchState") == "unknown",
    "an unexpected transport failure is internal_error with unknown dispatch state");
fake.Throw = false;

// ---- 応答上限：JSON を切らずにエラーにする ----
var escapeHeavy = new string('\u0001', 32768);
fake.Next = Ok(ReadGolden.Replace("\"text\":\"one\\r\\ntwo\\r\\n\"", "\"text\":" + System.Text.Json.JsonSerializer.Serialize(escapeHeavy)));
var tooLarge = await DocumentReadTools.ReadDocument(DocumentId, null, 1, 2, 32768);
Check(tooLarge.Length <= ProviderToolResults.MaximumResponseCharacters && Parse(tooLarge).Value<string>("Code") == "response_too_large",
    "an over-limit response becomes response_too_large and remains valid JSON");

Console.WriteLine($"Passed {passed} document read checks. No IDE, DTE, or pipe was used.");

static ProviderReadOutcome Ok(string body) => new("ok", null, null, body, "dispatched", FakeTransport.Epoch, null);

sealed class FakeTransport : IProviderReadTransport
{
    internal const int Pid = 4242;
    internal const string Epoch = "99999999999949998999999999999999";
    internal List<(string Operation, string Body, string? WorkspacePath)> Calls { get; } = new();
    internal ProviderReadOutcome? Next { get; set; }
    internal bool Throw { get; set; }

    public Task<ProviderReadOutcome> SendAsync(string operation, Func<ProviderTarget, object> bodyFactory,
        string? workspacePath, TimeSpan timeout)
    {
        if (Throw) throw new IOException("PRIVATE_SENTINEL");
        var body = ProviderProtocol.SerializeBody(bodyFactory(new ProviderTarget
        {
            VisualStudioPid = Pid,
            VisualStudioStartTicks = 638900000000000000,
            WorkspacePath = workspacePath
        }));
        Calls.Add((operation, body, workspacePath));
        var next = Next ?? throw new InvalidOperationException("No fake response configured.");
        Next = null;
        return Task.FromResult(next);
    }
}
