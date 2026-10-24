using System.Threading.RateLimiting;
using System.Text.Json;
using Duluka.Server.Admin;
using Duluka.Server.CBT1;
using Duluka.Server.Auth;
using Duluka.Server.Data;
using Duluka.Server.Domain;
using Duluka.Server.Security;

// CWD-independence (C/1): ContentRoot defaults to the process's CURRENT
// DIRECTORY, which would make appsettings.json (and every config key in it)
// load from wherever the EXE happens to be started. Pin it to the deployed
// application location — same keys, same precedence, stable location.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Secrets come from environment ONLY (DULUKA_GitHub__ClientSecret etc.) —
// appsettings.json carries empty placeholders by design (hard rule:
// no password/token in plain config).
builder.Configuration.AddEnvironmentVariables(prefix: "DULUKA_");

builder.Services.AddSingleton<OAuthFlowStore>();
builder.Services.AddSingleton<GitHubOAuthService>();
builder.Services.AddSingleton<AccountProvisioningService>();
builder.Services.AddSingleton<NativeAuthService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<Cbt1Store>();
// Browser access is limited to the official project site; no credentials/cookies.
builder.Services.AddCors(options => options.AddPolicy("cbt1-web", policy =>
    policy.WithOrigins("https://scotcsduluka.github.io")
          .WithMethods("GET", "POST", "DELETE")
          .AllowAnyHeader()));
builder.Services.AddHttpClient("github");

builder.Services.AddSingleton<Database>(sp =>
{
    var path = builder.Configuration["Database:Path"] ?? "AppData/DulukaAccount.db";
    var db = new Database(path, sp.GetRequiredService<ILogger<Database>>());
    db.Bootstrap();
    return db;
});

// Rate-limit boundary (C/6): auth entry points are the abuse surface — a fixed
// per-IP window on flow starts, a wider window on authenticated API calls.
// Rejections speak the same §7.1 envelope as every other error path.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = static (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(new
        {
            ok = false,
            reqId = Wire.ReqId(context.HttpContext.Request),
            errorCode = WireCodes.ServerRateLimited,
            httpStatus = 429,
            retryable = true,
            conflict = (object?)null,
            message = "Too many requests — retry later.",
        }, ct));
    };
    options.AddPolicy("cbt1-apply", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromHours(1) }));
    options.AddPolicy("auth-start", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("api", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1) }));
});

// /admin live log tail: mirror every Info+ record into a bounded ring buffer
// (console output untouched — this is purely an operator-console feed).
var adminLogs = new AdminLogBuffer();
builder.Services.AddSingleton(adminLogs);
builder.Logging.AddProvider(new AdminLogBufferProvider(adminLogs));

var app = builder.Build();
app.UseCors("cbt1-web");
app.UseRateLimiter();
app.MapCbt1();

// ─── helpers ────────────────────────────────────────────────────────────────

static string? BearerToken(HttpRequest req)
{
    var auth = req.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    return auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && auth.Length > prefix.Length
        ? auth[prefix.Length..].Trim()
        : null;
}

var sessionService = app.Services.GetRequiredService<SessionService>();
var db = app.Services.GetRequiredService<Database>();

// ─── health ─────────────────────────────────────────────────────────────────

app.MapGet("/healthz", () => Results.Ok(new { status = "live" }));
app.MapGet("/healthz/ready", () =>
{
    // readiness = schema present (Database.Bootstrap ran before endpoints bind)
    return Results.Ok(new { status = "ready", schema = Database.SchemaVersion });
});

// ─── auth: provider flows ───────────────────────────────────────────────────

var flows = app.Services.GetRequiredService<OAuthFlowStore>();
var github = app.Services.GetRequiredService<GitHubOAuthService>();
var provisioning = app.Services.GetRequiredService<AccountProvisioningService>();

app.MapPost("/v1/auth/{provider}/start", async (string provider, HttpRequest req) =>
{
    // NVIDIA (and any unimplemented provider) is RESERVED: no auth flow exists.
    // Hard rule: hardware-derived identity (GPU UUID/serial/driver/fingerprint)
    // is never accepted as an auth identity — that would be a spoofable bypass.
    if (!ProviderKeys.IsImplemented(provider))
        return Wire.Err(req, 501, WireCodes.ProviderReserved,
            $"Provider '{provider}' is reserved but has no authentication flow in this version.");

    using var body = await Wire.TryParseBodyAsync(req);
    if (body is null)
        return Wire.Err(req, 400, WireCodes.BadRequest, "Request body must be valid JSON.");
    var deviceName = body.RootElement.TryGetProperty("deviceName", out var dn) ? dn.GetString() : null;
    var deviceKey = body.RootElement.TryGetProperty("deviceKey", out var dk) ? dk.GetString() : null;

    if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Length > 64)
        return Wire.Err(req, 400, "invalid_device_name", "deviceName is required (max 64 chars).");
    if (string.IsNullOrWhiteSpace(deviceKey) || deviceKey.Length < 43)
        return Wire.Err(req, 400, "invalid_device_key", "deviceKey must be a client-generated base64url value of at least 43 chars (256 bits).");

    var state = Secrets.NewToken("duluka_state_");
    var flow = flows.Put(new OAuthFlow(
        state, Secrets.NewPkceVerifier(), SessionTokenHash: null, DeviceKeyHash: null,
        deviceName.Trim(), DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10)));

    // The client's device key is delivered with the COMPLETING callback request
    // (kept client-side until then); the flow binds it via the callback body.
    var (_, url) = github.BuildAuthorizeUrl(flow);
    // The state is returned RAW: it is this client's own correlation value and
    // must be echoed on the callback. Redaction (§9.2) applies to LOGS, never
    // to the value the flow itself handed to this client.
    return Wire.Ok(req, new { authorizationUrl = url, state = flow.State, expiresInMinutes = 10 });
}).RequireRateLimiting("auth-start");

