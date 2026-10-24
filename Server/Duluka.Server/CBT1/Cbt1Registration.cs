using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Duluka.Server.Auth;
using Duluka.Server.Data;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;

namespace Duluka.Server.CBT1;

// Separate recruitment database. This does not change Duluka Account schema
// or grant applicants any account/session privileges.
public sealed class Cbt1Store
{
    private readonly string _connectionString;

    public Cbt1Store(IConfiguration configuration)
    {
        var path = configuration["Cbt1:DatabasePath"] ?? "AppData/CBT1Applications.db";
        if (!Path.IsPathRooted(path))
            path = Path.GetFullPath(path, AppContext.BaseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 15
        }.ToString();

        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Cbt1Applications (
                Id TEXT NOT NULL PRIMARY KEY,
                AccountId TEXT,
                Username TEXT,
                Alias TEXT NOT NULL,
                Email TEXT NOT NULL COLLATE NOCASE UNIQUE,
                WindowsVersion TEXT NOT NULL,
                GpuVendor TEXT NOT NULL,
                TestingFocus TEXT NOT NULL,
                Notes TEXT NOT NULL,
                AppliedUtc TEXT NOT NULL,
                Decision TEXT NOT NULL DEFAULT 'pending'
            );
            CREATE INDEX IF NOT EXISTS IX_Cbt1Applications_AppliedUtc
                ON Cbt1Applications(AppliedUtc DESC);
            """;
        command.ExecuteNonQuery();
        // Additive upgrade for any databases created by the first CBT1 scaffold.
        // Legacy applications remain unlinked; no account is guessed from email.
        using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(Cbt1Applications)";
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = columns.ExecuteReader())
            while (reader.Read()) found.Add(reader.GetString(1));
        if (!found.Contains("AccountId"))
        {
            using var add = connection.CreateCommand();
            add.CommandText = "ALTER TABLE Cbt1Applications ADD COLUMN AccountId TEXT";
            add.ExecuteNonQuery();
        }
        if (!found.Contains("Username"))
        {
            using var add = connection.CreateCommand();
            add.CommandText = "ALTER TABLE Cbt1Applications ADD COLUMN Username TEXT";
            add.ExecuteNonQuery();
        }
        using var unique = connection.CreateCommand();
        unique.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Cbt1Applications_AccountId
            ON Cbt1Applications(AccountId) WHERE AccountId IS NOT NULL;
            """;
        unique.ExecuteNonQuery();
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    // Duplicate e-mails get the same public response to prevent enumeration.
    public bool Apply(Cbt1Application a)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO Cbt1Applications
              (Id,AccountId,Username,Alias,Email,WindowsVersion,GpuVendor,TestingFocus,Notes,AppliedUtc,Decision)
            VALUES ($id,$account,$username,$alias,$email,$win,$gpu,$focus,$notes,$now,'pending');
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$account", a.AccountId);
        cmd.Parameters.AddWithValue("$username", a.Username);
        cmd.Parameters.AddWithValue("$alias", a.Alias);
        cmd.Parameters.AddWithValue("$email", a.Email);
        cmd.Parameters.AddWithValue("$win", a.WindowsVersion);
        cmd.Parameters.AddWithValue("$gpu", a.GpuVendor);
        cmd.Parameters.AddWithValue("$focus", a.TestingFocus);
        cmd.Parameters.AddWithValue("$notes", a.Notes);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() == 1;
    }

    public object? ForAccount(string accountId)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT Id,Username,Alias,Email,WindowsVersion,GpuVendor,TestingFocus,
                   Notes,AppliedUtc,Decision FROM Cbt1Applications
            WHERE AccountId=$account LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$account", accountId);
        using var rows = cmd.ExecuteReader();
        if (!rows.Read()) return null;
        return new
        {
            id = rows.GetString(0),
            username = rows.IsDBNull(1) ? "" : rows.GetString(1),
            alias = rows.GetString(2),
            email = rows.GetString(3),
            windowsVersion = rows.GetString(4),
            gpuVendor = rows.GetString(5),
            testingFocus = rows.GetString(6),
            notes = rows.GetString(7),
            appliedUtc = rows.GetString(8),
            decision = rows.GetString(9)
        };
    }

    public List<object> Recent(int limit)
    {
        var items = new List<object>();
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT Id,Username,Alias,Email,WindowsVersion,GpuVendor,TestingFocus,
                   Notes,AppliedUtc,Decision FROM Cbt1Applications
            ORDER BY AppliedUtc DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
            items.Add(new
            {
                id = rows.GetString(0),
                username = rows.IsDBNull(1) ? "" : rows.GetString(1),
                alias = rows.GetString(2),
                email = rows.GetString(3),
                windowsVersion = rows.GetString(4),
                gpuVendor = rows.GetString(5),
                testingFocus = rows.GetString(6),
                notes = rows.GetString(7),
                appliedUtc = rows.GetString(8),
                decision = rows.GetString(9)
            });
        return items;
    }

    public bool Decide(string id, string decision)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Cbt1Applications SET Decision=$decision WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$decision", decision);
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool Delete(string id)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM Cbt1Applications WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1;
    }
}

