using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;

namespace Duluka.Server.CBT1;

// Dedicated CBT1 enrollment capabilities. Codes never stored in plaintext;
// every code is single-use, expires, and is redeemable only by a signed-in
// Duluka account. This does NOT create a Duluka account or grant admin rights.
public sealed class Cbt1InviteStore
{
    private readonly string _connectionString;
    public Cbt1InviteStore(IConfiguration config)
    {
        var path = config["Cbt1:DatabasePath"] ?? "AppData/CBT1Applications.db";
        if (!Path.IsPathRooted(path)) path = Path.GetFullPath(path, AppContext.BaseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder {
            DataSource=path, Mode=SqliteOpenMode.ReadWriteCreate, Pooling=true, DefaultTimeout=15
        }.ToString();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Cbt1Invites (
                Id TEXT PRIMARY KEY,
                CodeHash TEXT NOT NULL UNIQUE,
                Label TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                ExpiresUtc TEXT NOT NULL,
                RedeemedUtc TEXT,
                RedeemedBy TEXT,
                RevokedUtc TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Cbt1Invites_RedeemedBy
              ON Cbt1Invites(RedeemedBy) WHERE RedeemedBy IS NOT NULL;
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(code)));

    // Plaintext is sent only once in the local operator's response.
    public (string Id, string Code, string ExpiresUtc) Create(string label, int hours)
    {
        var code = "CBT1-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var id = Guid.NewGuid().ToString("N");
        var created = DateTimeOffset.UtcNow;
        var expires = created.AddHours(hours).ToString("O");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Cbt1Invites(Id,CodeHash,Label,CreatedUtc,ExpiresUtc)
            VALUES ($id,$hash,$label,$now,$expires);
            """;
        cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$hash",Hash(code));
        cmd.Parameters.AddWithValue("$label",label);
        cmd.Parameters.AddWithValue("$now",created.ToString("O"));
        cmd.Parameters.AddWithValue("$expires",expires);
        cmd.ExecuteNonQuery();
        return (id,code,expires);
    }

    public List<object> List()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id,Label,CreatedUtc,ExpiresUtc,RedeemedUtc,RedeemedBy,RevokedUtc
            FROM Cbt1Invites ORDER BY CreatedUtc DESC LIMIT 100;
            """;
        var list = new List<object>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(new {
            id=rd.GetString(0),label=rd.GetString(1),createdUtc=rd.GetString(2),
            expiresUtc=rd.GetString(3),redeemedUtc=rd.IsDBNull(4)?null:rd.GetString(4),
            redeemedBy=rd.IsDBNull(5)?null:rd.GetString(5),
            revokedUtc=rd.IsDBNull(6)?null:rd.GetString(6)
        });
        return list;
    }

    public bool Revoke(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Cbt1Invites SET RevokedUtc=$now WHERE Id=$id AND RevokedUtc IS NULL";
        cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id",id);
        return cmd.ExecuteNonQuery()==1;
    }

    public bool HasAccess(string accountId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM Cbt1Invites WHERE RedeemedBy=$id AND RevokedUtc IS NULL LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id",accountId);
        return cmd.ExecuteScalar() is not null;
    }

    public enum RedeemResult { Success, Invalid, AlreadyInvited }
    public RedeemResult Redeem(string input, string accountId)
    {
        var code = input.Trim().ToUpperInvariant();
        if (code.Length != 69 || !code.StartsWith("CBT1-",StringComparison.Ordinal) ||
            !code.AsSpan(5).ToString().All(Uri.IsHexDigit))
            return RedeemResult.Invalid;
        using var conn = Open();
        using var tx = conn.BeginTransaction(); // SQLite immediate writer lock
        using (var own = conn.CreateCommand())
        {
            own.Transaction=tx;
            own.CommandText = "SELECT 1 FROM Cbt1Invites WHERE RedeemedBy=$id AND RevokedUtc IS NULL LIMIT 1";
            own.Parameters.AddWithValue("$id",accountId);
            if (own.ExecuteScalar() is not null) { tx.Commit(); return RedeemResult.AlreadyInvited; }
        }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE Cbt1Invites
            SET RedeemedBy=$account, RedeemedUtc=$now
            WHERE CodeHash=$hash AND RedeemedBy IS NULL AND RevokedUtc IS NULL
            AND ExpiresUtc>$now;
            """;
        var now=DateTimeOffset.UtcNow.ToString("O");
        cmd.Parameters.AddWithValue("$account",accountId);
        cmd.Parameters.AddWithValue("$now",now);
        cmd.Parameters.AddWithValue("$hash",Hash(code));
        var affected = cmd.ExecuteNonQuery();
        tx.Commit();
        return affected==1 ? RedeemResult.Success : RedeemResult.Invalid;
    }
}

internal static class Cbt1InviteAdminRoutes
{
    private static bool IsLocal(HttpContext ctx, bool mutation)
    {
        if (!System.Net.IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress
              ?? System.Net.IPAddress.None)
            || !string.Equals(ctx.Request.Host.Host,"127.0.0.1",StringComparison.Ordinal)
            || ctx.Request.Host.Port != 5117
            || ctx.Request.Headers["X-Admin-Request"] != "1") return false;
        if (mutation && ctx.Request.Headers.Origin.ToString() != "http://127.0.0.1:5117")
            return false;
        return true;
    }

    internal static void Map(WebApplication app)
    {
        app.MapGet("/admin/api/cbt1/invites",(HttpContext ctx,Cbt1InviteStore store) =>
        {
            ctx.Response.Headers.CacheControl="no-store";
            return IsLocal(ctx,false) ? Results.Json(new {ok=true, invites=store.List()})
                : Results.StatusCode(403);
        });
        app.MapPost("/admin/api/cbt1/invites",async (HttpContext ctx,Cbt1InviteStore store) =>
        {
            ctx.Response.Headers.CacheControl="no-store";
            if (!IsLocal(ctx,true)) return Results.StatusCode(403);
            if (!ctx.Request.HasJsonContentType() || ctx.Request.ContentLength > 4096)
                return Results.BadRequest(new {ok=false,message="JSON required."});
            JsonDocument? doc;
            try { doc=await JsonDocument.ParseAsync(ctx.Request.Body,
                new JsonDocumentOptions{MaxDepth=4},ctx.RequestAborted); }
            catch(JsonException) { return Results.BadRequest(new {ok=false,message="Invalid JSON."}); }
            using (doc)
            {
                var root=doc.RootElement;
                if (root.ValueKind!=JsonValueKind.Object)
                    return Results.BadRequest(new {ok=false,message="Invalid body."});
                var label=root.TryGetProperty("label",out var l)&&l.ValueKind==JsonValueKind.String
                    ? l.GetString()?.Trim():null;
                var hours=root.TryGetProperty("hours",out var h)&&h.ValueKind==JsonValueKind.Number &&
                    h.TryGetInt32(out var num)?num:168;
                if (string.IsNullOrWhiteSpace(label)||label.Length>48 ||
                    label.Any(char.IsControl) || hours<1 || hours>720)
                    return Results.BadRequest(new {ok=false,message="Label and expiry (1-720 hours) required."});
                var item=store.Create(label,hours);
                app.Logger.LogInformation("Local Admin created CBT1 invite {InviteId}",item.Id);
                return Results.Json(new {ok=true, invite=new {id=item.Id,code=item.Code,expiresUtc=item.ExpiresUtc}});
            }
        }).RequireRateLimiting("auth-start");
        app.MapPost("/admin/api/cbt1/invites/{id}/revoke",
            (string id,HttpContext ctx,Cbt1InviteStore store) =>
        {
            ctx.Response.Headers.CacheControl="no-store";
            if (!IsLocal(ctx,true)) return Results.StatusCode(403);
            if (id.Length!=32 || !id.All(Uri.IsHexDigit))
                return Results.BadRequest(new {ok=false,message="Invalid invite ID"});
            var revoked=store.Revoke(id);
            if (revoked) app.Logger.LogWarning("Local Admin revoked CBT1 invite {InviteId}",id);
            return revoked ? Results.Json(new {ok=true,revoked=true}) : Results.NotFound();
        }).RequireRateLimiting("auth-start");

        // Operator-only CBT1 review. These routes exist solely in LocalAdmin
        // mode on 127.0.0.1:5117; the public Funnel still answers 404.
        app.MapGet("/admin/api/cbt1/applications",
            (HttpContext ctx,Cbt1Store store) =>
        {
            ctx.Response.Headers.CacheControl="no-store";
            return IsLocal(ctx,false) ? Results.Json(new {ok=true,applications=store.Recent(100)})
                : Results.StatusCode(403);
        });
        app.MapPost("/admin/api/cbt1/applications/{id}/decision",
            async (string id,HttpContext ctx,Cbt1Store store) =>
        {
            ctx.Response.Headers.CacheControl="no-store";
            if (!IsLocal(ctx,true)) return Results.StatusCode(403);
            if (id.Length!=32 || !id.All(Uri.IsHexDigit))
                return Results.BadRequest(new {ok=false,message="Invalid application ID."});
            if (!ctx.Request.HasJsonContentType() || ctx.Request.ContentLength>4096)
                return Results.BadRequest(new {ok=false,message="JSON required."});
            JsonDocument? doc;
            try {
                doc=await JsonDocument.ParseAsync(ctx.Request.Body,
                    new JsonDocumentOptions{MaxDepth=4},ctx.RequestAborted);
            } catch(JsonException) { return Results.BadRequest(new {ok=false,message="Invalid JSON."}); }
            using(doc)
            {
                var root=doc.RootElement;
                if (root.ValueKind!=JsonValueKind.Object ||
                    !root.TryGetProperty("decision",out var value) ||
                    value.ValueKind!=JsonValueKind.String)
                    return Results.BadRequest(new {ok=false,message="Decision is required."});
                var decision=value.GetString();
                if (decision is not ("pending" or "approved" or "waitlist" or "rejected"))
                    return Results.BadRequest(new {ok=false,message="Unknown decision."});
                var updated=store.Decide(id,decision);
                if (updated) app.Logger.LogWarning(
                    "Local Admin CBT1 application decision updated: {ApplicationId} -> {Decision}",
                    id,decision);
                return updated ? Results.Json(new {ok=true,decision}) : Results.NotFound();
            }
        }).RequireRateLimiting("auth-start");
    }
}
