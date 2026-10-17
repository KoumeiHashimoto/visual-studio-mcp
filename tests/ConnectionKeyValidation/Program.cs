using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using VisualStudioMcpServer.ConnectionKeys;

var tests = new (string Name, Action Run)[]
{
    ("codec round trip and bearer", CodecRoundTrip),
    ("codec rejects invalid documents", CodecRejects),
    ("pending expiry and single-use state", StateTransitions),
    ("reissue preserves active sessions", Reissue),
    ("capacity, auth rate and tombstones", Limits),
    ("all eight live slots are preserved", Capacity),
    ("UTC adjustments do not extend monotonic expiry", ClockBehavior),
    ("concurrent reservation has one winner", ReservationRace),
    ("credentials are not retained or formatted", Redaction),
    ("revocation cancels linked request tokens", RevocationCancellation),
    ("HTTP key admission binds a stateful session", HttpAdmission),
    ("copied key envelope initializes and continues a bound session", HttpCopiedEnvelope),
    ("copied key envelope metadata and expiry are bound to the grant", HttpEnvelopeTampering),
    ("copied key envelope remains valid for active session after pending expiry", HttpEnvelopeActiveLifetime),
    ("copied key envelope observes active idle and absolute expiry", HttpEnvelopeActiveExpiry),
    ("authorization bounds admit only bounded ASCII envelopes", AuthorizationHeaderBounds),
    ("malformed copied envelopes reach the key-auth rate limit", HttpEnvelopeRateLimit),
    ("HTTP replay and wrong credentials execute no tools", HttpRejectsReplay),
    ("owner credential cannot select a keyed session", HttpCredentialIsolation),
    ("workspace snapshot route remains owner-only", HttpWorkspaceSnapshotRoute),
    ("DELETE revokes admission before SDK dispatch", HttpDeleteClosesFirst),
    ("newer protocol probe does not consume the pending key", HttpProtocolProbe)
};
foreach (var test in tests) { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
Console.WriteLine($"PASS {tests.Length} connection-key checks");

static void CodecRoundTrip()
{
    var p = Payload(); var key = ConnectionKeyCodec.Encode(p);
    Assert(ConnectionKeyCodec.TryDecode(" \r\n" + key + "\t", out var decoded), "decode");
    Assert(decoded == p, "payload equality");
    Assert(ConnectionKeyCodec.TryParseBearer(p.ToBearerValue(), out var id, out var token, out var otp), "bearer parse");
    Assert((id, token, otp) == (p.KeyId, p.Token, p.Otp), "bearer parts");
}
static void CodecRejects()
{
    var key = ConnectionKeyCodec.Encode(Payload());
    foreach (var invalid in new[] { key + "x", key.Replace("VSDMCP-KEY1:", "VSDMCP-KEY2:"), "VSDMCP-KEY1:====", "VSDMCP-KEY1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")), "VSDMCP-KEY1:" + B64("{\"v\":1,\"v\":1}"), "VSDMCP-KEY1:" + B64(JsonWith("\"unknown\":1")), "VSDMCP-KEY1:" + B64(JsonWith("\"token\":4")), "VSDMCP-KEY1:" + B64(JsonWith("\"url\":\"http://localhost:1234/mcp\"")), "VSDMCP-KEY1:" + B64(JsonWith("\"visualStudioStartTicks\":\"9223372036854775808\"")), "VSDMCP-KEY1:" + B64(JsonWith("\"v\":2")), "VSDMCP-KEY1:" + B64(JsonWith("\"keyId\":\"short\"")), "VSDMCP-KEY1:" + B64(JsonWith("\"expiresAt\":\"1900000000\"")), "VSDMCP-KEY1:" + B64(JsonWith("\"visualStudioPid\":1.5")), "VSDMCP-KEY1:" + B64(JsonWith("\"url\":\"http://127.0.0.1:1234/mcp?x=1\"")) })
        Assert(!ConnectionKeyCodec.TryDecode(invalid, out _), "invalid key rejected");
    Assert(!ConnectionKeyCodec.TryParseBearer("vsc1." + new string('A',22) + ".x.y", out _, out _, out _), "malformed bearer");
}
static string JsonWith(string replacement) { var p = Payload(); var json = "{\"v\":1,\"keyId\":\""+p.KeyId+"\",\"visualStudioPid\":10,\"visualStudioStartTicks\":\"638900000000000000\",\"serverPid\":11,\"serverInstanceId\":\""+p.ServerInstanceId+"\",\"url\":\""+p.Url+"\",\"token\":\""+p.Token+"\",\"otp\":\""+p.Otp+"\",\"expiresAt\":1900000000}"; var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject(); using var patch = System.Text.Json.JsonDocument.Parse("{"+replacement+"}"); foreach (var field in patch.RootElement.EnumerateObject()) node[field.Name] = System.Text.Json.Nodes.JsonNode.Parse(field.Value.GetRawText()); return node.ToJsonString(); }
static string B64(string text) => ConnectionKeyCodec.Base64UrlEncode(Encoding.UTF8.GetBytes(text));
static void StateTransitions()
{
    var f = new Fixture(); var issued = f.Store.Issue(); Assert(issued.Result == ConnectionKeyResult.Success, "issue");
    Assert(issued.ExpiresAt == f.Clock.UtcNow.AddSeconds(300), "pending expiry is 300s after issue");
    Assert(ConnectionKeyCodec.TryDecode(issued.Key, out var key), "decode issued");
    var reservation = f.Store.Reserve(key!.ToBearerValue(), f.Store.Identity); Assert(reservation.Result == ConnectionKeyResult.Success, "reserve");
    Assert(f.Store.FailClosed(reservation.ReservationId) == ConnectionKeyResult.Success, "fail closed");
    Assert(f.Store.Reserve(key.ToBearerValue(), f.Store.Identity).Result == ConnectionKeyResult.AlreadyUsed, "failed init stays closed");
    var withinWindow = new Fixture(); var valid = withinWindow.Store.Issue(); ConnectionKeyCodec.TryDecode(valid.Key, out var validPayload);
    withinWindow.Clock.Advance(TimeSpan.FromSeconds(299));
    Assert(withinWindow.Store.Reserve(validPayload!.ToBearerValue(), withinWindow.Store.Identity).Result == ConnectionKeyResult.Success, "usable at 299s");
    var atDeadline = new Fixture(); var expiring = atDeadline.Store.Issue(); ConnectionKeyCodec.TryDecode(expiring.Key, out var expPayload);
    atDeadline.Clock.Advance(TimeSpan.FromSeconds(300));
    Assert(atDeadline.Store.Reserve(expPayload!.ToBearerValue(), atDeadline.Store.Identity).Result == ConnectionKeyResult.Expired, "expires at 300s");
}
static void Reissue()
{
    var f = new Fixture(); var first = f.Store.Issue(); ConnectionKeyCodec.TryDecode(first.Key, out var p1);
    var r = f.Store.Reserve(p1!.ToBearerValue(), f.Store.Identity); Assert(f.Store.Activate(r.ReservationId, "sdk-session-a") == ConnectionKeyResult.Success, "activate");
    var second = f.Store.Issue(); Assert(second.Result == ConnectionKeyResult.Success, "reissue");
    Assert(f.Store.AuthenticateSession(p1.ToBearerValue(), "sdk-session-a", f.Store.Identity) == ConnectionKeyResult.Success, "active remains");
    ConnectionKeyCodec.TryDecode(first.Key, out var oldPending);
    Assert(f.Store.Reserve(oldPending!.ToBearerValue(), f.Store.Identity).Result == ConnectionKeyResult.AlreadyUsed, "active cannot reserve again");
}
static void Limits()
{
    var f = new Fixture(); var pending = f.Store.Issue(); ConnectionKeyCodec.TryDecode(pending.Key, out var p);
    for (var i=0;i<10;i++) Assert(f.Store.Reserve("bad-bearer", f.Store.Identity).Result == ConnectionKeyResult.Invalid, "failed auth counted");
    Assert(f.Store.IsKeyAuthRateLimited(), "10 per minute");
    Assert(f.Store.Reserve(p!.ToBearerValue(), f.Store.Identity).Result == ConnectionKeyResult.RateLimited, "rate limit blocks without consuming pending key");
    f.Clock.Advance(TimeSpan.FromMinutes(1)); Assert(!f.Store.IsKeyAuthRateLimited(), "rate window expires");
    Assert(f.Store.Reserve(p.ToBearerValue(), f.Store.Identity).Result == ConnectionKeyResult.Success, "pending key survives rate limit");

    var scoped = new Fixture(); var firstIssue = scoped.Store.Issue(Guid.NewGuid()); var secondIssue = scoped.Store.Issue(Guid.NewGuid());
    Assert(scoped.Store.CancelPending(firstIssue.IssuanceId) == ConnectionKeyResult.NotFound, "old issuance cannot cancel reissue");
    Assert(scoped.Store.CancelPending(secondIssue.IssuanceId) == ConnectionKeyResult.Success, "current issuance can be canceled");

    var tombstones = new Fixture();
    for (var i=0;i<65;i++) Assert(tombstones.Store.Issue().Result == ConnectionKeyResult.Success, "issue within bounded tombstones");
    Assert(tombstones.Store.Issue().Result == ConnectionKeyResult.CapacityExceeded, "tombstones are bounded");
    tombstones.Clock.Advance(TimeSpan.FromSeconds(240));
    Assert(tombstones.Store.Issue().Result == ConnectionKeyResult.Success, "expired tombstones swept");
}
static void Capacity()
{
    var f = new Fixture();
    for (var i=0;i<8;i++)
    {
        var issued=f.Store.Issue(); Assert(issued.Result==ConnectionKeyResult.Success,"issue capacity grant");
        ConnectionKeyCodec.TryDecode(issued.Key,out var p); var reservation=f.Store.Reserve(p!.ToBearerValue(),f.Store.Identity);
        Assert(f.Store.Activate(reservation.ReservationId,"session-"+i)==ConnectionKeyResult.Success,"activate capacity grant");
    }
    Assert(f.Store.Issue().Result==ConnectionKeyResult.CapacityExceeded,"ninth grant refused");
    Assert(f.Store.GetStatus().ActiveCount==8,"no active eviction");
}
static void ClockBehavior()
{
    var f=new Fixture(); var issued=f.Store.Issue(); ConnectionKeyCodec.TryDecode(issued.Key,out var p);
    f.Clock.UtcNow=f.Clock.UtcNow.AddHours(-2);
    Assert(f.Store.Reserve(p!.ToBearerValue(),f.Store.Identity).Result==ConnectionKeyResult.Success,"wall-clock change does not expire early");
}
static void ReservationRace()
{
    var f = new Fixture(); var keyResult=f.Store.Issue(); ConnectionKeyCodec.TryDecode(keyResult.Key, out var payload);
    var winners = new ConcurrentBag<ConnectionKeyReservation>();
    Parallel.For(0,64,_ => winners.Add(f.Store.Reserve(payload!.ToBearerValue(), f.Store.Identity)));
    Assert(winners.Count(x=>x.Result==ConnectionKeyResult.Success)==1, "one reservation winner");
}
static void Redaction()
{
    var f = new Fixture(); var issued = f.Store.Issue(); ConnectionKeyCodec.TryDecode(issued.Key,out var decoded); var p = decoded ?? throw new InvalidOperationException("issued key decode failed");
    var storeText = f.Store.ToString() ?? string.Empty;
    Assert(!storeText.Contains(p.Token, StringComparison.Ordinal) && !storeText.Contains(p.Otp,StringComparison.Ordinal), "store redacted");
    Assert(!p.ToString().Contains(p.Token,StringComparison.Ordinal) && !p.ToString().Contains(p.Otp,StringComparison.Ordinal), "payload redacted");
    Assert(!issued.ToString().Contains(p.Token,StringComparison.Ordinal) && !issued.ToString().Contains(p.Otp,StringComparison.Ordinal), "issuance result redacted");
    var reservation=f.Store.Reserve(p.ToBearerValue(),f.Store.Identity);
    Assert(!reservation.ToString().Contains(p.Token,StringComparison.Ordinal) && !reservation.ToString().Contains(p.Otp,StringComparison.Ordinal), "result redacted");
}
static void RevocationCancellation()
{
    var f=new Fixture(); var issued=f.Store.Issue(); ConnectionKeyCodec.TryDecode(issued.Key,out var p);
    var reserved=f.Store.Reserve(p!.ToBearerValue(),f.Store.Identity);
    Assert(f.Store.TryGetReservationCancellationToken(reserved.ReservationId,out var initToken),"reservation lifetime token");
    Assert(f.Store.Activate(reserved.ReservationId,"cancel-session")==ConnectionKeyResult.Success,"activate cancellation grant");
    Assert(f.Store.TryGetSessionCancellationToken("cancel-session",out var sessionToken),"session lifetime token");
    f.Store.RevokeAll();
    Assert(SpinWait.SpinUntil(()=>initToken.IsCancellationRequested && sessionToken.IsCancellationRequested,TimeSpan.FromSeconds(1)),"revocation signals linked tokens");
}
static void HttpAdmission()
{
    var f = new Fixture(); var issued = f.Store.Issue(); ConnectionKeyCodec.TryDecode(issued.Key, out var p);
    var context = HttpRequest("POST", p!.ToBearerValue(), "initialize", null);
    var called = false;
    ConnectionKeyHttpAuthorization.InvokeAsync(context, f.Store, _ => false, async ctx =>
    {
        called = true; ctx.Response.Headers["Mcp-Session-Id"] = "sdk-session"; ctx.Response.StatusCode = 200;
        await ctx.Response.WriteAsync("{}");
    }).GetAwaiter().GetResult();
    Assert(called, "initialize forwarded to SDK");
    var authenticated = f.Store.AuthenticateSession(p.ToBearerValue(), "sdk-session", f.Store.Identity);
    Assert(authenticated == ConnectionKeyResult.Success, $"session activated before response flush ({authenticated}; {f.Store.GetStatus().ActiveCount}; {context.Response.Headers["Mcp-Session-Id"]})");
}
static void HttpCopiedEnvelope()
{
    var f = new Fixture(); var issued = f.Store.Issue(); ConnectionKeyCodec.TryDecode(issued.Key, out var p);
    var calls = 0;
    var initial = HttpRequest("POST", issued.Key!, "initialize", null);
    ConnectionKeyHttpAuthorization.InvokeAsync(initial, f.Store, _ => false, async ctx =>
    { calls++; ctx.Response.Headers["Mcp-Session-Id"] = "copied-session"; await ctx.Response.WriteAsync("{}"); }).GetAwaiter().GetResult();
    Assert(calls == 1 && initial.Response.StatusCode == 200, "copied envelope initializes");
    var continuation = HttpRequest("POST", issued.Key!, "tools/list", "copied-session");
    ConnectionKeyHttpAuthorization.InvokeAsync(continuation, f.Store, _ => false, _ => { calls++; return Task.CompletedTask; }).GetAwaiter().GetResult();
    Assert(calls == 2 && continuation.Response.StatusCode == 200, "copied envelope continues bound session");

    var compound = HttpRequest("POST", p!.ToBearerValue(), "tools/list", "copied-session");
    ConnectionKeyHttpAuthorization.InvokeAsync(compound, f.Store, _ => false, _ => { calls++; return Task.CompletedTask; }).GetAwaiter().GetResult();
    Assert(calls == 3 && compound.Response.StatusCode == 200, "compound bearer remains compatible for same grant");

    var replay = HttpRequest("POST", issued.Key!, "initialize", null);
    ConnectionKeyHttpAuthorization.InvokeAsync(replay, f.Store, _ => false, _ => { calls++; return Task.CompletedTask; }).GetAwaiter().GetResult();
    Assert(replay.Response.StatusCode == 401 && calls == 3, "copied envelope replay rejected");
}
static void HttpEnvelopeTampering()
{
    var f = new Fixture(); var issued = f.Store.Issue(); ConnectionKeyCodec.TryDecode(issued.Key, out var p);
    var changed = new[]
    {
        p! with { VisualStudioPid = p.VisualStudioPid + 1 },
        p! with { VisualStudioStartTicks = p.VisualStudioStartTicks + 1 },
        p! with { ServerPid = p.ServerPid + 1 },
        p! with { ServerInstanceId = ConnectionKeyCodec.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)) },
        p! with { Url = "http://127.0.0.1:4321/mcp" },
        p! with { ExpiresAt = p.ExpiresAt + 1 },
        p! with { Token = ConnectionKeyCodec.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)) }
    };
    foreach (var tampered in changed)
    {
        var request = HttpRequest("POST", ConnectionKeyCodec.Encode(tampered), "initialize", null);
        var called = false;
        ConnectionKeyHttpAuthorization.InvokeAsync(request, f.Store, _ => false, _ => { called = true; return Task.CompletedTask; }).GetAwaiter().GetResult();
        Assert(request.Response.StatusCode is 401 or 403 && !called, "tampered envelope rejected before SDK");
    }
}
static void HttpEnvelopeActiveLifetime()
{
    var f = new Fixture(); var issued = f.Store.Issue(); ConnectionKeyCodec.TryDecode(issued.Key, out var p);
    var initialize = HttpRequest("POST", issued.Key!, "initialize", null);
    ConnectionKeyHttpAuthorization.InvokeAsync(initialize, f.Store, _ => false, async ctx =>
    { ctx.Response.Headers["Mcp-Session-Id"] = "long-session"; await ctx.Response.WriteAsync("{}"); }).GetAwaiter().GetResult();
    f.Clock.Advance(TimeSpan.FromSeconds(121));
    var continuation = HttpRequest("POST", issued.Key!, "tools/list", "long-session"); var called = false;
    ConnectionKeyHttpAuthorization.InvokeAsync(continuation, f.Store, _ => false, _ => { called = true; return Task.CompletedTask; }).GetAwaiter().GetResult();
    Assert(called && continuation.Response.StatusCode == 200, "active envelope not cut off by pending wall-clock expiry");
}
static void HttpEnvelopeActiveExpiry()
{
    static (Fixture Fixture, string Key) StartSession(string sessionId)
    {
        var f = new Fixture(); var issued = f.Store.Issue();
        var initialize = HttpRequest("POST", issued.Key!, "initialize", null);
        ConnectionKeyHttpAuthorization.InvokeAsync(initialize, f.Store, _ => false, async ctx =>
        { ctx.Response.Headers["Mcp-Session-Id"] = sessionId; await ctx.Response.WriteAsync("{}"); }).GetAwaiter().GetResult();
        return (f, issued.Key!);
    }

    var idle = StartSession("idle-session");
    idle.Fixture.Clock.Advance(TimeSpan.FromMinutes(30));
    var idleRequest = HttpRequest("POST", idle.Key, "tools/list", "idle-session"); var idleCalled = false;
    ConnectionKeyHttpAuthorization.InvokeAsync(idleRequest, idle.Fixture.Store, _ => false,
        _ => { idleCalled = true; return Task.CompletedTask; }).GetAwaiter().GetResult();
    Assert(!idleCalled && idleRequest.Response.StatusCode == 401, "active envelope expires at idle limit");

    var absolute = StartSession("absolute-session");
    var elapsed = TimeSpan.Zero;
    var beforeAbsoluteLimit = TimeSpan.FromHours(7) + TimeSpan.FromMinutes(59);
    while (elapsed < beforeAbsoluteLimit)
    {
        var step = TimeSpan.FromMinutes(29);
        if (step > beforeAbsoluteLimit - elapsed) step = beforeAbsoluteLimit - elapsed;
        absolute.Fixture.Clock.Advance(step);
        elapsed += step;
        var refresh = HttpRequest("POST", absolute.Key, "tools/list", "absolute-session"); var refreshCalled = false;
        ConnectionKeyHttpAuthorization.InvokeAsync(refresh, absolute.Fixture.Store, _ => false,
            _ => { refreshCalled = true; return Task.CompletedTask; }).GetAwaiter().GetResult();
        Assert(refreshCalled && refresh.Response.StatusCode == 200, "active session continues before absolute limit");
    }
    absolute.Fixture.Clock.Advance(TimeSpan.FromMinutes(1));
    var absoluteRequest = HttpRequest("POST", absolute.Key, "tools/list", "absolute-session"); var absoluteCalled = false;
    ConnectionKeyHttpAuthorization.InvokeAsync(absoluteRequest, absolute.Fixture.Store, _ => false,
        _ => { absoluteCalled = true; return Task.CompletedTask; }).GetAwaiter().GetResult();
    Assert(!absoluteCalled && absoluteRequest.Response.StatusCode == 401, "active envelope expires at absolute limit");
}
static void HttpEnvelopeRateLimit()
{
    var f = new Fixture(); var calls = 0;
    for (var i = 0; i < 10; i++)
    {
        var request = HttpRequest("POST", "VSDMCP-KEY1:malformed", "initialize", null);
        ConnectionKeyHttpAuthorization.InvokeAsync(request, f.Store, _ => false,
            _ => { calls++; return Task.CompletedTask; }).GetAwaiter().GetResult();
        if (i < 9) Assert(request.Response.StatusCode == 401, "malformed envelope rejected before rate limit");
        else Assert(request.Response.StatusCode == 429 && request.Response.Headers.RetryAfter == "5", "malformed envelope receives rate-limit response");
    }
    var limited = HttpRequest("POST", "VSDMCP-KEY1:malformed", "initialize", null);
    ConnectionKeyHttpAuthorization.InvokeAsync(limited, f.Store, _ => false,
        _ => { calls++; return Task.CompletedTask; }).GetAwaiter().GetResult();
    Assert(limited.Response.StatusCode == 429 && limited.Response.Headers.RetryAfter == "5" && calls == 0,
        "rate limit checked before parsing subsequent malformed envelope");
}
static void AuthorizationHeaderBounds()
{
    Assert(ConnectionKeyHttpAuthorization.IsAuthorizationHeaderAllowed("Bearer vsc1.short.token.otp", 1), "short legacy header admitted to auth parser");
    Assert(!ConnectionKeyHttpAuthorization.IsAuthorizationHeaderAllowed("Bearer VSDMCP-KEY1:" + new string('A', 8193), 1), "oversized envelope rejected");
    Assert(!ConnectionKeyHttpAuthorization.IsAuthorizationHeaderAllowed("Bearer VSDMCP-KEY1:" + "abc\u0100", 1), "non-ASCII header rejected");
    Assert(!ConnectionKeyHttpAuthorization.IsAuthorizationHeaderAllowed("Bearer VSDMCP-KEY1:abc", 2), "duplicate headers rejected");
    Assert(!ConnectionKeyHttpAuthorization.IsAuthorizationHeaderAllowed("Bearer " + new string('x', 257), 1), "oversized short credential rejected");
}
static void HttpRejectsReplay()
{
    var f = new Fixture(); var issued=f.Store.Issue(); ConnectionKeyCodec.TryDecode(issued.Key,out var p);
    var initial = HttpRequest("POST", p!.ToBearerValue(), "initialize", null);
    ConnectionKeyHttpAuthorization.InvokeAsync(initial, f.Store, _ => false, async ctx =>
    { ctx.Response.Headers["Mcp-Session-Id"]="session"; await ctx.Response.WriteAsync("{}"); }).GetAwaiter().GetResult();
    var calls = 0;
    var replay=HttpRequest("POST",p.ToBearerValue(),"initialize",null);
    ConnectionKeyHttpAuthorization.InvokeAsync(replay,f.Store,_=>false,_=>{calls++;return Task.CompletedTask;}).GetAwaiter().GetResult();
    Assert(replay.Response.StatusCode==401 && calls==0,"replay fails before SDK");
    var wrong=HttpRequest("POST",p.ToBearerValue()+"x","tools/list","session");
    ConnectionKeyHttpAuthorization.InvokeAsync(wrong,f.Store,_=>false,_=>{calls++;return Task.CompletedTask;}).GetAwaiter().GetResult();
    Assert(wrong.Response.StatusCode==401 && calls==0,"wrong bearer fails before tools");
}
static void HttpCredentialIsolation()
{
    var f=new Fixture(); var issued=f.Store.Issue(); ConnectionKeyCodec.TryDecode(issued.Key,out var p);
    var reservation=f.Store.Reserve(p!.ToBearerValue(),f.Store.Identity); f.Store.Activate(reservation.ReservationId,"key-session");
    var owner=HttpRequest("POST","trusted-owner","tools/list","key-session"); var called=false;
    ConnectionKeyHttpAuthorization.InvokeAsync(owner,f.Store,x=>x=="trusted-owner",_=>{called=true;return Task.CompletedTask;}).GetAwaiter().GetResult();
    Assert(owner.Response.StatusCode==401 && !called,"owner credential rejected for keyed SDK session");
    var keyOnOwner=HttpRequest("POST",p.ToBearerValue(),"issue","key-session");
    keyOnOwner.Request.Path="/_vsix/connection-key/status";
    ConnectionKeyHttpAuthorization.InvokeAsync(keyOnOwner,f.Store,_=>false,_=>{called=true;return Task.CompletedTask;}).GetAwaiter().GetResult();
    Assert(keyOnOwner.Response.StatusCode==401 && !called,"compound credential rejected on owner route");
    var envelopeOnOwner=HttpRequest("POST",issued.Key!,"issue",null);
    envelopeOnOwner.Request.Path="/_vsix/connection-key/status";
    ConnectionKeyHttpAuthorization.InvokeAsync(envelopeOnOwner,f.Store,_=>false,_=>{called=true;return Task.CompletedTask;}).GetAwaiter().GetResult();
    Assert(envelopeOnOwner.Response.StatusCode==401 && !called,"copied envelope rejected on owner route");
}
static void HttpDeleteClosesFirst()
{
    var f=new Fixture(); var issued=f.Store.Issue(); ConnectionKeyCodec.TryDecode(issued.Key,out var p);
    var reservation=f.Store.Reserve(p!.ToBearerValue(),f.Store.Identity); f.Store.Activate(reservation.ReservationId,"delete-session");
    var context=HttpRequest("DELETE",p.ToBearerValue(),null,"delete-session"); var sawClosed=false;
    ConnectionKeyHttpAuthorization.InvokeAsync(context,f.Store,_=>false,_=>
    { sawClosed=f.Store.AuthenticateSession(p.ToBearerValue(),"delete-session",f.Store.Identity)!=ConnectionKeyResult.Success; return Task.CompletedTask; }).GetAwaiter().GetResult();
    Assert(sawClosed,"further admission closed before SDK DELETE");
}
static void HttpWorkspaceSnapshotRoute()
{
    var f = new Fixture();
    var owner = HttpRequest("POST", "trusted-owner", "", null);
    owner.Request.Path = "/_vsix/workspace/snapshot";
    var ownerCalled = false;
    ConnectionKeyHttpAuthorization.InvokeAsync(owner, f.Store, value => value == "trusted-owner",
        _ => { ownerCalled = true; return Task.CompletedTask; }).GetAwaiter().GetResult();
    Assert(ownerCalled, "owner credential may publish workspace snapshot");

    var key = HttpRequest("POST", "wrong", "", null);
    key.Request.Path = "/_vsix/workspace/snapshot";
    var keyCalled = false;
    ConnectionKeyHttpAuthorization.InvokeAsync(key, f.Store, _ => false,
        _ => { keyCalled = true; return Task.CompletedTask; }).GetAwaiter().GetResult();
    Assert(key.Response.StatusCode == 401 && !keyCalled, "connection-key credential cannot publish workspace snapshot");
}
static void HttpProtocolProbe()
{
    var f=new Fixture(); var issued=f.Store.Issue(); ConnectionKeyCodec.TryDecode(issued.Key,out var p);
    var probe=HttpRequest("POST",p!.ToBearerValue(),"tools/list",null); probe.Request.Headers["MCP-Protocol-Version"]="2026-07-28";
    var passed=false;
    ConnectionKeyHttpAuthorization.InvokeAsync(probe,f.Store,_=>false,_=>{passed=true;return Task.CompletedTask;}).GetAwaiter().GetResult();
    Assert(passed && f.Store.ValidatePending(p.ToBearerValue(),f.Store.Identity)==ConnectionKeyResult.Success,"protocol probe leaves key pending");
}
static DefaultHttpContext HttpRequest(string method,string bearer,string? rpcMethod,string? sessionId)
{
    var context=new DefaultHttpContext(); context.Request.Method=method; context.Request.Path="/mcp";
    context.Request.Headers.Authorization="Bearer "+bearer;
    if(sessionId is not null) context.Request.Headers["Mcp-Session-Id"]=sessionId;
    if(rpcMethod is not null)
    {
        context.Request.ContentType="application/json";
        var protocol=rpcMethod=="initialize"?"\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}":"";
        var json=rpcMethod=="initialize"?$"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{{{protocol}}}}}":$"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{rpcMethod}\",\"params\":{{}}}}";
        context.Request.Body=new MemoryStream(Encoding.UTF8.GetBytes(json)); context.Request.ContentLength=context.Request.Body.Length;
    }
    context.Response.Body=new MemoryStream(); return context;
}
static ConnectionKeyPayload Payload() => new(1, ConnectionKeyCodec.Base64UrlEncode(Enumerable.Range(0,16).Select(i=>(byte)i).ToArray()), 10, 638900000000000000, 11, ConnectionKeyCodec.Base64UrlEncode(Enumerable.Range(16,16).Select(i=>(byte)i).ToArray()), "http://127.0.0.1:1234/mcp", ConnectionKeyCodec.Base64UrlEncode(Enumerable.Range(32,32).Select(i=>(byte)i).ToArray()), ConnectionKeyCodec.Base64UrlEncode(Enumerable.Range(64,32).Select(i=>(byte)i).ToArray()), 1900000000);
static void Assert(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class Fixture
{
    public FakeClock Clock { get; } = new();
    public ConnectionKeyStore Store { get; }
    public Fixture() => Store = new(Clock, Clock, 10, 638900000000000000, 11, ConnectionKeyCodec.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)), "http://127.0.0.1:1234/mcp");
}
sealed class FakeClock : IConnectionKeyClock, IConnectionKeyRandom
{
    private long _counter;
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    public TimeSpan MonotonicElapsed { get; private set; }
    public void Advance(TimeSpan value) { UtcNow += value; MonotonicElapsed += value; }
    public void Fill(Span<byte> destination) { var bytes = RandomNumberGenerator.GetBytes(destination.Length); Interlocked.Increment(ref _counter); bytes[0] ^= (byte)_counter; bytes.CopyTo(destination); }
}