app.MapPost("/v1/auth/{provider}/callback", async (string provider, HttpRequest req) =>
{
    if (!ProviderKeys.IsImplemented(provider))
        return Wire.Err(req, 501, WireCodes.ProviderReserved, $"Provider '{provider}' is reserved in this version.");

    using var body = await Wire.TryParseBodyAsync(req);
    if (body is null)
        return Wire.Err(req, 400, WireCodes.BadRequest, "Request body must be valid JSON.");
    string get(string name) =>
        body.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? "" : "";

    var code = get("code");
    var state = get("state");
    var deviceKey = get("deviceKey");

    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        return Wire.Err(req, 400, "invalid_callback", "code and state are required.");
    if (string.IsNullOrWhiteSpace(deviceKey) || deviceKey.Length < 43)
        return Wire.Err(req, 400, "invalid_device_key", "deviceKey is required and must be at least 43 base64url chars.");

    var flow = flows.Consume(state);
    if (flow is null)
        return Wire.Err(req, 400, "invalid_state", "Unknown, expired, or already-used state — restart the flow.");

    var deviceKeyHash = Secrets.Sha256Hex(deviceKey);
    try
    {
        var identity = await github.ExchangeForIdentityAsync(code, flow);
        var (link, account, existed) = provisioning.LoginOrLink(identity, deviceKeyHash, flow.DeviceName);
        var device = db.FindDeviceByKeyHash(deviceKeyHash)
                     ?? throw new InvalidOperationException("device_missing_after_provision");
        var (token, session) = sessionService.Create(account.AccountId, device.DeviceId, link.LinkId);
        return Wire.Ok(req, new
        {
            sessionToken = token,
            accountId = account.AccountId,
            deviceId = device.DeviceId,
            existingAccount = existed,
            sessionExpiresAt = session.ExpiresAt,
            // Parity with /v1/auth/login + /v1/auth/register: the client
            // seeds its local profile from this response. Without these
            // fields a fully set-up account re-logging in with GitHub
            // looked locally "needs setup" and was trapped on the client's
            // Setup screen with a dead Back button — the real decision
            // lives server-side (this credential lookup / GET /me).
            username = db.FindNativeCredentialByAccount(account.AccountId)?.UsernameDisplay,
            displayName = account.DisplayName,
        });
    }
    catch (GitHubOAuthException ex)
    {
        return Wire.Err(req, 400, ex.Message, "Provider callback rejected.");
    }
    catch (InvalidOperationException ex) when (ex.Message == "device_revoked")
    {
        return Wire.Err(req, 403, WireCodes.PermDeviceRemoved, "This device key was revoked. Generate a new device key and retry.");
    }
    catch (InvalidOperationException ex) when (ex.Message == "device_key_in_use")
    {
        return Wire.Err(req, 409, WireCodes.ConflictLink, "This device key is already bound to another account.",
            new { kind = "device_key_bound_to_other_account" });
    }
    catch (Exception ex)
    {
        // §5.4-2: every failure path converges to a well-formed terminal
        // envelope — never a bare 500 with an infrastructure body.
        app.Logger.LogError(ex, "Unhandled auth callback failure");
        return Wire.Err(req, 500, WireCodes.ServerInternal, "Internal error — retry the request with the same id.");
    }
}).RequireRateLimiting("auth-start");

// ─── auth: link flows for an authenticated account ─────────────────────────