public sealed record Cbt1Application(
    string AccountId, string Username, string Alias, string Email, string WindowsVersion,
    string GpuVendor, string TestingFocus, string Notes);

internal sealed class Cbt1ApplyPayload
{
    public string? Alias { get; set; }
    public string? Email { get; set; }
    public string? WindowsVersion { get; set; }
    public string? GpuVendor { get; set; }
    public string? TestingFocus { get; set; }
    public string? Notes { get; set; }
    public string? Website { get; set; } // invisible honeypot field
    public bool Consent { get; set; }
}

internal sealed class Cbt1DecisionPayload
{
    public string? Decision { get; set; }
}

public static class Cbt1Registration
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private static bool Accepting(IConfiguration config) =>
        string.Equals(config["Cbt1:Enabled"], "true", StringComparison.OrdinalIgnoreCase);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { ok = false, errorCode = code, httpStatus = status, message },
            statusCode: status);

    // Admin token must be a random >= 32-character secret supplied through
    // DULUKA_Cbt1__AdminToken. Never store it in appsettings or the browser.
    private static bool IsAdmin(HttpRequest request, IConfiguration config)
    {
        var expected = config["Cbt1:AdminToken"];
        if (string.IsNullOrWhiteSpace(expected) || expected.Length < 32)
            return false;
        const string prefix = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var supplied = header[prefix.Length..].Trim();
        if (supplied.Length is < 32 or > 512)
            return false;
        var knownHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var sentHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(knownHash, sentHash);
    }

    private static async Task<T?> ReadLimited<T>(HttpRequest request, CancellationToken ct)
    {
        // Read a bounded number of bytes even if Content-Length is omitted.
        var buffer = new byte[8193];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await request.Body.ReadAsync(buffer.AsMemory(length), ct);
            if (count == 0) break;
            length += count;
        }
        if (length > 8192) throw new InvalidDataException("Request too large");
        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), JsonOptions);
    }

    private static (string AccountId, string Username)? AuthenticatedTester(
        HttpRequest request, SessionService sessions, Database accounts)
    {
        const string prefix = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var validation = sessions.Validate(header[prefix.Length..].Trim());
        if (validation is null) return null;
        var accountId = validation.Value.Account.AccountId;
        var name = accounts.FindNativeCredentialByAccount(accountId)?.UsernameDisplay;
        if (string.IsNullOrWhiteSpace(name)) return null;
        return (accountId, name);
    }

    private static bool BadText(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) || s.Trim().Length > max ||
        s.Any(char.IsControl);

    public static void MapCbt1(this WebApplication app)
    {
        var group = app.MapGroup("/v1/cbt1").RequireCors("cbt1-web");

        group.MapGet("/status", (HttpContext context, IConfiguration config) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { ok = true, accepting = Accepting(config), cycle = "CBT1" });
        });

        group.MapGet("/me", (
            HttpContext context, SessionService sessions, Database accounts, Cbt1Store store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var tester = AuthenticatedTester(context.Request, sessions, accounts);
            if (tester is null) return Error(401, "cbt1.login_required", "Sign in with a username.");
            return Results.Json(new { ok = true, username = tester.Value.Username,
                application = store.ForAccount(tester.Value.AccountId) });
        }).RequireRateLimiting("api");

        group.MapPost("/apply", async (
            HttpContext context, IConfiguration config, Cbt1Store store,
            SessionService sessions, Database accounts) =>
        {
            if (!Accepting(config))
                return Error(503, "cbt1.closed", "CBT1 applications are not open.");
            var tester = AuthenticatedTester(context.Request, sessions, accounts);
            if (tester is null)
                return Error(401, "cbt1.login_required", "Sign in with a username.");
            if (!context.Request.HasJsonContentType())
                return Error(415, "cbt1.json_required", "JSON request required.");

            Cbt1ApplyPayload? p;
            try { p = await ReadLimited<Cbt1ApplyPayload>(context.Request, context.RequestAborted); }
            catch (InvalidDataException)
            {
                return Error(413, "cbt1.too_large", "Application is too large.");
            }
            catch (JsonException)
            {
                return Error(400, "cbt1.invalid_json", "Malformed application.");
            }

            if (p is null) return Error(400, "cbt1.invalid_json", "Malformed application.");
            if (!string.IsNullOrWhiteSpace(p.Website))
                return Results.Json(new { ok = true, received = true }); // bot decoy
            if (!p.Consent)
                return Error(400, "cbt1.consent", "Consent is required.");
            if (BadText(p.Alias, 40) || BadText(p.Email, 254) ||
                p.Notes is { Length: > 500 } ||
                (p.Notes?.Any(char.IsControl) ?? false))
                return Error(400, "cbt1.invalid_field", "Check alias, email and notes.");

            var email = p.Email!.Trim().ToLowerInvariant();
            if (!MailAddress.TryCreate(email, out var address) ||
                !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
                return Error(400, "cbt1.invalid_email", "A valid email is required.");
            if (p.WindowsVersion is not ("Windows 10" or "Windows 11") ||
                p.GpuVendor is not ("NVIDIA" or "AMD" or "Intel" or "Mixed" or "Other") ||
                p.TestingFocus is not ("Overlay" or "Capture" or "GPU Profiles" or "DX9 Hook" or "UI" or "General"))
                return Error(400, "cbt1.invalid_choice", "Select supported options.");

            var saved = store.Apply(new Cbt1Application(
                tester.Value.AccountId, tester.Value.Username,
                p.Alias!.Trim(), email, p.WindowsVersion, p.GpuVendor,
                p.TestingFocus, p.Notes?.Trim() ?? ""));
            context.Response.Headers.CacheControl = "no-store";
            // A tester can only see their own application; never reveal another
            // account's details when an email uniqueness rule prevents insertion.
            if (!saved && store.ForAccount(tester.Value.AccountId) is null)
                return Error(409, "cbt1.cannot_apply", "Application could not be saved.");
            return Results.Json(new { ok = true, received = true,
                application = store.ForAccount(tester.Value.AccountId) });
        }).RequireRateLimiting("cbt1-apply");

        group.MapGet("/admin/applications", (
            HttpContext context, IConfiguration config, Cbt1Store store, int? limit) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!IsAdmin(context.Request, config)) return Results.Unauthorized();
            return Results.Json(new { ok = true, applications = store.Recent(limit ?? 100) });
        }).RequireRateLimiting("auth-start");

        group.MapPost("/admin/applications/{id}/decision", async (
            string id, HttpContext context, IConfiguration config, Cbt1Store store) =>
        {
            if (!IsAdmin(context.Request, config)) return Results.Unauthorized();
            if (id.Length != 32 || !id.All(Uri.IsHexDigit))
                return Error(400, "cbt1.bad_id", "Invalid application identifier.");
            Cbt1DecisionPayload? payload;
            try { payload = await ReadLimited<Cbt1DecisionPayload>(context.Request, context.RequestAborted); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                return Error(400, "cbt1.invalid_json", "Invalid decision.");
            }
            if (payload?.Decision is not ("pending" or "approved" or "waitlist" or "rejected"))
                return Error(400, "cbt1.bad_decision", "Unknown decision.");
            return store.Decide(id, payload.Decision) ? Results.Ok(new { ok = true }) : Results.NotFound();
        }).RequireRateLimiting("auth-start");

        group.MapDelete("/admin/applications/{id}", (
            string id, HttpContext context, IConfiguration config, Cbt1Store store) =>
        {
            if (!IsAdmin(context.Request, config)) return Results.Unauthorized();
            if (id.Length != 32 || !id.All(Uri.IsHexDigit))
                return Error(400, "cbt1.bad_id", "Invalid application identifier.");
            return store.Delete(id) ? Results.Ok(new { ok = true }) : Results.NotFound();
        }).RequireRateLimiting("auth-start");
    }
}
