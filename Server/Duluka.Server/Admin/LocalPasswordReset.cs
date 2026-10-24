using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Duluka.Server.Data;
using Duluka.Server.Domain;
using Duluka.Server.Security;

namespace Duluka.Server.Admin;

/// <summary>
/// LocalAdmin-only password recovery. The owner retrieves the 48-character
/// single-use code from an ACL-protected local file, NEVER from the HTTP API.
/// A successful reset changes the PBKDF2 verifier and revokes all sessions
/// atomically. The code rotates after every success.
/// </summary>
internal static class LocalPasswordReset
{
    private static readonly object Gate = new();
    private static DateTimeOffset _windowStart = DateTimeOffset.UtcNow;
    private static int _badCodes;

    public static void Map(WebApplication app, Database db)
    {
        var secretFile = app.Configuration["LocalAdmin:ResetCodeFile"];
        if (string.IsNullOrWhiteSpace(secretFile) || !Path.IsPathFullyQualified(secretFile))
            throw new InvalidOperationException("LocalAdmin:ResetCodeFile must specify a secure absolute local file.");

        app.MapPost("/admin/api/accounts/{id}/reset-password", async (string id, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (!System.Net.IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress ?? System.Net.IPAddress.None) ||
                ctx.Request.Host.Host != "127.0.0.1" || ctx.Request.Host.Port != 5117 ||
                ctx.Request.Headers["X-Admin-Request"].ToString() != "1" ||
                ctx.Request.Headers.Origin.Count != 1 ||
                ctx.Request.Headers.Origin.ToString() != "http://127.0.0.1:5117")
                return Wire.Err(ctx.Request, 403, "admin.reset.local_only",
                    "Password recovery requires the local operator dashboard.");

            if (!ctx.Request.HasJsonContentType() || ctx.Request.ContentLength is > 4096)
                return Wire.Err(ctx.Request, 400, WireCodes.BadRequest, "Invalid reset request.");

            string? code;
            string? password;
            string? username;
            try
            {
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                var buffer = new char[4097];
                var read = await reader.ReadBlockAsync(buffer.AsMemory(), ctx.RequestAborted);
                if (read > 4096) return Wire.Err(ctx.Request, 413, WireCodes.BadRequest, "Request too large.");
                using var body = JsonDocument.Parse(new string(buffer, 0, read));
                if (body.RootElement.ValueKind != JsonValueKind.Object)
                    return Wire.Err(ctx.Request, 400, WireCodes.BadRequest, "Invalid reset request.");
                static string? Field(JsonElement obj, string key) =>
                    obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() : null;
                code = Field(body.RootElement, "recoveryCode");
                password = Field(body.RootElement, "newPassword");
                username = Field(body.RootElement, "confirmUsername");
            }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
            {
                return Wire.Err(ctx.Request, 400, WireCodes.BadRequest, "Invalid reset request.");
            }

            if (code is null || code.Length != 48 ||
                !SecretsCodeFormat(code) || password is null ||
                !UsernamePolicy.IsValidPassword(password) ||
                string.IsNullOrWhiteSpace(username) || username.Length > 32)
                return Wire.Err(ctx.Request, 400, WireCodes.BadRequest,
                    "Enter the 48-character recovery code, account username and a valid new password.");

            lock (Gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _windowStart > TimeSpan.FromMinutes(5))
                {
                    _windowStart = now;
                    _badCodes = 0;
                }
                if (_badCodes >= 5)
                    return Wire.Err(ctx.Request, 429, "admin.reset.locked", "Too many recovery attempts. Retry later.");

                string expected;
                try { expected = File.ReadAllText(secretFile).Trim(); }
                catch (IOException)
                {
                    return Wire.Err(ctx.Request, 503, "admin.reset.disabled",
                        "Recovery code file is unavailable. Restart Local Admin.");
                }

                var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
                var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
                if (expected.Length != 48 ||
                    !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                {
                    _badCodes++;
                    return Wire.Err(ctx.Request, 403, "admin.reset.invalid_code", "Invalid recovery code.");
                }

                var credential = db.FindNativeCredentialByAccount(id);
                if (credential is null)
                    return Wire.Err(ctx.Request, 404, "admin.reset.no_native_login",
                        "Account has no username/password credential to reset.");
                if (!string.Equals(username.Trim(), credential.UsernameDisplay, StringComparison.OrdinalIgnoreCase))
                    return Wire.Err(ctx.Request, 400, "admin.reset.username_mismatch",
                        "Confirmation username does not match this account.");

                var hash = Secrets.HashPassword(password);
                if (!db.AdminResetNativePassword(id, hash))
                    return Wire.Err(ctx.Request, 404, "nf.account", "Account credential no longer exists.");

                // Rotate after success. New code stays on the owner's machine
                // and is never returned in HTTP responses or log entries.
                var next = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
                File.WriteAllText(secretFile, next, new UTF8Encoding(false));
                _badCodes = 0;
                app.Logger.LogWarning("Local admin password reset completed for account {AccountId}; sessions revoked.", id);
                return Wire.Ok(ctx.Request, new { reset = true, sessionsRevoked = true });
            }
        }).RequireRateLimiting("auth-start");
    }

    private static bool SecretsCodeFormat(string value) =>
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}