app.MapPost("/v1/account/providers", async (HttpRequest req) =>
{
    var token = BearerToken(req);
    if (token is null) return Wire.Err(req, 401, WireCodes.AuthSessionExpired, "Authorization: Bearer <session token> required.");
    var validation = sessionService.Validate(token);
    if (validation is null) return Wire.DeadSessionErr(req, sessionService, token, "Session is expired, revoked, or unknown.");

    using var body = await Wire.TryParseBodyAsync(req);
    if (body is null)
        return Wire.Err(req, 400, WireCodes.BadRequest, "Request body must be valid JSON.");
    var provider = body.RootElement.TryGetProperty("provider", out var p) ? p.GetString() : null;
    if (string.IsNullOrWhiteSpace(provider)) return Wire.Err(req, 400, "invalid_provider", "provider is required.");
    if (!ProviderKeys.IsImplemented(provider))
        return Wire.Err(req, 501, WireCodes.ProviderReserved, $"Provider '{provider}' is reserved in this version.");

    var state = Secrets.NewToken("duluka_state_");
    var flow = flows.Put(new OAuthFlow(
        state, Secrets.NewPkceVerifier(),
        SessionTokenHash: Secrets.Sha256Hex(token),   // link flows complete onto THIS account
        DeviceKeyHash: null, DeviceName: "link-flow",
        DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10)));
    var (_, url) = github.BuildAuthorizeUrl(flow);
    return Wire.Ok(req, new { authorizationUrl = url, state = flow.State });   // raw state — same rule as login start
}).RequireRateLimiting("auth-start");

app.MapPost("/v1/account/providers/{provider}/complete", async (string provider, HttpRequest req) =>
{
    if (!ProviderKeys.IsImplemented(provider))
        return Wire.Err(req, 501, WireCodes.ProviderReserved, $"Provider '{provider}' is reserved in this version.");

    using var body = await Wire.TryParseBodyAsync(req);
    if (body is null)
        return Wire.Err(req, 400, WireCodes.BadRequest, "Request body must be valid JSON.");
    string get(string name) =>
        body.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? "" : "";
    var code = get("code");
    var state = get("state");

    var flow = flows.Consume(state);
    if (flow is null || flow.SessionTokenHash is null)
        return Wire.Err(req, 400, "invalid_state", "Unknown, expired, or non-link state.");
    var validation = db.ValidateSession(flow.SessionTokenHash);
    if (validation is null)
    {
        // Same §6.3 root-cause vocabulary as every other surface (M-11):
        // revoked → 401 auth.session_revoked, suspended → 403
        // perm.account_suspended, everything else → 401 auth.session_expired.
        var hash = flow.SessionTokenHash;
        return db.SessionTokenWasRevoked(hash)
            ? Wire.Err(req, 401, WireCodes.AuthSessionRevoked, "The session that started this link flow was revoked.")
            : db.LiveSessionAccountSuspended(hash)
            ? Wire.Err(req, 403, WireCodes.PermAccountSuspended, "The account that started this link flow is suspended.")
            : Wire.Err(req, 401, WireCodes.AuthSessionExpired, "The session that started this link flow is gone.");
    }

    var accountId = validation.Value.Session.AccountId;
    try
    {
        var identity = await github.ExchangeForIdentityAsync(code, flow);
        var existing = db.FindActiveLink(identity.ProviderKey, identity.ProviderUserId);
        if (existing is not null)
            return existing.AccountId == accountId
                ? Wire.Ok(req, new { linked = true, linkId = existing.LinkId, already = true })
                : Wire.Err(req, 409, WireCodes.ConflictLink,
                    "This provider identity is already linked to another account. Unlink it there first.",
                    Wire.OwnConflictResource(db, accountId, "provider_identity_bound_to_other_account"));
        var link = db.CreateLink(accountId, identity.ProviderKey, identity.ProviderUserId,
            identity.ProviderEmail, identity.DisplayName);
        if (!string.Equals(link.AccountId, accountId, StringComparison.Ordinal))
            // Lost the unique-anchor race — the surviving row belongs to
            // another account; never report a foreign link as ours (§6.2:
            // deterministic 409, no silent merge).
            return Wire.Err(req, 409, WireCodes.ConflictLink,
                "This provider identity is already linked to another account. Unlink it there first.",
                Wire.OwnConflictResource(db, accountId, "provider_identity_bound_to_other_account"));
        return Wire.Ok(req, new { linked = true, linkId = link.LinkId, already = false });
    }
    catch (GitHubOAuthException ex)
    {
        return Wire.Err(req, 400, ex.Message, "Provider callback rejected.");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled link completion failure");
        return Wire.Err(req, 500, WireCodes.ServerInternal, "Internal error — retry the request with the same id.");
    }
}).RequireRateLimiting("auth-start");

// ─── auth: native username/password (Duluka's own credential) ───────────────

var nativeAuth = app.Services.GetRequiredService<NativeAuthService>();

static string? NativeAuthFieldError(string username, string password, string deviceKey, string deviceName)
{
    if (!UsernamePolicy.IsValidFormat(username)) return WireCodes.InvalidUsername;
    if (string.IsNullOrEmpty(password)) return WireCodes.InvalidPassword;
    if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Length > 64) return "invalid_device_name";
    if (string.IsNullOrWhiteSpace(deviceKey) || deviceKey.Length < 43) return "invalid_device_key";
    return null;
}

static string NativeAuthFieldText(string code) => code switch
{
    var c when c == WireCodes.InvalidUsername => "Username must be 3-32 characters (letters, digits, dot, underscore, hyphen).",
    var c when c == WireCodes.InvalidPassword => "Password must be 8-128 characters.",
    "invalid_device_name" => "deviceName is required (max 64 chars).",
    "invalid_device_key" => "deviceKey must be a client-generated base64url value of at least 43 chars (256 bits).",
    _ => "Invalid request.",
};

