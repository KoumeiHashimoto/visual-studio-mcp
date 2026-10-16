using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace VisualStudioMcpServer.ConnectionKeys;

/// <summary>Authorizes connection-key clients and binds them to one stateful MCP session.</summary>
internal static class ConnectionKeyHttpAuthorization
{
    internal static async Task InvokeAsync(HttpContext context, ConnectionKeyStore store,
        Func<string, bool> isOwnerCredential, RequestDelegate next)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim()
            : string.Empty;
        var ownerPath = context.Request.Path.Value;
        var ownerRoute = ownerPath switch
        {
            "/_vsix/shutdown" => HttpMethods.IsPost(context.Request.Method),
            "/_vsix/connection-key/issue" or "/_vsix/connection-key/cancel" or
            "/_vsix/connection-key/revoke-all" or "/_vsix/connection-key/status" => HttpMethods.IsPost(context.Request.Method),
            _ => false
        };
        if (isOwnerCredential(bearer))
        {
            if (context.Request.Path.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                var legacySessionId = context.Request.Headers["Mcp-Session-Id"].ToString();
                if (!string.IsNullOrEmpty(legacySessionId) && store.IsKeyedSession(legacySessionId))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await next(context);
                return;
            }
            if (ownerRoute)
            {
                if (!await HasBoundedOwnerBodyAsync(context))
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }
                await next(context);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!context.Request.Path.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method) && !HttpMethods.IsGet(context.Request.Method) &&
            !HttpMethods.IsDelete(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var sessionId = context.Request.Headers["Mcp-Session-Id"].ToString();
        if (sessionId.Length > 256 || sessionId.Any(character => character > 0x7f))
        {
            await RejectAuthAsync(context, ConnectionKeyResult.SessionMismatch);
            return;
        }
        var initializing = string.IsNullOrEmpty(sessionId);
        Guid reservationId = Guid.Empty;
        if (initializing)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (!context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? true)
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }
            var pending = store.ValidatePending(bearer, store.Identity);
            if (pending != ConnectionKeyResult.Success)
            {
                await RejectAuthAsync(context, pending);
                return;
            }
            if (IsNewerSessionlessProtocol(context.Request.Headers["MCP-Protocol-Version"].ToString()))
            {
                await next(context);
                return;
            }
            if (!await IsInitializeRequestAsync(context, 64 * 1024))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var reservation = store.Reserve(bearer, store.Identity);
            if (reservation.Result != ConnectionKeyResult.Success)
            {
                await RejectAuthAsync(context, reservation.Result);
                return;
            }
            reservationId = reservation.ReservationId;
        }
        else
        {
            var result = store.AuthenticateSession(bearer, sessionId, store.Identity);
            if (result != ConnectionKeyResult.Success)
            {
                await RejectAuthAsync(context, result);
                return;
            }
            if (HttpMethods.IsPost(context.Request.Method) && await IsInitializeRequestAsync(context, 1024 * 1024))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
        }

        CancellationToken grantToken;
        var hasGrantToken = initializing
            ? store.TryGetReservationCancellationToken(reservationId, out grantToken)
            : store.TryGetSessionCancellationToken(sessionId, out grantToken);
        if (!hasGrantToken)
        {
            if (initializing) store.FailClosed(reservationId);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var deleting = !initializing && HttpMethods.IsDelete(context.Request.Method);
        if (deleting && store.CloseSession(sessionId) != ConnectionKeyResult.Success)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,
            deleting ? CancellationToken.None : grantToken);
        context.RequestAborted = linked.Token;
        var initializeResponseObserved = false;
        var initializeBound = false;
        if (initializing)
        {
            context.Response.OnStarting(() =>
            {
                initializeResponseObserved = true;
                BindInitializeResponse();
                return Task.CompletedTask;
            });
        }
        try
        {
            await next(context);
            if (!initializing && !deleting && context.Response.StatusCode == StatusCodes.Status404NotFound)
                store.CloseSession(sessionId);
            if (initializing && !initializeResponseObserved) BindInitializeResponse();
        }
        catch
        {
            if (initializing) store.FailClosed(reservationId);
            throw;
        }

        void BindInitializeResponse()
        {
            if (initializeBound) return;
            var createdSessionId = context.Response.Headers["Mcp-Session-Id"].ToString();
            if (context.RequestAborted.IsCancellationRequested || context.Response.StatusCode is < 200 or >= 300 ||
                string.IsNullOrWhiteSpace(createdSessionId))
            {
                store.FailClosed(reservationId);
                return;
            }
            var activated = store.Activate(reservationId, createdSessionId);
            if (activated != ConnectionKeyResult.Success)
            {
                store.FailClosed(reservationId);
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }
            initializeBound = true;
            context.Response.OnCompleted(() =>
            {
                if (context.RequestAborted.IsCancellationRequested) store.CloseSession(createdSessionId);
                return Task.CompletedTask;
            });
        }
    }

    private static async Task<bool> IsInitializeRequestAsync(HttpContext context, long maximumBodyBytes)
    {
        if (context.Request.ContentLength is long length && (length > maximumBodyBytes || length == 0)) return false;
        context.Request.EnableBuffering(64 * 1024, maximumBodyBytes);
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body,
                new JsonDocumentOptions { MaxDepth = 16 }, context.RequestAborted);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("jsonrpc", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == "2.0" &&
                root.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number &&
                root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && method.GetString() == "initialize" &&
                root.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("protocolVersion", out var protocolVersion) && protocolVersion.ValueKind == JsonValueKind.String &&
                parameters.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("clientInfo", out var clientInfo) && clientInfo.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        finally { if (context.Request.Body.CanSeek) context.Request.Body.Position = 0; }
    }

    private static async Task<bool> HasBoundedOwnerBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength is > 4096) return false;
        context.Request.EnableBuffering(4096, 4096);
        var buffer = new byte[1024];
        var readTotal = 0;
        try
        {
            while (readTotal <= 4096)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0,
                    Math.Min(buffer.Length, 4097 - readTotal)), context.RequestAborted);
                if (read == 0) break;
                readTotal += read;
            }
            return readTotal <= 4096;
        }
        catch (IOException) { return false; }
        finally { if (context.Request.Body.CanSeek) context.Request.Body.Position = 0; }
    }

    private static bool IsNewerSessionlessProtocol(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var version) && version >= new DateOnly(2026, 7, 28);

    private static async Task RejectAuthAsync(HttpContext context, ConnectionKeyResult result)
    {
        var status = result switch
        {
            ConnectionKeyResult.RateLimited or ConnectionKeyResult.CapacityExceeded => StatusCodes.Status429TooManyRequests,
            ConnectionKeyResult.IdentityMismatch => StatusCodes.Status403Forbidden,
            ConnectionKeyResult.SessionMismatch or ConnectionKeyResult.SessionRequired or ConnectionKeyResult.NotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status401Unauthorized
        };
        if (status == StatusCodes.Status429TooManyRequests) context.Response.Headers.RetryAfter = "5";
        var code = result switch
        {
            ConnectionKeyResult.RateLimited => "key_rate_limited",
            ConnectionKeyResult.CapacityExceeded => "key_capacity_exceeded",
            ConnectionKeyResult.IdentityMismatch => "server_identity_mismatch",
            ConnectionKeyResult.Expired => "key_expired",
            ConnectionKeyResult.AlreadyUsed => "key_already_used",
            ConnectionKeyResult.SessionMismatch => "session_mismatch",
            ConnectionKeyResult.SessionRequired => "session_required",
            ConnectionKeyResult.NotFound => "session_expired",
            _ => "key_invalid"
        };
        var response = System.Text.Json.JsonSerializer.Serialize(new
        {
            code,
            message = code switch
            {
                "key_rate_limited" => "Authentication attempts are temporarily limited.",
                "key_capacity_exceeded" => "Connection-key session capacity is full.",
                "server_identity_mismatch" => "The credential belongs to a different server instance.",
                "key_expired" => "The connection key has expired.",
                "key_already_used" => "This connection key cannot create another session.",
                "session_mismatch" => "The bearer credential does not match this MCP session.",
                "session_required" => "An authenticated MCP session is required.",
                "session_expired" => "The MCP session is no longer available.",
                _ => "The connection credential is invalid."
            },
            retryable = status == StatusCodes.Status429TooManyRequests,
            mayHaveChanged = false
        });
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = System.Text.Encoding.UTF8.GetByteCount(response);
        await context.Response.WriteAsync(response, context.RequestAborted);
    }
}