static (string Username, string Password, string DeviceKey, string DeviceName) ReadNativeAuthBody(JsonDocument body)
{
    string get(string name) =>
        body.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? "" : "";
    return (get("username"), get("password"), get("deviceKey"), get("deviceName"));
}

app.MapPost("/v1/auth/register", async (HttpRequest req) =>
{
    using var body = await Wire.TryParseBodyAsync(req);
    if (body is null)
        return Wire.Err(req, 400, WireCodes.BadRequest, "Request body must be valid JSON.");
    var (username, password, deviceKey, deviceName) = ReadNativeAuthBody(body);
    var fieldError = NativeAuthFieldError(username, password, deviceKey, deviceName)
                     ?? (UsernamePolicy.IsValidPassword(password) ? null : WireCodes.InvalidPassword);
    if (fieldError is not null)
        return Wire.Err(req, 400, fieldError, NativeAuthFieldText(fieldError));

    var deviceKeyHash = Secrets.Sha256Hex(deviceKey);
    try
    {
        var (account, device) = nativeAuth.Register(
            username, password, deviceKeyHash, deviceName.Trim());
        var (token, session) = sessionService.Create(account.AccountId, device.DeviceId, null);
        return Wire.Ok(req, new
        {
            sessionToken = token,
            accountId = account.AccountId,
            deviceId = device.DeviceId,
            username = username.Trim(),
            sessionExpiresAt = session.ExpiresAt,
        });
    }
    catch (InvalidOperationException ex) when (ex.Message == "username_taken")
    {
        return Wire.Err(req, 409, WireCodes.ConflictUsernameTaken,
            "That username is already taken.");
    }
    catch (InvalidOperationException ex) when (ex.Message == "device_revoked")
    {
        return Wire.Err(req, 403, WireCodes.PermDeviceRemoved, "This device key was revoked. Generate a new device key and retry.");
    }
    catch (InvalidOperationException ex) when (ex.Message == "device_key_in_use")
    {
        return Wire.Err(req, 409, WireCodes.ConflictLink, "This device key is already bound to another account.",
            new { kind = "device_key_bound_to_other_account" });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled register failure");
        return Wire.Err(req, 500, WireCodes.ServerInternal, "Internal error — retry the request with the same id.");
    }
}).RequireRateLimiting("auth-start");

app.MapPost("/v1/auth/login", async (HttpRequest req) =>
{
    using var body = await Wire.TryParseBodyAsync(req);
    if (body is null)
        return Wire.Err(req, 400, WireCodes.BadRequest, "Request body must be valid JSON.");
    var (username, password, deviceKey, deviceName) = ReadNativeAuthBody(body);
    var fieldError = NativeAuthFieldError(username, password, deviceKey, deviceName)
                     ?? (UsernamePolicy.IsValidPassword(password) ? null : WireCodes.InvalidPassword);
    if (fieldError is not null)
        return Wire.Err(req, 400, fieldError, NativeAuthFieldText(fieldError));

    var deviceKeyHash = Secrets.Sha256Hex(deviceKey);
    try
    {
        var (account, device) = nativeAuth.Login(
            username, password, deviceKeyHash, deviceName.Trim());
        var (token, session) = sessionService.Create(account.AccountId, device.DeviceId, null);
        var native = db.FindNativeCredentialByAccount(account.AccountId);
        return Wire.Ok(req, new
        {
            sessionToken = token,
            accountId = account.AccountId,
            deviceId = device.DeviceId,
            username = native?.UsernameDisplay,
            sessionExpiresAt = session.ExpiresAt,
        });
    }
    catch (InvalidOperationException ex) when (ex.Message == "invalid_credentials")
    {
        // GENERIC by design: unknown username and wrong password are the same
        // answer on the wire (no account enumeration).
        return Wire.Err(req, 401, WireCodes.InvalidCredentials, "Incorrect username or password.");
    }
    catch (InvalidOperationException ex) when (ex.Message == "account_suspended")
    {
        return Wire.Err(req, 403, WireCodes.PermAccountSuspended, "This account is suspended.");
    }
    catch (InvalidOperationException ex) when (ex.Message == "device_revoked")
    {
        return Wire.Err(req, 403, WireCodes.PermDeviceRemoved, "This device key was revoked. Generate a new device key and retry.");
    }
    catch (InvalidOperationException ex) when (ex.Message == "device_key_in_use")
    {
        return Wire.Err(req, 409, WireCodes.ConflictLink, "This device key is already bound to another account.",
            new { kind = "device_key_bound_to_other_account" });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled login failure");
        return Wire.Err(req, 500, WireCodes.ServerInternal, "Internal error — retry the request with the same id.");
    }
}).RequireRateLimiting("auth-start");

// ─── auth: session lifecycle ────────────────────────────────────────────────

app.MapPost("/v1/auth/session/refresh", (HttpRequest req) =>
{
    var token = BearerToken(req);
    if (token is null) return Wire.Err(req, 401, WireCodes.AuthSessionExpired, "Authorization: Bearer <session token> required.");
    var expires = sessionService.Refresh(token);
    return expires is null
        ? Wire.DeadSessionErr(req, sessionService, token, "Session is expired, revoked, or unknown.")
        : Wire.Ok(req, new { sessionExpiresAt = expires });
}).RequireRateLimiting("api");

app.MapPost("/v1/account/password", async (HttpRequest req) =>
{
    var token = BearerToken(req);
    if (token is null) return Wire.Err(req, 401, WireCodes.AuthSessionExpired, "Authorization: Bearer <session token> required.");
    var validation = sessionService.Validate(token);
    if (validation is null) return Wire.DeadSessionErr(req, sessionService, token, "Session is expired, revoked, or unknown.");

    using var body = await Wire.TryParseBodyAsync(req);
    if (body is null)
        return Wire.Err(req, 400, WireCodes.BadRequest, "Request body must be valid JSON.");
    string get(string name) =>
        body.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? "" : "";
    var currentPassword = get("currentPassword");
    var newPassword = get("newPassword");
    var username = get("username");
    if (!UsernamePolicy.IsValidPassword(newPassword))
        return Wire.Err(req, 400, WireCodes.InvalidPassword,
            $"Password must be {UsernamePolicy.MinPasswordLength}-{UsernamePolicy.MaxPasswordLength} characters.");

    var accountId = validation.Value.Item3.AccountId;

    // Account WITH a native credential → change requires the current password.
    if (db.HasNativeCredential(accountId))
    {
        if (string.IsNullOrEmpty(currentPassword))
            return Wire.Err(req, 400, WireCodes.InvalidCredentials, "Current password is required.");
        try
        {
            nativeAuth.ChangePassword(accountId, currentPassword, Secrets.HashPassword(newPassword));
            return Wire.Ok(req, new { changed = true });
        }
        catch (InvalidOperationException ex) when (ex.Message == "invalid_credentials")
        {
            return Wire.Err(req, 400, WireCodes.InvalidCredentials, "Current password is incorrect.");
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Unhandled password change failure");
            return Wire.Err(req, 500, WireCodes.ServerInternal, "Internal error — retry the request with the same id.");
        }
    }

    // PROVIDER-ONLY (bootstrapped) account → first-time password: the user
    // picks a username, no current password exists to verify. This releases
    // the last-provider trap: once the account has a password, its provider
    // link can be unlinked without locking the account out.
    if (!UsernamePolicy.IsValidFormat(username))
        return Wire.Err(req, 400, WireCodes.InvalidUsername,
            "username is required when adding password login to a provider-only account.");
    try
    {
        // SetInitialPassword hashes internally — pass the PLAINTEXT (a
        // pre-hashed value here would be hashed twice and never verify).
        nativeAuth.SetInitialPassword(accountId, username, newPassword);
        return Wire.Ok(req, new { changed = true, username = username.Trim() });
    }
    catch (InvalidOperationException ex) when (ex.Message == "username_taken")
    {
        return Wire.Err(req, 409, WireCodes.ConflictUsernameTaken, "That username is already taken.");
    }
    catch (InvalidOperationException ex) when (ex.Message == "credential_exists")
    {
        return Wire.Err(req, 409, WireCodes.ConflictUsernameTaken, "This account already has a password.");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled initial password failure");
        return Wire.Err(req, 500, WireCodes.ServerInternal, "Internal error — retry the request with the same id.");
    }
}).RequireRateLimiting("api");

app.MapPost("/v1/auth/session/revoke", (HttpRequest req) =>
{
    var token = BearerToken(req);
    if (token is null) return Wire.Err(req, 401, WireCodes.AuthSessionExpired, "Authorization: Bearer <session token> required.");
    var revoked = sessionService.Revoke(token, "user-logout");
    return revoked
        ? Wire.Ok(req, new { revoked = true })
        : Wire.DeadSessionErr(req, sessionService, token, "Unknown session.");
}).RequireRateLimiting("api");

app.MapPost("/v1/auth/sessions/revoke-all", (HttpRequest req) =>
{
    var token = BearerToken(req);
    var validation = token is null ? null : sessionService.Validate(token);
    if (validation is null)
        return Wire.DeadSessionErr(req, sessionService, token, "Session is expired, revoked, or unknown.");
    var count = sessionService.RevokeAll(validation.Value.Item3.AccountId, "logout-all");
    return Wire.Ok(req, new { revokedSessions = count });
}).RequireRateLimiting("api");

// ─── account ────────────────────────────────────────────────────────────────

app.MapGet("/v1/account/me", (HttpRequest req) =>
{
    var token = BearerToken(req);
    var validation = token is null ? null : sessionService.Validate(token);
    if (validation is null)
        return Wire.DeadSessionErr(req, sessionService, token, "Session is expired, revoked, or unknown.");
    var (_, device, account) = validation.Value;
    var native = db.FindNativeCredentialByAccount(account.AccountId);
    return Wire.Ok(req, new
    {
        accountId = account.AccountId,
        displayName = account.DisplayName,
        profileImage = account.ProfileImage,
        username = native?.UsernameDisplay,
        createdAt = account.CreatedAt,
        currentDevice = new { device.DeviceId, device.DeviceName },
    });
}).RequireRateLimiting("api");

/// <summary>
/// Update the account's PROFILE presentation fields. The body is a full profile
/// snapshot: omitted/null fields CLEAR those values (deterministic overwrite,
/// no merge). Only DisplayName + ProfileImage are touched — AccountId, Status,
/// the username credential, devices and sessions are structurally unreachable
/// from this endpoint. 401 semantics are identical to every other account
/// surface (no profile change ever mints or revokes a session).
/// </summary>
app.MapPut("/v1/account/profile", async (HttpRequest req) =>
{
    var token = BearerToken(req);
    var validation = token is null ? null : sessionService.Validate(token);
    if (validation is null)
        return Wire.DeadSessionErr(req, sessionService, token, "Session is expired, revoked, or unknown.");

    using var body = await Wire.TryParseBodyAsync(req);
    if (body is null)
        return Wire.Err(req, 400, WireCodes.BadRequest, "Request body must be valid JSON.");

    string? GetOptionalString(string name)
    {
        if (!body.RootElement.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
    // Missing key OR explicit null OR empty = CLEAR; otherwise trimmed text.
    var displayName = GetOptionalString("displayName");
    var profileImage = GetOptionalString("profileImage");

    if (!ProfilePolicy.IsValidDisplayName(displayName))
        return Wire.Err(req, 400, WireCodes.InvalidDisplayName,
            $"displayName must be at most {ProfilePolicy.MaxDisplayNameLength} characters with no control characters.");
    if (!ProfilePolicy.IsValidProfileImage(profileImage))
        return Wire.Err(req, 400, WireCodes.InvalidProfileImage,
            "profileImage must be a data:image/png|jpeg|webp base64 data URL within the size cap.");

    var accountId = validation.Value.Item3.AccountId;
    try
    {
        var trimmedName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        var storedImage = string.IsNullOrWhiteSpace(profileImage) ? null : profileImage;
        db.UpdateAccountProfile(accountId, trimmedName, storedImage);
        var account = db.GetAccount(accountId)!;
        var native = db.FindNativeCredentialByAccount(accountId);
        return Wire.Ok(req, new
        {
            accountId = account.AccountId,
            displayName = account.DisplayName,
            profileImage = account.ProfileImage,
            username = native?.UsernameDisplay,
        });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled profile update failure");
        return Wire.Err(req, 500, WireCodes.ServerInternal, "Internal error — retry the request with the same id.");
    }
}).RequireRateLimiting("api");

app.MapGet("/v1/account/providers", (HttpRequest req) =>
{
    var token = BearerToken(req);
    var validation = token is null ? null : sessionService.Validate(token);
    if (validation is null)
        return Wire.DeadSessionErr(req, sessionService, token, "Session is expired, revoked, or unknown.");
    var links = db.LinksForAccount(validation.Value.Item3.AccountId)
        .Select(l => new { l.LinkId, l.ProviderKey, l.ProviderEmail, l.Status, l.LinkedAt });
    return Wire.Ok(req, new { providers = links });
}).RequireRateLimiting("api");

app.MapDelete("/v1/account/providers/{linkId}", (string linkId, HttpRequest req) =>
{
    var token = BearerToken(req);
    var validation = token is null ? null : sessionService.Validate(token);
    if (validation is null)
        return Wire.DeadSessionErr(req, sessionService, token, "Session is expired, revoked, or unknown.");
    var result = provisioning.Unlink(validation.Value.Item3.AccountId, linkId);
    return result.Ok
        ? Wire.Ok(req, new { unlinked = true, revokedSessions = result.RevokedSessions })
        : result.Code switch
        {
            "last_provider" => Wire.Err(req, 409, WireCodes.ConflictLink,
                "This is the account's last active provider and cannot be unlinked.",
                Wire.OwnConflictResource(db, validation.Value.Item3.AccountId, "last_provider_cannot_unlink")),
            "link_already_unlinked" => Wire.Err(req, 409, WireCodes.ConflictLink, "This provider link is already unlinked.",
                new { kind = "link_already_unlinked", linkId }),
            "link_not_found" => Wire.Err(req, 404, WireCodes.NfLink, "No such provider link for this account."),
            _ => Wire.Err(req, 500, WireCodes.ServerInternal, "Unlink failed."),
        };
}).RequireRateLimiting("api");

// ─── account deletion (user-initiated, IRREVERSIBLE) ────────────────────────

// DELETE /v1/account — the account deletes ITSELF. One transaction removes the
// account and EVERY dependent row (sessions, devices, provider links + their
// credential references, native credential, sync profiles); the current
// session dies with the account. An account WITH a native credential must
// present its CURRENT password (possession of a session alone is not enough
// to destroy a password-protected identity — same bar as the password
// change); a provider-only account has nothing else to present, its live
// session IS the proof. Deleting — not merely revoking — frees every UNIQUE
// anchor: the username may be registered again, the same device key may
// enroll again, and the same GitHub identity may bootstrap a FRESH account.
app.MapDelete("/v1/account", async (HttpRequest req) =>
{
    var token = BearerToken(req);
    if (token is null) return Wire.Err(req, 401, WireCodes.AuthSessionExpired, "Authorization: Bearer <session token> required.");
    var validation = sessionService.Validate(token);
    if (validation is null) return Wire.DeadSessionErr(req, sessionService, token, "Session is expired, revoked, or unknown.");

    // Body is OPTIONAL (absent, {} or {"currentPassword":"…"}). A NON-EMPTY
    // body that is not valid JSON is a client protocol error → 400
    // bad_request (§7.1), never a silent "no password given".
    string currentPassword = "";
    using (var reader = new StreamReader(req.Body))
    {
        var raw = await reader.ReadToEndAsync();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            JsonDocument body;
            try { body = JsonDocument.Parse(raw); }
            catch (JsonException) { return Wire.Err(req, 400, WireCodes.BadRequest, "Request body must be valid JSON."); }
            using (body)
            {
                if (body.RootElement.ValueKind != JsonValueKind.Object)
                    return Wire.Err(req, 400, WireCodes.BadRequest, "Request body must be a JSON object.");
                if (body.RootElement.TryGetProperty("currentPassword", out var pw) && pw.ValueKind == JsonValueKind.String)
                    currentPassword = pw.GetString() ?? "";
            }
        }
    }

    try
    {
        var deletedSessions = nativeAuth.DeleteAccount(validation.Value.Item3.AccountId, currentPassword);
        app.Logger.LogInformation("Duluka account {AccountId} deleted by user request", validation.Value.Item3.AccountId);
        return Wire.Ok(req, new { deleted = true, deletedSessions });
    }
    catch (InvalidOperationException ex) when (ex.Message is "password_required" or "invalid_credentials")
    {
        return Wire.Err(req, 400, WireCodes.InvalidCredentials, ex.Message == "password_required"
            ? "Current password is required to delete this account."
            : "Current password is incorrect.");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled account deletion failure");
        return Wire.Err(req, 500, WireCodes.ServerInternal, "Internal error — retry the request with the same id.");
    }
}).RequireRateLimiting("api");

// ─── devices ────────────────────────────────────────────────────────────────

app.MapGet("/v1/account/devices", (HttpRequest req) =>
{
    var token = BearerToken(req);
    var validation = token is null ? null : sessionService.Validate(token);
    if (validation is null)
        return Wire.DeadSessionErr(req, sessionService, token, "Session is expired, revoked, or unknown.");
    var devices = db.DevicesForAccount(validation.Value.Item3.AccountId)
        .Select(d => new { d.DeviceId, d.DeviceName, d.CreatedAt, d.LastSeenAt, d.RevokedAt });
    return Wire.Ok(req, new { devices });
}).RequireRateLimiting("api");

app.MapPost("/v1/account/devices/{deviceId}/revoke", (string deviceId, HttpRequest req) =>
{
    var token = BearerToken(req);
    var validation = token is null ? null : sessionService.Validate(token);
    if (validation is null)
        return Wire.DeadSessionErr(req, sessionService, token, "Session is expired, revoked, or unknown.");
    var (_, device, account) = validation.Value;
    var target = db.GetDevice(deviceId);
    if (target is null || target.AccountId != account.AccountId)
        return Wire.Err(req, 404, WireCodes.NfDevice, "No such device for this account.");
    var revoked = db.RevokeDevice(deviceId, DateTimeOffset.UtcNow);
    return Wire.Ok(req, new { revoked = true, sessionsRevoked = revoked });
}).RequireRateLimiting("api");

// ─── local operator console (/admin) ────────────────────────────────────
AdminConsole.Map(app, db, adminLogs);

app.Start();
WarnIfExternalBind(app.Urls);
app.WaitForShutdown();

/// <summary>v0 stays loopback-only BY DEFAULT (appsettings "Urls" pin + H-8).
/// Widening is a deliberate deployment decision (DULUKA_Urls / -Public), so a
/// non-loopback bind announces itself loudly at startup — plain HTTP, the
/// /admin console and per-IP rate limits are all exposed with it.</summary>
static void WarnIfExternalBind(IEnumerable<string> urls)
{
    var external = new List<string>();
    foreach (var u in urls)
    {
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) continue;
        var host = uri.Host.Trim('[', ']').ToLowerInvariant();
        // only explicit loopback hosts stay private; anything else (0.0.0.0,
        // *, +, a LAN IP, a hostname) is reachable from other machines
        if (host is not ("127.0.0.1" or "localhost" or "::1")) external.Add(u);
    }
    if (external.Count == 0) return;
    Console.WriteLine();
    Console.WriteLine("================================================================");
    Console.WriteLine("  [!] EXTERNAL BIND — other machines can now reach this server:");
    foreach (var u in external) Console.WriteLine("      " + u);
    Console.WriteLine("  [!] v0 speaks PLAIN HTTP (no TLS) and /admin is exposed with it.");
    Console.WriteLine("      - LAN use: fine. Internet use: front it with a TLS reverse proxy / tunnel.");
    Console.WriteLine("      - If clients cannot connect, allow TCP 5115 in Windows Firewall:");
    Console.WriteLine("          netsh advfirewall firewall add rule name=\"Duluka.Server 5115\" dir=in action=allow protocol=TCP localport=5115");
    Console.WriteLine("================================================================");
    Console.WriteLine();
}

/// <summary>§7.1 wire envelope: every response carries ok + reqId echo
/// (client-issued X-ReqId, verbatim); errors add errorCode/httpStatus/
/// retryable per §7.2, successes nest the payload under `resource`.</summary>
internal static class Wire
{
    public static IResult Err(HttpRequest req, int status, string code, string message, object? conflict = null) =>
        Results.Json(new
        {
            ok = false,
            reqId = ReqId(req),
            errorCode = code,
            httpStatus = status,
            retryable = status >= 500 || status == 429,
            conflict,
            message,
        }, statusCode: status);

    /// <summary>§6.2/M-10: a 409 conflict body must carry the CURRENT server
    /// resource so the client can re-apply or drop deterministically. The
    /// resource is the CALLER's own state only — a foreign account's rows are
    /// never revealed (§6.3 cross-tenant rule). Payload shape is the owning
    /// fix's freedom (§7.1); the SEMANTIC (current resource revealed) is frozen.</summary>
    public static object OwnConflictResource(Database db, string accountId, string kind) => new
    {
        kind,
        providers = db.LinksForAccount(accountId)
            .Where(l => string.Equals(l.Status, LinkStatus.Active, StringComparison.Ordinal))
            .Select(l => new { l.LinkId, l.ProviderKey, l.Status }),
        hasNativeCredential = db.HasNativeCredential(accountId),
    };

    /// <summary>§7.1 envelope for a failed session validation. The STATUS
    /// follows the root cause, not a fixed 401 (§6.3, M-2): a live session
    /// whose ACCOUNT is suspended answers 403 perm.account_suspended; a
    /// missing token is the plain 401; revoked/expired stay 401 with their
    /// distinguishing §7.2 codes from DeadSessionCode.</summary>
    public static IResult DeadSessionErr(HttpRequest req, SessionService sessions, string? token, string message)
    {
        if (token is null) return Err(req, 401, WireCodes.AuthSessionExpired, message);
        var code = sessions.DeadSessionCode(token);
        return Err(req, code == WireCodes.PermAccountSuspended ? 403 : 401, code, message);
    }

    public static IResult Ok(HttpRequest req, object resource) =>
        Results.Json(new { ok = true, reqId = ReqId(req), resource });

    public static string? ReqId(HttpRequest req)
    {
        if (!req.Headers.TryGetValue("X-ReqId", out var values) || values.Count == 0) return null;
        var v = values[0]?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>Parse the request body as JSON. Returns null for a
    /// malformed/empty/truncated body — a client protocol error must answer the
    /// 400 bad_request envelope (§7.1), never an unhandled 500 with an empty
    /// body. Transport-level stream failures are NOT swallowed here.</summary>
    public static async Task<JsonDocument?> TryParseBodyAsync(HttpRequest req)
    {
        try { return await System.Text.Json.JsonDocument.ParseAsync(req.Body); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}

/// <summary>§7.2 registry codes actually emitted on the wire. Codes outside
/// the frozen registry (provider_*, invalid_*, bad_request) are documented
/// extensions — clients fall back per HTTP class (§7.2 unknown-code rule).</summary>
internal static class WireCodes
{
    public const string AuthSessionExpired = "auth.session_expired";
    public const string AuthSessionRevoked = "auth.session_revoked";
    public const string PermDeviceRemoved = "perm.device_removed";
    public const string NfLink = "nf.link";
    public const string NfDevice = "nf.device";
    public const string ConflictLink = "conflict.link_conflict";
    public const string ServerInternal = "server.internal";
    public const string ServerRateLimited = "server.rate_limited";
    public const string ProviderReserved = "provider_reserved";
    public const string BadRequest = "bad_request";

    // Documented extensions for native credentials (§7.2 extension rule).
    // invalid_credentials is deliberately GENERIC — unknown username and wrong
    // password are indistinguishable on the wire (no account enumeration).
    public const string InvalidCredentials = "invalid_credentials";
    public const string InvalidUsername = "invalid_username";
    public const string InvalidPassword = "invalid_password";
    public const string NativeCredentialAbsent = "native_credential_absent";

    // Documented extensions for the profile surface (§7.2 extension rule).
    public const string InvalidDisplayName = "invalid_display_name";
    public const string InvalidProfileImage = "invalid_profile_image";

    public const string ConflictUsernameTaken = "conflict.username_taken";
    public const string PermAccountSuspended = "perm.account_suspended";
}
