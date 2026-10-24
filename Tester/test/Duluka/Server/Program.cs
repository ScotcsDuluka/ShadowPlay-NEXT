using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Duluka.Server;
using Duluka.Server.Auth;
using Duluka.Server.Data;
using Duluka.Server.Domain;
using Duluka.Server.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Duluka.Server.Tests;

/// <summary>
/// C/6 auth/security test suite — deterministic, no network. GitHub is a fake
/// HttpMessageHandler; the database is a fresh temp SQLite file per run
/// (self-cleaning, per repo hygiene policy).
/// </summary>
internal static class Program
{
    private static int _passed, _failed;
    private static readonly List<string> Failures = new();

    private static int Main()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine(" Duluka.Server — C/6 auth/security tests");
        Console.WriteLine("==================================================");

        Run("SECRETS-1: PKCE S256 challenge matches RFC 7636 appendix B vector", Test_PkceVector);
        Run("SECRETS-2: redaction destroys secret bodies", Test_Redaction);
        Run("SECRETS-3: session token stored as SHA-256 hash, never raw", Test_TokenHashAtRest);
        Run("FLOW-1: state is single-use — replayed callback rejected", Test_StateSingleUse);
        Run("FLOW-2: expired flow rejected", Test_FlowExpiry);
        Run("LOGIN-1: same provider identity → same account (dedup)", Test_LoginDedup);
        Run("LOGIN-2: unique-anchor race (parallel first login) → one account", Test_UniqueAnchorRace);
        Run("LOGIN-3: different identity → different account", Test_SeparateAccounts);
        Run("LOGIN-4: device key reuse across accounts → device_key_in_use", Test_DeviceKeyCrossAccount);
        Run("SESSION-1: validate/refresh lifecycle", Test_SessionLifecycle);
        Run("SESSION-2: expired session rejected", Test_SessionExpiry);
        Run("SESSION-3: second login on same device supersedes prior (contract 5.2)", Test_SessionSupersede);
        Run("SESSION-4: refresh absolute cap honors configured AbsoluteDays", Test_RefreshAbsoluteCap);
        Run("REVOKE-1: session revoke + revoke-all", Test_SessionRevoke);
        Run("REVOKE-2: device revoke kills its sessions + blocks the key", Test_DeviceRevoke);
        Run("UNLINK-1: last-provider unlink rejected", Test_UnlinkLastProvider);
        Run("UNLINK-2: unlink non-last destroys credential + via-link sessions", Test_UnlinkNonLast);
        Run("UNLINK-3: re-login after unlink returns to the SAME account (same device key)", Test_RelinkAfterUnlink);
        Run("UNLINK-5: returning identity + foreign device key refused, nothing persisted", Test_RelinkForeignDeviceRefused);
        Run("SCHEMA-1: v1 database migrates to v2, rows survive, re-link works", Test_SchemaV2Migration);
        Run("NVIDIA-1: provider reserved, no flow, no hardware identity surface", Test_NvidiaReserved);
        Run("NATIVE-1: register creates account + credential + device (canonical username)", Test_NativeRegister);
        Run("NATIVE-2: duplicate username across case variants rejected (DB unique)", Test_NativeDuplicateUsername);
        Run("NATIVE-3: invalid username/password rejected deterministically", Test_NativeValidation);
        Run("NATIVE-4: login success — case-insensitive username, session valid", Test_NativeLogin);
        Run("NATIVE-4B: same credentials + new device → same account, second device", Test_NativeMultiDeviceLogin);
        Run("NATIVE-5: wrong password and unknown username = SAME generic failure", Test_NativeGenericFailure);
        Run("NATIVE-6: password verifier at rest — PBKDF2, salted, never plaintext", Test_NativeHashAtRest);
        Run("NATIVE-7: second login on same device supersedes prior session (§5.2)", Test_NativeSessionSupersede);
        Run("NATIVE-8: password change — wrong current refused, new password works", Test_NativePasswordChange);
        Run("NATIVE-9: revoked device key refused; cross-account key refused", Test_NativeDeviceGuards);
        Run("NATIVE-12: cross-account device-key register → refused, NO orphan account/credential", Test_NativeRegisterNoOrphanOnDeviceConflict);
        Run("LOGIN-5: unknown identity + foreign device key → refused, NO orphan account/link", Test_LoginOrLinkNoOrphanOnDeviceConflict);
        Run("NATIVE-13: provider-only account adopts username+password, trap released", Test_NativeAdoptPassword);
        Run("SETUP-1: GitHub bootstrap → forced-setup path → native login, SAME account", Test_SetupBootstrapFlow);
        Run("SETUP-2: username immutable — second adoption refused, password change still works", Test_SetupUsernameImmutable);
        Run("SETUP-3: setup refuses taken/invalid usernames and weak passwords", Test_SetupValidation);
        Run("SETUP-4: /me username surface flips only after setup (client gate fact)", Test_SetupMeUsernameSurface);
        Run("NATIVE-10: suspended account cannot login (perm.account_suspended)", Test_NativeSuspended);
        Run("NATIVE-11: GitHub ProviderLink login reaches the SAME native account", Test_NativeProviderSameAccount);
        Run("UNLINK-4: native credential allows unlinking the last provider link", Test_UnlinkWithNativeCredential);
        Run("SCHEMA-2: v2-shaped database migrates to v3 additively, rows survive", Test_SchemaV3Migration);
        Run("DELETE-1: cascade removes account + dependents, every anchor freed", Test_AccountDeletionCascade);
        Run("DELETE-2: password-protected deletion demands the current password", Test_AccountDeletionPasswordProof);
        Run("PROFILE-1: display name update persists (trimmed)", Test_ProfileDisplayName);
        Run("PROFILE-2: profile image data URL persists + clears", Test_ProfileImage);
        Run("PROFILE-3: profile edit preserves account identity + live session", Test_ProfileIdentityPreserved);
        Run("PROFILE-4: ProfilePolicy validation matrix", Test_ProfilePolicyMatrix);
        Run("SCHEMA-3: v3-shaped database migrates to v4 additively, rows survive", Test_SchemaV4Migration);
        RunHttpIntegrationTests();
        RunCwdIndependenceTests();

        Console.WriteLine();
        Console.WriteLine($"--------------------------------------------------");
        Console.WriteLine($" RESULT: {_passed} passed, {_failed} failed, {_passed + _failed} total");
        Console.WriteLine($"--------------------------------------------------");
        if (_failed > 0)
            foreach (var f in Failures)
                Console.WriteLine("  FAILED: " + f);
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        Console.Write($"  {name.PadRight(72)}");
        try { test(); _passed++; Console.WriteLine("PASS"); }
        catch (Exception ex)
        {
            _failed++;
            var msg = ex.Message.Length > 400 ? ex.Message[..400] + "..." : ex.Message;
            Console.WriteLine("FAIL");
            Failures.Add($"{name} — {msg}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    // ─── fixtures ───────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, string> UserDb = new(); // githubId → login

    /// <summary>Fake GitHub: token exchange + /user serving identities from UserDb.</summary>
    private sealed class FakeGitHubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/login/oauth/access_token"))
            {
                var content = request.Content!.ReadAsStringAsync(ct).Result;
                var code = content.Split('&').First(p => p.StartsWith("code="))[5..];
                var json = UserDb.ContainsKey(code)
                    ? $$"""{"access_token":"gho_fake_{{code}}","token_type":"bearer"}"""
                    : """{"error":"bad_verification_code","error_description":"unknown code"}""";
                return Task.FromResult(JsonResponse(json));
            }
            if (url.Contains("/user"))
            {
                var token = request.Headers.Authorization!.ToString(); // "Bearer gho_fake_<code>"
                var ghId = token["gho_fake_".Length..];
                var login = UserDb.TryGetValue(ghId, out var l) ? l : "user" + ghId;
                var json = $$"""{"id":{{ghId}},"login":"{{login}}","email":"{{login}}@example.test"}""";
                return Task.FromResult(JsonResponse(json));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class FixedHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static IConfiguration TestConfig(string dbPath, int absoluteDays = 30, int slidingDays = 7) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "test-client-id",
            ["GitHub:ClientSecret"] = "test-client-secret",
            ["GitHub:RedirectUri"] = "http://localhost:8517/v1/auth/github/callback",
            ["Database:Path"] = dbPath,
            ["Session:AbsoluteDays"] = absoluteDays.ToString(),
            ["Session:SlidingDays"] = slidingDays.ToString(),
        })
        .Build();

    private sealed record Fixture(Database Db, GitHubOAuthService GitHub, AccountProvisioningService Provisioning,
        NativeAuthService Native, SessionService Sessions, string DbPath)
        : IDisposable
    {
        public string RegisterGitHubUser(long ghId, string login)
        {
            UserDb[ghId.ToString()] = login;
            return ghId.ToString();
        }

        public void Dispose()
        {
            // Close the SQLite connection BEFORE the temp file cleanup — the
            // WAL-mode connection holds the file lock otherwise. The server's
            // Database now opens a POOLED connection per operation; disposing
            // the object does not return those pooled handles, so the temp
            // delete below races them. Clear the pool for this data source
            // first — then the delete always succeeds.
            Db.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            Cleanup(DbPath);
        }
    }

    private static Fixture NewFixture(int absoluteDays = 30, int slidingDays = 7)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"duluka-test-{Guid.NewGuid():N}.db");
        var config = TestConfig(dbPath, absoluteDays, slidingDays);
        var db = new Database(dbPath, NullLogger<Database>.Instance);
        db.Bootstrap();
        var github = new GitHubOAuthService(
            new FixedHttpClientFactory(new FakeGitHubHandler()), config, NullLogger<GitHubOAuthService>.Instance);
        return new Fixture(db, github, new AccountProvisioningService(db),
            new NativeAuthService(db), new SessionService(db, config), dbPath);
    }

    private static (string deviceKey, string deviceKeyHash) NewDeviceKey() =>
        (Secrets.NewToken("devk_"), Secrets.Sha256Hex(Secrets.NewToken("devk_")));

    private static void Cleanup(string dbPath)
    {
        // Pooled SQLite handles outlive connection Dispose — return them to
        // the OS before deleting the store, or the delete races the pool
        // (observed as "used by another process" on every migration test).
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var p = dbPath + suffix;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    // ─── tests ──────────────────────────────────────────────────────────────

    private static void Test_PkceVector()
    {
        // RFC 7636 Appendix B — the canonical S256 vector.
        var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert(Secrets.PkceChallenge(verifier) == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            "S256 challenge must match the RFC vector");
    }

    private static void Test_Redaction()
    {
        var r = Secrets.Redact("super-secret-token-value");
        Assert(!r.Contains("secret"), "redacted value must not contain the secret body");
        Assert(r.StartsWith("super-"), "redaction keeps a short prefix for correlation");
        Assert(Secrets.Redact("").Length > 0, "empty redacts to a placeholder");
        Assert(Secrets.FixedTimeEquals("abc", "abc") && !Secrets.FixedTimeEquals("abc", "abd"),
            "constant-time compare works");
    }

    private static void Test_TokenHashAtRest()
    {
        var f = NewFixture();
        try
        {
            var ghId = f.RegisterGitHubUser(1001, "hashy");
            var (link, account, _) = f.Db.UpsertLink(ProviderKeys.GitHub, ghId, null, null);
            var device = f.Db.CreateDevice(account.AccountId, "dev", Secrets.Sha256Hex("devkey-1"));
            var (token, session) = f.Sessions.Create(account.AccountId, device.DeviceId, link.LinkId);

            Assert(!string.Equals(session.SessionTokenHash, token, StringComparison.Ordinal),
                "DB must not hold the raw token");
            Assert(session.SessionTokenHash == Secrets.Sha256Hex(token),
                "DB holds the SHA-256 digest of the token");
            Assert(f.Db.ValidateSession(Secrets.Sha256Hex(token)) is not null, "valid token validates");
        }
        finally { f.Dispose(); }
    }

    private static void Test_StateSingleUse()
    {
        var store = new OAuthFlowStore();
        var flow = store.Put(new OAuthFlow("state-1", "verifier", null, null, "d", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1)));
        Assert(store.Consume(flow.State) is not null, "first consume succeeds");
        Assert(store.Consume(flow.State) is null, "second consume (replay) must fail");
        Assert(store.Consume("never-issued") is null, "unknown state rejected");
    }

    private static void Test_FlowExpiry()
    {
        var store = new OAuthFlowStore();
        store.Put(new OAuthFlow("state-old", "v", null, null, "d", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1)));
        Assert(store.Consume("state-old") is null, "expired flow must not complete");
    }

    private static void Test_LoginDedup()
    {
        var f = NewFixture();
        try
        {
            var ghId = f.RegisterGitHubUser(2001, "dulukafan");
            var (key1, hash1) = NewDeviceKey();
            var (key2, hash2) = NewDeviceKey();

            var id1 = new GitHubIdentity(ProviderKeys.GitHub, ghId, "dulukafan@example.test", "dulukafan");
            var (_, account1, existed1) = f.Provisioning.LoginOrLink(id1, hash1, "pc-1");
            Assert(!existed1, "first login creates the account");

            var (_, account2, existed2) = f.Provisioning.LoginOrLink(id1, hash2, "pc-2");
            Assert(existed2, "second login is an existing account");
            Assert(account1.AccountId == account2.AccountId, "same identity → same AccountId");
            Assert(f.Db.LinksForAccount(account1.AccountId).Count(l => l.Status == LinkStatus.Active) == 1,
                "no duplicate link rows for the same identity");
        }
        finally { f.Dispose(); }
    }

    private static void Test_UniqueAnchorRace()
    {
        var f = NewFixture();
        try
        {
            var ghId = f.RegisterGitHubUser(3001, "racer");
            var barrier = new Barrier(2);
            var results = new ConcurrentBag<string>();
            var errors = new ConcurrentBag<string>();

            // Two concurrent first-logins for the SAME provider identity — the
            // duplicate-account race. Both must converge on ONE account; the
            // loser's orphan account must be cleaned up.
            Parallel.For(0, 2, _ =>
            {
                try
                {
                    barrier.SignalAndWait(TimeSpan.FromSeconds(5));
                    var (_, hash) = NewDeviceKey();
                    var id = new GitHubIdentity(ProviderKeys.GitHub, ghId, "racer@example.test", "racer");
                    var (_, account, existed) = f.Provisioning.LoginOrLink(id, hash, "race-dev");
                    results.Add(account.AccountId + "|" + existed);
                }
                catch (Exception ex) { errors.Add(ex.Message); }
            });

            Assert(errors.IsEmpty, "no exception may escape the race: " + string.Join("; ", errors));
            Assert(results.Count == 2, "both threads completed");
            Assert(results.Select(r => r.Split('|')[0]).Distinct().Count() == 1,
                "both threads converge on ONE AccountId");
        }
        finally { f.Dispose(); }
    }

    private static void Test_SeparateAccounts()
    {
        var f = NewFixture();
        try
        {
            var idA = new GitHubIdentity(ProviderKeys.GitHub, "4001", "a@example.test", "alice");
            var idB = new GitHubIdentity(ProviderKeys.GitHub, "4002", "b@example.test", "bob");
            var (_, hashA) = NewDeviceKey();
            var (_, hashB) = NewDeviceKey();
            var (_, accA, _) = f.Provisioning.LoginOrLink(idA, hashA, "devA");
            var (_, accB, _) = f.Provisioning.LoginOrLink(idB, hashB, "devB");
            Assert(accA.AccountId != accB.AccountId, "different identities → different accounts");
        }
        finally { f.Dispose(); }
    }

    private static void Test_DeviceKeyCrossAccount()
    {
        var f = NewFixture();
        try
        {
            var idA = new GitHubIdentity(ProviderKeys.GitHub, "5001", "a@example.test", "alice");
            var idB = new GitHubIdentity(ProviderKeys.GitHub, "5002", "b@example.test", "bob");
            var (_, hashA) = NewDeviceKey();
            f.Provisioning.LoginOrLink(idA, hashA, "devA");

            var threw = false;
            try { f.Provisioning.LoginOrLink(idB, hashA, "devB"); }
            catch (InvalidOperationException ex) { threw = ex.Message == "device_key_in_use"; }
            Assert(threw, "cross-account device key reuse must throw device_key_in_use");
        }
        finally { f.Dispose(); }
    }

    private static void Test_SessionLifecycle()
    {
        var f = NewFixture();
        try
        {
            var ghId = f.RegisterGitHubUser(6001, "sessiony");
            var id = new GitHubIdentity(ProviderKeys.GitHub, ghId, null, "sessiony");
            var (_, hash) = NewDeviceKey();
            var (link, account, _) = f.Provisioning.LoginOrLink(id, hash, "dev");
            var device = f.Db.DevicesForAccount(account.AccountId).Single();

            var (token, session) = f.Sessions.Create(account.AccountId, device.DeviceId, link.LinkId);
            var validation = f.Sessions.Validate(token);
            Assert(validation is not null, "fresh session validates");
            Assert(validation!.Value.Item1.SessionId == session.SessionId, "same session row");
            Assert(validation.Value.Item2.DeviceId == device.DeviceId, "device binding resolves");

            var refreshed = f.Sessions.Refresh(token);
            Assert(refreshed is not null, "refresh on a live session succeeds");
            Assert(refreshed >= session.ExpiresAt, "sliding refresh never shortens the window");
        }
        finally { f.Dispose(); }
    }

    private static void Test_SessionExpiry()
    {
        var f = NewFixture();
        try
        {
            var ghId = f.RegisterGitHubUser(7001, "expired");
            var id = new GitHubIdentity(ProviderKeys.GitHub, ghId, null, null);
            var (_, hash) = NewDeviceKey();
            var (link, account, _) = f.Provisioning.LoginOrLink(id, hash, "dev");
            var device = f.Db.DevicesForAccount(account.AccountId).Single();

            var token = Secrets.NewToken(SessionService.TokenPrefix);
            f.Db.CreateSession(account.AccountId, device.DeviceId, link.LinkId,
                Secrets.Sha256Hex(token), DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
            Assert(f.Sessions.Validate(token) is null, "expired session must not validate");
        }
        finally { f.Dispose(); }
    }

    private static void Test_SessionSupersede()
    {
        var f = NewFixture();
        try
        {
            // Contract §5.2: exactly ONE active session per DeviceId — a second
            // login on the same device revokes the previous session first.
            var ghId = f.RegisterGitHubUser(12001, "superseder");
            var id = new GitHubIdentity(ProviderKeys.GitHub, ghId, null, null);
            var (_, hash) = NewDeviceKey();
            var (link, account, _) = f.Provisioning.LoginOrLink(id, hash, "one-device");
            var device = f.Db.DevicesForAccount(account.AccountId).Single();

            var (t1, s1) = f.Sessions.Create(account.AccountId, device.DeviceId, link.LinkId);
            var (t2, _) = f.Sessions.Create(account.AccountId, device.DeviceId, link.LinkId);

            Assert(f.Sessions.Validate(t2) is not null, "newest session on the device is live");
            Assert(f.Sessions.Validate(t1) is null, "previous session on the SAME device was superseded");
            var old = f.Db.ValidateSession(Secrets.Sha256Hex(t1));
            Assert(old is null, "superseded session no longer validates");
            Assert(!string.Equals(t1, t2, StringComparison.Ordinal), "tokens are distinct");
            Assert(s1.RevokedAt is null, "sanity: the row captured at creation was live");
        }
        finally { f.Dispose(); }
    }

    private static void Test_RefreshAbsoluteCap()
    {
        // Operators may SHORTEN the absolute TTL (Session:AbsoluteDays) — the
        // refresh cap must follow the configured value, not a hardcoded 30d.
        var f = NewFixture(absoluteDays: 14, slidingDays: 20);
        try
        {
            var ghId = f.RegisterGitHubUser(13001, "capped");
            var id = new GitHubIdentity(ProviderKeys.GitHub, ghId, null, null);
            var (_, hash) = NewDeviceKey();
            var (link, account, _) = f.Provisioning.LoginOrLink(id, hash, "dev");
            var device = f.Db.DevicesForAccount(account.AccountId).Single();

            // Session created with a short explicit expiry so the refresh
            // actually moves the window.
            var token = Secrets.NewToken(SessionService.TokenPrefix);
            var created = DateTimeOffset.UtcNow;
            f.Db.CreateSession(account.AccountId, device.DeviceId, link.LinkId,
                Secrets.Sha256Hex(token), created + TimeSpan.FromDays(1));

            var refreshed = f.Sessions.Refresh(token);
            Assert(refreshed is not null, "refresh on a live session succeeds");
            var cap = created + TimeSpan.FromDays(14);
            Assert(refreshed!.Value <= cap + TimeSpan.FromSeconds(5),
                $"refresh must cap at CreatedAt+AbsoluteDays (14d), got {refreshed}");
            Assert(refreshed.Value > DateTimeOffset.UtcNow,
                "capped refresh still extends the live window");
        }
        finally { f.Dispose(); }
    }

    private static void Test_SessionRevoke()
    {
        var f = NewFixture();
        try
        {
            var ghId = f.RegisterGitHubUser(8001, "revokeme");
            var id = new GitHubIdentity(ProviderKeys.GitHub, ghId, null, null);
            var (_, hash1) = NewDeviceKey();
            var (_, hash2) = NewDeviceKey();
            var (link, account, _) = f.Provisioning.LoginOrLink(id, hash1, "dev-1");
            f.Provisioning.LoginOrLink(id, hash2, "dev-2");
            var device1 = f.Db.DevicesForAccount(account.AccountId).Single(d => d.DeviceName == "dev-1");
            var device2 = f.Db.DevicesForAccount(account.AccountId).Single(d => d.DeviceName == "dev-2");

            // Independent sessions live on independent devices (contract §5.2:
            // one ACTIVE session per device — two sessions on one device is
            // impossible by construction now).
            var t1 = f.Sessions.Create(account.AccountId, device1.DeviceId, link.LinkId).Token;
            var t2 = f.Sessions.Create(account.AccountId, device2.DeviceId, link.LinkId).Token;

            Assert(f.Sessions.Revoke(t1, "user-logout"), "own-session revoke succeeds");
            Assert(f.Sessions.Validate(t1) is null, "revoked session dead");
            Assert(f.Sessions.Validate(t2) is not null, "other device's session survives");
            Assert(f.Sessions.DeadSessionCode(t1) == "auth.session_revoked",
                "revoked session reports auth.session_revoked (§7.2 registry)");

            Assert(f.Sessions.RevokeAll(account.AccountId, "logout-all") >= 1, "revoke-all counts");
            Assert(f.Sessions.Validate(t2) is null, "revoke-all kills the rest");
        }
        finally { f.Dispose(); }
    }

    private static void Test_DeviceRevoke()
    {
        var f = NewFixture();
        try
        {
            var ghId = f.RegisterGitHubUser(9001, "devicey");
            var id = new GitHubIdentity(ProviderKeys.GitHub, ghId, null, null);
            var (_, hash) = NewDeviceKey();
            var (link, account, _) = f.Provisioning.LoginOrLink(id, hash, "doomed-device");
            var device = f.Db.DevicesForAccount(account.AccountId).Single();
            var (token, _) = f.Sessions.Create(account.AccountId, device.DeviceId, link.LinkId);
            Assert(f.Sessions.Validate(token) is not null, "session alive before revoke");

            var sessionsRevoked = f.Db.RevokeDevice(device.DeviceId, DateTimeOffset.UtcNow);
            Assert(sessionsRevoked >= 1, "device revoke kills its sessions");
            Assert(f.Sessions.Validate(token) is null, "session dead after device revoke");

            // Revoked device key can never silently re-register.
            var rethrew = false;
            try { f.Provisioning.LoginOrLink(id, hash, "doomed-device"); }
            catch (InvalidOperationException ex) { rethrew = ex.Message == "device_revoked"; }
            Assert(rethrew, "revoked device key must throw device_revoked");
        }
        finally { f.Dispose(); }
    }

    private static void Test_UnlinkLastProvider()
    {
        var f = NewFixture();
        try
        {
            var ghId = f.RegisterGitHubUser(10001, "lonely");
            var id = new GitHubIdentity(ProviderKeys.GitHub, ghId, null, null);
            var (_, hash) = NewDeviceKey();
            var (link, account, _) = f.Provisioning.LoginOrLink(id, hash, "dev");

            var result = f.Provisioning.Unlink(account.AccountId, link.LinkId);
            Assert(!result.Ok && result.Code == "last_provider",
                "unlinking the only provider must be rejected with last_provider");
            Assert(f.Db.GetLink(link.LinkId)!.Status == LinkStatus.Active, "link stays active");
        }
        finally { f.Dispose(); }
    }

    private static void Test_UnlinkNonLast()
    {
        var f = NewFixture();
        try
        {
            var gh1 = f.RegisterGitHubUser(11001, "two-providers");
            var idGh = new GitHubIdentity(ProviderKeys.GitHub, gh1, null, null);
            var (_, hash) = NewDeviceKey();
            var (ghLink, account, _) = f.Provisioning.LoginOrLink(idGh, hash, "dev");
            var device = f.Db.DevicesForAccount(account.AccountId).Single();

            // A second provider identity for the SAME account (the scenario the
            // link flow implements).
            var futureLink = new AccountProviderLink(
                Secrets.NewToken("duluka_link_"), account.AccountId, "discord", "discord-42",
                null, LinkStatus.Active, DateTimeOffset.UtcNow, null);
            f.Db.AddLink(futureLink);

            // Two sessions on two devices (§5.2: one active session per device).
            var (_, hash2) = NewDeviceKey();
            f.Db.CreateDevice(account.AccountId, "dev-2", hash2);
            var device2 = f.Db.DevicesForAccount(account.AccountId).Single(d => d.DeviceName == "dev-2");
            var (token, _) = f.Sessions.Create(account.AccountId, device.DeviceId, ghLink.LinkId);
            var (tokenViaFuture, _) = f.Sessions.Create(account.AccountId, device2.DeviceId, futureLink.LinkId);
            Assert(f.Sessions.Validate(tokenViaFuture) is not null, "via-link session alive before unlink");

            var result = f.Provisioning.Unlink(account.AccountId, futureLink.LinkId);
            Assert(result.Ok, "unlink succeeds while another provider remains");
            Assert(result.RevokedSessions >= 1, "sessions issued via the unlinked link are revoked");
            Assert(f.Sessions.Validate(tokenViaFuture) is null, "via-link session dead after unlink");
            Assert(f.Sessions.Validate(token) is not null, "sessions from other links survive");

            var after = f.Db.GetLink(futureLink.LinkId)!;
            Assert(after.Status == LinkStatus.Unlinked, "link is soft-unlinked");
            Assert(f.Db.LinksForAccount(account.AccountId).Count(l => l.Status == LinkStatus.Active) == 1,
                "exactly one active link remains");
        }
        finally { f.Dispose(); }
    }

    private static void Test_RelinkAfterUnlink()
    {
        // Contract §6.2 (REVISED after a real-world failure report):
        // unlinking a non-last provider must leave the provider identity
        // RE-LINKABLE — the v1 table-level UNIQUE anchored Unlinked rows too,
        // so every later login with that identity 500'd forever; the partial
        // unique index (Active rows only) fixed the 500. But the OLD
        // convergence ("fresh account") was its own bug: unlink → logout →
        // login-again is a normal user journey, and the client arrives with
        // the SAME device key (it persists in the local store across
        // logout). The old contract 409'd that key forever ("This device
        // key is already bound to another account" — while the identity was
        // linked to NOTHING) and, with a minted key, silently stranded the
        // old account. A returning identity must re-enter ITS OWN account.
        var f = NewFixture();
        try
        {
            var ghId = f.RegisterGitHubUser(14001, "relinker");
            var id = new GitHubIdentity(ProviderKeys.GitHub, ghId, null, null);
            var (_, hash) = NewDeviceKey();
            var (ghLink, account, _) = f.Provisioning.LoginOrLink(id, hash, "dev");

            var second = new AccountProviderLink(
                Secrets.NewToken("duluka_link_"), account.AccountId, "discord", "discord-77",
                null, LinkStatus.Active, DateTimeOffset.UtcNow, null);
            f.Db.AddLink(second);

            Assert(f.Provisioning.Unlink(account.AccountId, ghLink.LinkId).Ok, "non-last unlink succeeds");

            // The SAME GitHub identity + the SAME device key (the real
            // client's state after logout) log in again — must land back on
            // the SAME account with a fresh link period, never throw.
            var (newLink, sameAccount, existed) = f.Provisioning.LoginOrLink(id, hash, "dev");
            Assert(existed, "returning identity re-enters the existing account");
            Assert(sameAccount.AccountId == account.AccountId,
                "same account — the old home is not stranded");
            Assert(newLink.Status == LinkStatus.Active, "new link period is active");
            Assert(newLink.LinkId != ghLink.LinkId, "LinkId is minted fresh (immutable history preserved)");
            Assert(newLink.AccountId == account.AccountId, "new link period belongs to the same account");

            var old = f.Db.GetLink(ghLink.LinkId)!;
            Assert(old.Status == LinkStatus.Unlinked, "old link row stays Unlinked (audit history)");
            Assert(f.Db.ActiveLinkCount(account.AccountId) == 2,
                "account holds github (again) + its remaining provider");

            // The device key was REUSED, not duplicated: still exactly one
            // live device row behind that hash, bound to the same account.
            var device = f.Db.FindDeviceByKeyHash(hash);
            Assert(device is not null && device.AccountId == account.AccountId,
                "device binding survives the unlink → login round trip");
        }
        finally { f.Dispose(); }
    }

    private static void Test_RelinkForeignDeviceRefused()
    {
        // A returning (unlinked-history) identity may not be dragged onto a
        // device key bound to a DIFFERENT account: refused BEFORE any
        // persistence — no new link period, no account churn. Its own key
        // still gets it home.
        var f = NewFixture();
        try
        {
            // Identity B bootstraps account B with its own device key.
            var (keyB, hashB) = NewDeviceKey();
            var ghB = f.RegisterGitHubUser(9200, "returnee");
            var identityB = new GitHubIdentity(ProviderKeys.GitHub, ghB, null, null);
            var (linkB, accountB, _) = f.Provisioning.LoginOrLink(identityB, hashB, "dev-B");

            // A second account with a different live device key.
            var (_, hashX) = NewDeviceKey();
            var ghY = f.RegisterGitHubUser(9201, "other-home");
            var identityY = new GitHubIdentity(ProviderKeys.GitHub, ghY, null, null);
            var (_, accountX, _) = f.Provisioning.LoginOrLink(identityY, hashX, "dev-X");

            // Unlink B's identity (B keeps a way in via a seeded provider).
            var second = new AccountProviderLink(
                Secrets.NewToken("duluka_link_"), accountB.AccountId, "discord", "discord-88",
                null, LinkStatus.Active, DateTimeOffset.UtcNow, null);
            f.Db.AddLink(second);
            Assert(f.Provisioning.Unlink(accountB.AccountId, linkB.LinkId).Ok, "non-last unlink succeeds");

            // The unlinked identity tries the FOREIGN device key → refused,
            // and NOTHING is persisted (no fresh link period on either home).
            var code = "";
            try { f.Provisioning.LoginOrLink(identityB, hashX, "dev-X"); }
            catch (InvalidOperationException ex) { code = ex.Message; }
            Assert(code == "device_key_in_use",
                $"returning identity + foreign key must be device_key_in_use (got: {code})");
            Assert(f.Db.ActiveLinkCount(accountB.AccountId) == 1,
                "no link period was opened by the refused login (account B)");
            Assert(f.Db.ActiveLinkCount(accountX.AccountId) == 1,
                "no link period was opened by the refused login (account X)");

            // Its OWN key still gets it home — the same account, key reused.
            var (home, homeAccount, existed) = f.Provisioning.LoginOrLink(identityB, hashB, "dev-B");
            Assert(existed && homeAccount.AccountId == accountB.AccountId,
                "own key returns the identity to its own account");
            Assert(home.Status == LinkStatus.Active, "home link period active");
        }
        finally { f.Dispose(); }
    }

    private static void Test_SchemaV2Migration()
    {
        // A v1 database (table-level UNIQUE on the provider anchor) must
        // migrate to v2 on Bootstrap: rows survive, the constraint moves to a
        // partial index over Active rows, and the previously-fatal re-link
        // after unlink works. Use the Database's OpenConn (pooled) instead of
        // raw SqliteConnection to avoid file lock after cleanup.
        var dbPath = Path.Combine(Path.GetTempPath(), $"duluka-mig-{Guid.NewGuid():N}.db");
        try
        {
            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
            {
                raw.Open();
                ExecRaw(raw, """
                    CREATE TABLE SchemaHistory(Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL);
                    CREATE TABLE DulukaAccount(
                        AccountId TEXT PRIMARY KEY, Status TEXT NOT NULL DEFAULT 'Active',
                        DisplayName TEXT, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                    CREATE TABLE AccountProviderLink(
                        LinkId TEXT PRIMARY KEY,
                        AccountId TEXT NOT NULL REFERENCES DulukaAccount(AccountId),
                        ProviderKey TEXT NOT NULL, ProviderUserId TEXT NOT NULL,
                        ProviderEmail TEXT, Status TEXT NOT NULL DEFAULT 'Active',
                        LinkedAt TEXT NOT NULL, UnlinkedAt TEXT,
                        UNIQUE(ProviderKey, ProviderUserId));
                    CREATE TABLE AccountDevice(
                        DeviceId TEXT PRIMARY KEY,
                        AccountId TEXT NOT NULL REFERENCES DulukaAccount(AccountId),
                        DeviceName TEXT NOT NULL, DeviceKeyHash TEXT NOT NULL UNIQUE,
                        CreatedAt TEXT NOT NULL, LastSeenAt TEXT NOT NULL, RevokedAt TEXT);
                    CREATE TABLE AccountSession(
                        SessionId TEXT PRIMARY KEY, SessionTokenHash TEXT NOT NULL UNIQUE,
                        AccountId TEXT NOT NULL REFERENCES DulukaAccount(AccountId),
                        DeviceId TEXT NOT NULL REFERENCES AccountDevice(DeviceId),
                        IssuedViaLinkId TEXT, CreatedAt TEXT NOT NULL, ExpiresAt TEXT NOT NULL,
                        LastSeenAt TEXT NOT NULL, RevokedAt TEXT, RevokedReason TEXT);
                    CREATE TABLE SyncProfile(
                        AccountId TEXT NOT NULL REFERENCES DulukaAccount(AccountId),
                        ProductKey TEXT NOT NULL, SchemaVersion INTEGER NOT NULL,
                        CurrentVersion INTEGER NOT NULL, DataBlob BLOB,
                        UpdatedByDeviceId TEXT, UpdatedAt TEXT NOT NULL, UNIQUE(AccountId, ProductKey));
                    CREATE TABLE CredentialReference(
                        LinkId TEXT PRIMARY KEY REFERENCES AccountProviderLink(LinkId),
                        ProviderKey TEXT NOT NULL, ProviderUserId TEXT NOT NULL,
                        AccessTokenRef TEXT, RefreshTokenRef TEXT, Scopes TEXT,
                        ObtainedAt TEXT NOT NULL, ExpiresAt TEXT);
                    INSERT INTO SchemaHistory VALUES (1, '2026-01-01T00:00:00.0000000+00:00');
                    INSERT INTO DulukaAccount VALUES ('acc-v1', 'Active', NULL,
                        '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                    INSERT INTO AccountProviderLink VALUES ('link-v1', 'acc-v1', 'github', '99001',
                        NULL, 'Active', '2026-01-01T00:00:00.0000000+00:00', NULL);
                    """);
            }

            var migrated = new Database(dbPath, NullLogger<Database>.Instance);
            try
            {
                migrated.Bootstrap();

                // Rows survived the rebuild.
                var link = migrated.GetLink("link-v1");
                Assert(link is not null && link.Status == LinkStatus.Active, "v1 rows survive migration");
                var (same, acc, existed) = migrated.UpsertLink(ProviderKeys.GitHub, "99001", null, null);
                Assert(existed && acc.AccountId == "acc-v1" && same.LinkId == "link-v1",
                    "anchor still resolves to the same account after migration");

                // The re-login path that was FATAL on v1 now converges — and
                // returns to the SAME account (a returning identity re-enters
                // its own home; it must not be re-bootstrapped as a stranger).
                migrated.Unlink("link-v1", DateTimeOffset.UtcNow);
                var (fresh, freshAcc, existed2) = migrated.UpsertLink(ProviderKeys.GitHub, "99001", null, null);
                Assert(existed2 && freshAcc.AccountId == "acc-v1",
                    "re-login after unlink returns to the SAME account (v1: unique violation → 500)");
                Assert(fresh.Status == LinkStatus.Active && fresh.LinkId != "link-v1",
                    "fresh link period active, old row stays history");
            }
            finally { migrated.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

            // SchemaHistory records v2.
            using (var check = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
            {
                check.Open();
                using var cmd = check.CreateCommand();
                cmd.CommandText = "SELECT MAX(Version) FROM SchemaHistory";
                Assert(Convert.ToInt32(cmd.ExecuteScalar()) >= 2, "schema migrated past v2 (current: v3 adds native credentials additively)");
                using var idx = check.CreateCommand();
                idx.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='UX_Link_ActiveIdentity'";
                Assert(Convert.ToInt32(idx.ExecuteScalar()) == 1, "partial unique index present");
                using var tbl = check.CreateCommand();
                tbl.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='AccountProviderLink'";
                var sql = (string?)tbl.ExecuteScalar() ?? "";
                Assert(!sql.Contains("UNIQUE(ProviderKey", StringComparison.OrdinalIgnoreCase),
                    "table-level UNIQUE removed");
            }
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    private static void ExecRaw(Microsoft.Data.Sqlite.SqliteConnection conn, string batch)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = batch;
        cmd.ExecuteNonQuery();
    }

    private static void Test_NvidiaReserved()
    {
        // The reserved provider must exist as a key but expose NO auth surface
        // and NO hardware-identity properties (C/6 hard rule).
        Assert(ProviderKeys.All.Contains(ProviderKeys.Nvidia), "nvidia is a known ProviderKey");
        Assert(!ProviderKeys.IsImplemented(ProviderKeys.Nvidia), "nvidia has no implemented flow");
        Assert(ProviderKeys.IsImplemented(ProviderKeys.GitHub), "github is the implemented provider");

        var type = typeof(NvidiaProviderStub);
        Assert(type.GetProperties().Length == 0, "nvidia stub exposes no identity surface");
    }

    // ─── native username/password credentials ───────────────────────────────

    private static void Test_NativeRegister()
    {
        var f = NewFixture();
        try
        {
            var (key, hash) = NewDeviceKey();
            var (account, device) = f.Native.Register("Alice", "correct-horse-1", hash, "dev");
            Assert(account.DisplayName == "Alice", "display keeps the chosen case");
            Assert(account.Status == AccountStatus.Active, "account starts Active");

            var cred = f.Db.FindNativeCredentialByUsername("alice");
            Assert(cred is not null, "canonical (lowercased) username is the uniqueness anchor");
            Assert(cred!.AccountId == account.AccountId, "credential is owned by the account");
            Assert(f.Db.FindDeviceByKeyHash(hash)!.DeviceId == device.DeviceId, "device bound to the account");

            var (token, _) = f.Sessions.Create(account.AccountId, device.DeviceId, null);
            Assert(f.Sessions.Validate(token) is not null, "native login issues a working Duluka session");
        }
        finally { f.Dispose(); }
    }

    private static void Test_NativeDuplicateUsername()
    {
        var f = NewFixture();
        try
        {
            var (key, hash) = NewDeviceKey();
            f.Native.Register("Alice", "correct-horse-1", hash, "dev");

            foreach (var variant in new[] { "ALICE", "alice", "  alice  " })
            {
                var (k2, h2) = NewDeviceKey();
                var threw = false;
                try { f.Native.Register(variant, "another-pass-1", h2, "dev"); }
                catch (InvalidOperationException ex)
                {
                    threw = ex.Message == "username_taken";
                }
                Assert(threw, $"duplicate variant '{variant}' must be refused with username_taken");
            }

            Assert(f.Db.FindNativeCredentialByUsername("alice") is not null, "original credential intact");
        }
        finally { f.Dispose(); }
    }

    private static void Test_NativeValidation()
    {
        var f = NewFixture();
        try
        {
            foreach (var bad in new[] { "", "   ", "ab", "has space", "bad!char", new string('a', 33) })
                Assert(!UsernamePolicy.IsValidFormat(bad), $"'{Truncate(bad, 12)}' must be an invalid username");

            Assert(UsernamePolicy.IsValidFormat("a.B_c-9"), "charset letters/digits/._- valid");
            foreach (var pw in new[] { "", "short1a", new string('x', 129) })
                Assert(!UsernamePolicy.IsValidPassword(pw), "short/empty/overlong passwords rejected");
            Assert(UsernamePolicy.IsValidPassword(new string('x', 8)), "8-char password accepted");
        }
        finally { f.Dispose(); }
    }

    private static void Test_NativeLogin()
    {
        var f = NewFixture();
        try
        {
            var (key, hash) = NewDeviceKey();
            var (account, _) = f.Native.Register("Bob_TheBuilder", "correct-horse-1", hash, "dev");

            var (loginAccount, loginDevice) = f.Native.Login("BOB_thebuilder", "correct-horse-1", hash, "dev");
            Assert(loginAccount.AccountId == account.AccountId, "case-insensitive username reaches the SAME account");
            Assert(loginDevice.DeviceId == f.Db.FindDeviceByKeyHash(hash)!.DeviceId, "device reused, not duplicated");
        }
        finally { f.Dispose(); }
    }

    private static void Test_NativeMultiDeviceLogin()
    {
        var f = NewFixture();
        try
        {
            var (_, hashA) = NewDeviceKey();
            var (account, deviceA) = f.Native.Register("Alice", "correct-horse-1", hashA, "device-a");

            var (_, hashB) = NewDeviceKey();
            var (loginAccount, deviceB) = f.Native.Login("alice", "correct-horse-1", hashB, "device-b");

            Assert(!string.Equals(hashA, hashB, StringComparison.Ordinal), "independent device keys differ");
            Assert(loginAccount.AccountId == account.AccountId, "new-device login reaches the original account");
            Assert(deviceB.DeviceId != deviceA.DeviceId, "new-device login creates a distinct device");
            Assert(f.Db.DevicesForAccount(account.AccountId).Count == 2,
                "the account owns both devices");

            var (_, otherAccountDeviceKey) = NewDeviceKey();
            var other = f.Native.Register("Bob", "correct-horse-1", otherAccountDeviceKey, "device-other");
            var devicesBeforeCollision = f.Db.DevicesForAccount(other.Account.AccountId).Count;
            var threw = false;
            try { f.Native.Login("bob", "correct-horse-1", hashA, "device-reused"); }
            catch (InvalidOperationException ex) { threw = ex.Message == "device_key_in_use"; }
            Assert(threw, "a key owned by another account remains guarded");
            Assert(other.Account.AccountId != account.AccountId, "guard test uses a separate account");
            Assert(f.Db.DevicesForAccount(other.Account.AccountId).Count == devicesBeforeCollision,
                "cross-account collision created an orphan device");
            Assert(f.Sessions.Validate("duluka_st_collision") is null,
                "cross-account collision created a session");
        }
        finally { f.Dispose(); }
    }

    private static void Test_NativeGenericFailure()
    {
        var f = NewFixture();
        try
        {
            var (key, hash) = NewDeviceKey();
            f.Native.Register("Carol", "correct-horse-1", hash, "dev");

            var unknownCode = "";
            try { f.Native.Login("nobody-here", "whatever-pass-1", hash, "dev"); }
            catch (InvalidOperationException ex) { unknownCode = ex.Message; }
            Assert(unknownCode == "invalid_credentials", "unknown username = invalid_credentials");

            var wrongPwCode = "";
            try { f.Native.Login("carol", "totally-wrong-1", hash, "dev"); }
            catch (InvalidOperationException ex) { wrongPwCode = ex.Message; }
            Assert(wrongPwCode == "invalid_credentials", "wrong password = invalid_credentials");
            Assert(unknownCode == wrongPwCode, "the two failures must be INDISTINGUISHABLE (no enumeration)");
        }
        finally { f.Dispose(); }
    }

    private static void Test_NativeHashAtRest()
    {
        var f = NewFixture();
        try
        {
            const string password = "correct-horse-1";
            var (key, hash) = NewDeviceKey();
            var (account, _) = f.Native.Register("Dave", password, hash, "dev");
            var cred = f.Db.FindNativeCredentialByAccount(account.AccountId)!;

            Assert(cred.PasswordHash.StartsWith("pbkdf2-sha256$", StringComparison.Ordinal),
                "verifier is PBKDF2-SHA256 (framework primitive), not plaintext");
            Assert(!cred.PasswordHash.Contains(password, StringComparison.OrdinalIgnoreCase),
                "plaintext password never reaches storage");
            Assert(!cred.PasswordHash.Contains(Secrets.Sha256Hex(password)), "not a bare digest either — slow hash only");

            var (k2, h2) = NewDeviceKey();
            var (account2, _) = f.Native.Register("Eve", password, h2, "dev");
            var cred2 = f.Db.FindNativeCredentialByAccount(account2.AccountId)!;
            Assert(cred.PasswordHash != cred2.PasswordHash, "same password → different stored verifier (per-credential salt)");
        }
        finally { f.Dispose(); }
    }

    private static void Test_NativeSessionSupersede()
    {
        var f = NewFixture();
        try
        {
            var (key, hash) = NewDeviceKey();
            var (account, device) = f.Native.Register("Fay", "correct-horse-1", hash, "dev");

            var (token1, _) = f.Sessions.Create(account.AccountId, device.DeviceId, null);
            var (token2, _) = f.Sessions.Create(account.AccountId, device.DeviceId, null);
            Assert(f.Sessions.Validate(token1) is null, "second login on the same device supersedes the first (§5.2)");
            Assert(f.Sessions.Validate(token2) is not null, "the newest session stays valid");
        }
        finally { f.Dispose(); }
    }

    private static void Test_NativePasswordChange()
    {
        var f = NewFixture();
        try
        {
            const string oldPassword = "correct-horse-1";
            const string newPassword = "tin-foil-chapeau-9";
            var (key, hash) = NewDeviceKey();
            var (account, device) = f.Native.Register("Frank", oldPassword, hash, "dev");

            var wrongCurrent = false;
            try { f.Native.ChangePassword(account.AccountId, "not-the-current-1", Secrets.HashPassword(newPassword)); }
            catch (InvalidOperationException ex) { wrongCurrent = ex.Message == "invalid_credentials"; }
            Assert(wrongCurrent, "wrong current password must be refused with invalid_credentials");

            f.Native.ChangePassword(account.AccountId, oldPassword, Secrets.HashPassword(newPassword));

            var newLoginOk = false;
            try { f.Native.Login("frank", newPassword, hash, "dev"); newLoginOk = true; }
            catch { }
            Assert(newLoginOk, "login with the NEW password works");

            var oldLoginCode = "";
            try { f.Native.Login("frank", oldPassword, hash, "dev"); }
            catch (InvalidOperationException ex) { oldLoginCode = ex.Message; }
            Assert(oldLoginCode == "invalid_credentials", "login with the OLD password fails");
        }
        finally { f.Dispose(); }
    }

    private static void Test_NativeDeviceGuards()
    {
        var f = NewFixture();
        try
        {
            var (key1, hash1) = NewDeviceKey();
            var (account1, device1) = f.Native.Register("Grace", "correct-horse-1", hash1, "dev");

            f.Db.RevokeDevice(device1.DeviceId, DateTimeOffset.UtcNow);
            var revokedCode = "";
            try { f.Native.Login("grace", "correct-horse-1", hash1, "dev"); }
            catch (InvalidOperationException ex) { revokedCode = ex.Message; }
            Assert(revokedCode == "device_revoked", "a revoked device key can never silently re-register");

            var (key2, hash2) = NewDeviceKey();
            f.Native.Register("Henry", "another-pass-1", hash2, "dev");
            var crossCode = "";
            try { f.Native.Login("grace", "correct-horse-1", hash2, "dev"); }
            catch (InvalidOperationException ex) { crossCode = ex.Message; }
            Assert(crossCode == "device_key_in_use", "a device key bound to another account is refused");
        }
        finally { f.Dispose(); }
    }

    private static void Test_NativeRegisterNoOrphanOnDeviceConflict()
    {
        var f = NewFixture();
        try
        {
            // Account B already exists and owns a LIVE device key.
            var (keyB, hashB) = NewDeviceKey();
            var (accountB, deviceB) = f.Native.Register("owner-b", "correct-horse-1", hashB, "dev-B");

            // A registration for a NEW account reusing B's device key must be
            // refused BEFORE any account/credential persistence — the 409 is
            // correct, and no orphan account may survive it.
            var code = "";
            try { f.Native.Register("victim-a", "another-pass-1", hashB, "dev-A"); }
            catch (InvalidOperationException ex) { code = ex.Message; }
            Assert(code == "device_key_in_use", $"cross-account key must fail with device_key_in_use (got: {code})");

            Assert(f.Db.FindNativeCredentialByUsername("victim-a") is null,
                "no orphan credential for the refused registration");
            var accountsViaCredential = f.Db.FindNativeCredentialByUsername("owner-b");
            Assert(accountsViaCredential is not null && accountsViaCredential.AccountId == accountB.AccountId,
                "the existing owner account is untouched");

            var devB = f.Db.FindDeviceByKeyHash(hashB)!;
            Assert(devB.DeviceId == deviceB.DeviceId && devB.AccountId == accountB.AccountId,
                "the existing device binding is unchanged");
        }
        finally { f.Dispose(); }
    }

    private static void Test_LoginOrLinkNoOrphanOnDeviceConflict()
    {
        var f = NewFixture();
        try
        {
            // Account B exists (GitHub bootstrap) and owns a LIVE device key.
            var (keyB, hashB) = NewDeviceKey();
            var ghB = f.RegisterGitHubUser(9100, "owner-b");
            var identityB = new GitHubIdentity(ProviderKeys.GitHub, ghB, null, null);
            var (linkB, accountB, existedB) = f.Provisioning.LoginOrLink(identityB, hashB, "dev-B");
            Assert(!existedB, "owner-b bootstrap created its account");
            var deviceB = f.Db.FindDeviceByKeyHash(hashB)!;

            // A DIFFERENT, UNKNOWN GitHub identity tries to log in with B's
            // device key: must be refused BEFORE the anchor upsert — no orphan
            // account/provider-link may survive the 409.
            var ghX = f.RegisterGitHubUser(9101, "unknown-x");
            var identityX = new GitHubIdentity(ProviderKeys.GitHub, ghX, null, null);
            var code = "";
            try { f.Provisioning.LoginOrLink(identityX, hashB, "dev-A"); }
            catch (InvalidOperationException ex) { code = ex.Message; }
            Assert(code == "device_key_in_use",
                $"unknown identity + foreign key must be device_key_in_use (got: {code})");
            Assert(f.Db.FindActiveLink(ProviderKeys.GitHub, ghX) is null,
                "no provider-link row persisted for the refused identity");

            // The owner's link + device binding remain untouched.
            Assert(f.Db.FindActiveLink(ProviderKeys.GitHub, ghB)!.AccountId == accountB.AccountId,
                "owner link intact");
            var devB = f.Db.FindDeviceByKeyHash(hashB)!;
            Assert(devB.AccountId == accountB.AccountId && devB.DeviceId == deviceB.DeviceId,
                "owner device binding intact");
        }
        finally { f.Dispose(); }
    }

    private static void Test_NativeAdoptPassword()
    {
        var f = NewFixture();
        try
        {
            // Provider-only (bootstrapped) account: GitHub link, no credential.
            var (key, hash) = NewDeviceKey();
            var ghId = f.RegisterGitHubUser(9102, "adopter");
            var identity = new GitHubIdentity(ProviderKeys.GitHub, ghId, null, null);
            var (link, account, _) = f.Provisioning.LoginOrLink(identity, hash, "dev");
            Assert(!f.Db.HasNativeCredential(account.AccountId), "starts provider-only");

            // Adopt a password with a chosen username.
            f.Native.SetInitialPassword(account.AccountId, "Adopter_1", "adopted-pass-1");
            Assert(f.Db.HasNativeCredential(account.AccountId), "credential created");

            var loginOk = false;
            try
            {
                f.Native.Login("adopter_1", "adopted-pass-1", hash, "dev");
                loginOk = true;
            }
            catch
            {
            }
            Assert(loginOk, "password login works after adoption (canonical username)");

            // Username uniqueness still enforced across accounts.
            var (k2, h2) = NewDeviceKey();
            var gh2 = f.RegisterGitHubUser(9103, "adopter-two");
            var (link2, account2, _) = f.Provisioning.LoginOrLink(
                new GitHubIdentity(ProviderKeys.GitHub, gh2, null, null), h2, "dev-2");
            var dup = false;
            try { f.Native.SetInitialPassword(account2.AccountId, "adopter_1", "adopted-pass-1"); }
            catch (InvalidOperationException ex) { dup = ex.Message == "username_taken"; }
            Assert(dup, "adopting a taken username must be refused with username_taken");

            // THE TRAP RELEASED: with a password present, the only provider
            // link can be unlinked without locking the account out.
            var result = f.Provisioning.Unlink(account.AccountId, link.LinkId);
            Assert(result.Ok, $"unlink succeeds once a password exists (got {result.Code})");
            var stillIn = false;
            try
            {
                f.Native.Login("adopter_1", "adopted-pass-1", hash, "dev");
                stillIn = true;
            }
            catch
            {
            }
            Assert(stillIn, "password login still works after unlinking");
        }
        finally { f.Dispose(); }
    }

    /// <summary>SETUP-1 — the exact data path the client's forced first-time
    /// setup flow walks: a GitHub-bootstrapped account (no native credential)
    /// adopts a permanent username + first password, then signs in with those
    /// credentials onto the SAME account (no duplicate account is created).</summary>
    private static void Test_SetupBootstrapFlow()
    {
        var f = NewFixture();
        try
        {
            var (key, hash) = NewDeviceKey();
            var ghId = f.RegisterGitHubUser(9201, "bootstrapper");
            var (link, account, _) = f.Provisioning.LoginOrLink(
                new GitHubIdentity(ProviderKeys.GitHub, ghId, null, null), hash, "dev");

            // The /me username surface (FindNativeCredentialByAccount) is what
            // the client reads: null → "" → provider-only → forced setup.
            Assert(f.Db.FindNativeCredentialByAccount(account.AccountId) is null,
                "bootstrap account exposes NO username before setup");

            f.Native.SetInitialPassword(account.AccountId, "Boot.User", "bootstrap-pass-1");

            var credential = f.Db.FindNativeCredentialByAccount(account.AccountId);
            Assert(credential is not null, "setup created the native credential");
            Assert(credential!.UsernameDisplay == "Boot.User", "display username preserved verbatim");
            Assert(credential.UsernameCanonical == "boot.user", "canonical username is lowercased");

            // Username + password sign-in reaches the SAME account.
            var (loginAccount, _) = f.Native.Login("BOOT.USER", "bootstrap-pass-1", hash, "dev");
            Assert(loginAccount.AccountId == account.AccountId,
                "native login lands on the SAME bootstrap account (no duplicate)");

            // And the provider identity still resolves to that same account.
            var (_, relinkAccount, existed) = f.Provisioning.LoginOrLink(
                new GitHubIdentity(ProviderKeys.GitHub, ghId, null, null), hash, "dev");
            Assert(existed, "re-login reports the account as existing");
            Assert(relinkAccount.AccountId == account.AccountId, "provider path hits the same account");
        }
        finally { f.Dispose(); }
    }

    /// <summary>SETUP-2 — USERNAME IMMUTABILITY: a second adoption on the same
    /// account is refused (credential_exists → the wire maps it to 409), the
    /// display username is unchanged, and the password can still be changed
    /// through the authenticated change path (with the current password).</summary>
    private static void Test_SetupUsernameImmutable()
    {
        var f = NewFixture();
        try
        {
            var (key, hash) = NewDeviceKey();
            var ghId = f.RegisterGitHubUser(9202, "immutable");
            var (_, account, _) = f.Provisioning.LoginOrLink(
                new GitHubIdentity(ProviderKeys.GitHub, ghId, null, null), hash, "dev");
            f.Native.SetInitialPassword(account.AccountId, "keeper", "first-pass-123");

            var refused = false;
            try { f.Native.SetInitialPassword(account.AccountId, "renamed", "second-pass-456"); }
            catch (InvalidOperationException ex) { refused = ex.Message == "credential_exists"; }
            Assert(refused, "second adoption must be refused with credential_exists");

            var credential = f.Db.FindNativeCredentialByAccount(account.AccountId);
            Assert(credential!.UsernameDisplay == "keeper", "username unchanged after refused adoption");

            var wrongCurrent = false;
            try { f.Native.ChangePassword(account.AccountId, "wrong-current-1", Secrets.HashPassword("changed-pass-99")); }
            catch (InvalidOperationException ex) { wrongCurrent = ex.Message == "invalid_credentials"; }
            Assert(wrongCurrent, "password change still requires the CURRENT password");

            f.Native.ChangePassword(account.AccountId, "first-pass-123", Secrets.HashPassword("changed-pass-99"));
            var newPwWorks = false;
            try { f.Native.Login("keeper", "changed-pass-99", hash, "dev"); newPwWorks = true; } catch { }
            Assert(newPwWorks, "changed password signs in");
            var oldPwDead = false;
            try { f.Native.Login("keeper", "first-pass-123", hash, "dev"); }
            catch (InvalidOperationException ex) { oldPwDead = ex.Message == "invalid_credentials"; }
            Assert(oldPwDead, "old password is dead after the change");
        }
        finally { f.Dispose(); }
    }

    /// <summary>SETUP-3 — validation around first-time setup: the username
    /// policy the client mirrors, uniqueness against NATIVE accounts too, and
    /// password-length bounds.</summary>
    private static void Test_SetupValidation()
    {
        var f = NewFixture();
        try
        {
            // Username policy the setup form mirrors client-side.
            Assert(!UsernamePolicy.IsValidFormat("ab"), "below 3 chars rejected");
            Assert(!UsernamePolicy.IsValidFormat(new string('a', 33)), "above 32 chars rejected");
            Assert(!UsernamePolicy.IsValidFormat("bad name"), "space rejected");
            Assert(!UsernamePolicy.IsValidFormat("bad!name"), "symbol rejected");
            Assert(UsernamePolicy.IsValidFormat("ok.name-1_x"), "charset letters/digits/dot/underscore/hyphen accepted");
            Assert(!UsernamePolicy.IsValidPassword("short"), "password below 8 rejected");
            Assert(UsernamePolicy.IsValidPassword(new string('p', 8)), "password at 8 accepted");

            var (key, hash) = NewDeviceKey();
            var (nativeAccount, _) = f.Native.Register("resident", "resident-pass-1", hash, "dev");

            // A bootstrap account adopting a NATIVE account's username is a
            // duplicate — refused with username_taken (unique index).
            var (k2, h2) = NewDeviceKey();
            var ghId = f.RegisterGitHubUser(9203, "newcomer");
            var (_, bootstrap, _) = f.Provisioning.LoginOrLink(
                new GitHubIdentity(ProviderKeys.GitHub, ghId, null, null), h2, "dev-2");
            var taken = false;
            try { f.Native.SetInitialPassword(bootstrap.AccountId, "RESIDENT", "adopted-pass-1"); }
            catch (InvalidOperationException ex) { taken = ex.Message == "username_taken"; }
            Assert(taken, "adopting a native account's username is refused (canonical match)");
            Assert(f.Db.FindNativeCredentialByAccount(bootstrap.AccountId) is null,
                "refused adoption leaves NO credential behind");

            // And the refused attempt did not disturb the resident account.
            var stillIn = false;
            try { f.Native.Login("resident", "resident-pass-1", hash, "dev"); stillIn = true; } catch { }
            Assert(stillIn, "resident account unaffected by refused adoption");
        }
        finally { f.Dispose(); }
    }

    /// <summary>SETUP-4 — the client gate fact: the username surfaced by /me
    /// is EMPTY for a fresh GitHub bootstrap (→ forced setup) and populated
    /// once the first-time setup completes; the session issued BEFORE setup
    /// stays valid throughout (no re-login required to finish setup).</summary>
    private static void Test_SetupMeUsernameSurface()
    {
        var f = NewFixture();
        try
        {
            var (key, hash) = NewDeviceKey();
            var ghId = f.RegisterGitHubUser(9204, "surfacetest");
            var (_, account, _) = f.Provisioning.LoginOrLink(
                new GitHubIdentity(ProviderKeys.GitHub, ghId, null, null), hash, "dev");
            var device = f.Db.FindDeviceByKeyHash(hash)!;
            var (token, session) = f.Sessions.Create(account.AccountId, device.DeviceId, viaLinkId: null);
            Assert(!string.IsNullOrEmpty(token), "bootstrap session issued");

            Assert(f.Db.FindNativeCredentialByAccount(account.AccountId)?.UsernameDisplay is null,
                "username surface empty before setup (client forced-setup condition)");

            f.Native.SetInitialPassword(account.AccountId, "surface.user", "surface-pass-1");

            Assert(f.Db.FindNativeCredentialByAccount(account.AccountId)?.UsernameDisplay == "surface.user",
                "username surface populated after setup");

            // The pre-setup session is untouched by credential creation.
            Assert(f.Sessions.Validate(token) is not null, "session issued before setup still validates");
            Assert(session.ExpiresAt > DateTimeOffset.UtcNow, "session expiry in the future");
        }
        finally { f.Dispose(); }
    }

    private static void Test_NativeSuspended()
    {
        var f = NewFixture();
        try
        {
            var (key, hash) = NewDeviceKey();
            var (account, _) = f.Native.Register("Ivan", "correct-horse-1", hash, "dev");
            ExecRaw(f.DbPath, $"UPDATE DulukaAccount SET Status='Suspended' WHERE AccountId='{account.AccountId}'");

            var code = "";
            try { f.Native.Login("ivan", "correct-horse-1", hash, "dev"); }
            catch (InvalidOperationException ex) { code = ex.Message; }
            Assert(code == "account_suspended", "suspended accounts are refused at login, before any session");
        }
        finally { f.Dispose(); }
    }

    private static void Test_NativeProviderSameAccount()
    {
        var f = NewFixture();
        try
        {
            // Native account first; GitHub is then LINKED onto it (the
            // authenticated link flow seeds the ProviderLink), so a later
            // GitHub login must land on the SAME account — never a second one.
            var (key, hash) = NewDeviceKey();
            var (account, device) = f.Native.Register("Kate", "correct-horse-1", hash, "dev");

            var ghId = f.RegisterGitHubUser(9001, "kate");
            f.Db.CreateLink(account.AccountId, ProviderKeys.GitHub, ghId, "kate@example.com", "kate");

            var identity = new GitHubIdentity(ProviderKeys.GitHub, ghId, "kate@example.com", "kate");
            var (link, ghAccount, existed) = f.Provisioning.LoginOrLink(identity, hash, "dev");
            Assert(existed, "the GitHub identity already has a link");
            Assert(ghAccount.AccountId == account.AccountId, "GitHub login reaches the SAME Duluka Account");
            Assert(link.AccountId == account.AccountId, "link belongs to the same account");

            var (token, _) = f.Sessions.Create(ghAccount.AccountId, device.DeviceId, link.LinkId);
            Assert(f.Sessions.Validate(token) is not null, "one device, one working Duluka session");
        }
        finally { f.Dispose(); }
    }

    private static void Test_UnlinkWithNativeCredential()
    {
        var f = NewFixture();
        try
        {
            var (key, hash) = NewDeviceKey();
            var (account, _) = f.Native.Register("Lena", "correct-horse-1", hash, "dev");
            var ghId = f.RegisterGitHubUser(9002, "lena");
            var link = f.Db.CreateLink(account.AccountId, ProviderKeys.GitHub, ghId, null, null);

            // WITH a native credential, the password is still a way in — the
            // last PROVIDER link may be unlinked (account is never locked out).
            var result = f.Provisioning.Unlink(account.AccountId, link.LinkId);
            Assert(result.Ok, $"native-backed account may unlink its only provider (got {result.Code})");

            var stillIn = false;
            try { f.Native.Login("lena", "correct-horse-1", hash, "dev"); stillIn = true; }
            catch { }
            Assert(stillIn, "password login still works after unlinking");
        }
        finally { f.Dispose(); }
    }

    private static void Test_SchemaV3Migration()
    {
        // Build a CURRENT database, then rewind it to the v2 shape (drop the
        // v3 table + marker). Re-opening must re-apply the v3 DDL additively —
        // accounts/links/devices/sessions survive untouched.
        var dbPath = Path.Combine(Path.GetTempPath(), $"duluka-test-{Guid.NewGuid():N}.db");
        var config = TestConfig(dbPath, 30, 7);
        var db = new Database(dbPath, NullLogger<Database>.Instance);
        db.Bootstrap();
        var (key, hash) = NewDeviceKey();
        var account = db.CreateNativeAccount("migrate", "Migrate", Secrets.HashPassword("correct-horse-1"));
        db.CreateDevice(account.AccountId, "dev", hash);
        var (link, _, _) = db.UpsertLink(ProviderKeys.GitHub, "7001", null, null);
        db.DisposeAsync().AsTask().GetAwaiter().GetResult();

        ExecRaw(dbPath, "DELETE FROM SchemaHistory WHERE Version=3");
        ExecRaw(dbPath, "DROP TABLE NativeCredential");

        var db2 = new Database(dbPath, NullLogger<Database>.Instance);
        db2.Bootstrap();
        try
        {
            using var inspect = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString());
            inspect.Open();
            using (var v = inspect.CreateCommand())
            {
                v.CommandText = "SELECT COALESCE(MAX(Version),0) FROM SchemaHistory";
                Assert(Convert.ToInt32(v.ExecuteScalar()) == Database.SchemaVersion,
                    "schema marker advanced to the current version");
            }
            using (var v = inspect.CreateCommand())
            {
                // UpsertLink seeded its own provider account, so count by the
                // SPECIFIC native account — the row must survive the rewind.
                v.CommandText = "SELECT COUNT(*) FROM DulukaAccount WHERE AccountId=$id";
                v.Parameters.AddWithValue("$id", account.AccountId);
                Assert(Convert.ToInt32(v.ExecuteScalar()) == 1,
                    "the native account survives the additive migration");
            }
            Assert(db2.FindNativeCredentialByUsername("migrate") is null,
                "rewound v2 credential is gone, but the table exists again");
            var cred = db2.FindNativeCredentialByAccount(account.AccountId);
            Assert(cred is null, "no phantom credential rows");

            // The migrated database is fully usable: new native account works.
            var (k2, h2) = NewDeviceKey();
            var newAccount = db2.CreateNativeAccount("fresh", "Fresh", Secrets.HashPassword("correct-horse-1"));
            Assert(db2.FindNativeCredentialByUsername("fresh")!.AccountId == newAccount.AccountId,
                "post-migration registration works");
        }
        finally
        {
            db2.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Cleanup(dbPath);
        }
    }

    // ─── account deletion (service level — the machinery behind DELETE /v1/account) ───

    private static void Test_AccountDeletionCascade()
    {
        var f = NewFixture();
        try
        {
            // Fully loaded account: provider link + device + live session +
            // native credential (bootstrap account that finished its setup).
            var ghId = f.RegisterGitHubUser(13001, "doomed-provider");
            var id = new GitHubIdentity(ProviderKeys.GitHub, ghId, null, null);
            var (_, hash) = NewDeviceKey();
            var (link, account, _) = f.Provisioning.LoginOrLink(id, hash, "dev");
            var device = f.Db.DevicesForAccount(account.AccountId).Single();
            f.Sessions.Create(account.AccountId, device.DeviceId, null);
            f.Native.SetInitialPassword(account.AccountId, "doomed.user", "cascade-pass-123");

            var sessions = f.Native.DeleteAccount(account.AccountId, "cascade-pass-123");
            Assert(sessions >= 1, "the live session must be counted among the deleted");
            Assert(f.Db.GetAccount(account.AccountId) is null, "the account row is gone");
            Assert(f.Db.GetLink(link.LinkId) is null, "the provider link row is gone");
            Assert(!f.Db.HasNativeCredential(account.AccountId), "the native credential is gone");
            Assert(f.Db.DevicesForAccount(account.AccountId).Count == 0, "the device rows are gone");
            Assert(f.Db.FindActiveLink(ProviderKeys.GitHub, ghId) is null,
                "the (provider, identity) anchor is freed — the duplicate-account defense no longer sees the dead account");

            // Every UNIQUE anchor is reusable: the same identity bootstraps a
            // FRESH account, and the username can be registered again.
            var (_, fresh, existed) = f.Provisioning.LoginOrLink(id, Secrets.Sha256Hex(Secrets.NewToken("devk_")), "dev2");
            Assert(!existed && fresh.AccountId != account.AccountId,
                "the same identity must bootstrap a FRESH account after deletion");
            f.Native.Register("doomed.user", "fresh-pass-456", Secrets.Sha256Hex(Secrets.NewToken("devk_")), "dev3");
        }
        finally { f.Dispose(); }
    }

    private static void Test_AccountDeletionPasswordProof()
    {
        var f = NewFixture();
        try
        {
            var (devKey, _) = NewDeviceKey();
            var (account, device) = f.Native.Register("proof.user", "keeper-pass-123", Secrets.Sha256Hex(devKey), "dev");
            f.Sessions.Create(account.AccountId, device.DeviceId, null);

            foreach (var bad in new[] { "", "wrong-pass-999" })
            {
                var refused = false;
                try { f.Native.DeleteAccount(account.AccountId, bad); }
                catch (InvalidOperationException) { refused = true; }
                Assert(refused, "a password-protected account must refuse deletion without the correct password");
            }
            Assert(f.Db.GetAccount(account.AccountId) is not null,
                "a refused deletion must not touch the account");

            var sessions = f.Native.DeleteAccount(account.AccountId, "keeper-pass-123");
            Assert(sessions >= 1, "the correct password must delete and count the sessions");
            Assert(f.Db.GetAccount(account.AccountId) is null, "the account row is gone after the correct password");
        }
        finally { f.Dispose(); }
    }

    private static void ExecRaw(string dbPath, string sql)
    {
        // Pooling would keep the file handle alive after Close and break the
        // fixture's cleanup delete — same discipline as Database itself.
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ─── profile: display name / profile image (presentation surface) ──────

    private static string TinyPngDataUrl() =>
        "data:image/png;base64," + Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

    /// <summary>Fixture with a native account, its device and a live session —
    /// the profile surface's precondition is an authenticated account.</summary>
    private static (Fixture F, string Token, string AccountId, string DeviceId) NewNativeSessionFixture()
    {
        var f = NewFixture();
        var (account, device) = f.Native.Register("profiley", "correct-horse-1",
            Secrets.Sha256Hex("devk-profile-1"), "prof-dev");
        var (token, _) = f.Sessions.Create(account.AccountId, device.DeviceId, null);
        return (f, token, account.AccountId, device.DeviceId);
    }

    private static void Test_ProfileDisplayName()
    {
        var (f, _, accountId, _) = NewNativeSessionFixture();
        try
        {
            // The endpoint trims before persisting (Program.cs); the data layer
            // stores the profile verbatim — that contract is pinned here.
            f.Db.UpdateAccountProfile(accountId, "Prof  Al", TinyPngDataUrl());
            var reloaded = f.Db.GetAccount(accountId);
            Assert(reloaded is not null, "account still present after profile update");
            Assert(reloaded!.DisplayName == "Prof  Al", "display name persisted verbatim");
            Assert(reloaded.UpdatedAt >= reloaded.CreatedAt, "UpdatedAt advanced past CreatedAt");

            // Empty display name = clear (deterministic overwrite, no merge;
            // the endpoint maps whitespace input to null before this layer).
            f.Db.UpdateAccountProfile(accountId, null, TinyPngDataUrl());
            Assert(f.Db.GetAccount(accountId)!.DisplayName is null,
                "null display name clears the field");
        }
        finally { f.Dispose(); }
    }

    private static void Test_ProfileImage()
    {
        var (f, _, accountId, _) = NewNativeSessionFixture();
        try
        {
            var url = TinyPngDataUrl();
            f.Db.UpdateAccountProfile(accountId, "P", url);
            Assert(f.Db.GetAccount(accountId)!.ProfileImage == url,
                "profile image data URL persisted verbatim");

            f.Db.UpdateAccountProfile(accountId, "P", null);
            Assert(f.Db.GetAccount(accountId)!.ProfileImage is null,
                "null clears the profile image");
        }
        finally { f.Dispose(); }
    }

    private static void Test_ProfileIdentityPreserved()
    {
        var (f, token, accountId, deviceId) = NewNativeSessionFixture();
        try
        {
            var before = f.Db.GetAccount(accountId)!;
            var credBefore = f.Db.FindNativeCredentialByAccount(accountId)!;
            var devicesBefore = f.Db.DevicesForAccount(accountId).Count;

            f.Db.UpdateAccountProfile(accountId, "Renamed Person", TinyPngDataUrl());

            var after = f.Db.GetAccount(accountId)!;
            var credAfter = f.Db.FindNativeCredentialByAccount(accountId)!;
            Assert(after.AccountId == before.AccountId, "AccountId unchanged");
            Assert(after.Status == before.Status, "Status unchanged");
            Assert(after.CreatedAt == before.CreatedAt, "CreatedAt unchanged");
            Assert(credAfter.UsernameCanonical == credBefore.UsernameCanonical,
                "username anchor unchanged by a profile edit");
            Assert(credAfter.UsernameDisplay == credBefore.UsernameDisplay,
                "username display unchanged by a profile edit");
            Assert(f.Db.DevicesForAccount(accountId).Count == devicesBefore, "device rows untouched");
            Assert(f.Db.DevicesForAccount(accountId).Single().DeviceId == deviceId, "same device remains");
            Assert(f.Sessions.Validate(token) is not null, "the live session survives a profile edit");
            Assert(after.DisplayName == "Renamed Person", "only the profile fields changed");
        }
        finally { f.Dispose(); }
    }

    private static void Test_ProfilePolicyMatrix()
    {
        var png = TinyPngDataUrl();
        // Display name.
        Assert(ProfilePolicy.IsValidDisplayName(null), "null display name = clear");
        Assert(ProfilePolicy.IsValidDisplayName(""), "empty display name = clear");
        Assert(ProfilePolicy.IsValidDisplayName("   "), "whitespace display name = clear");
        Assert(ProfilePolicy.IsValidDisplayName("Alice"), "plain display name valid");
        Assert(ProfilePolicy.IsValidDisplayName(new string('a', ProfilePolicy.MaxDisplayNameLength)),
            "64-char display name valid");
        Assert(!ProfilePolicy.IsValidDisplayName(new string('a', ProfilePolicy.MaxDisplayNameLength + 1)),
            "65-char display name rejected");
        Assert(!ProfilePolicy.IsValidDisplayName("bad\nname"), "control characters rejected");

        // Profile image.
        Assert(ProfilePolicy.IsValidProfileImage(null), "null image = clear");
        Assert(ProfilePolicy.IsValidProfileImage(""), "empty image = clear");
        Assert(ProfilePolicy.IsValidProfileImage(png), "png data URL valid");
        Assert(ProfilePolicy.IsValidProfileImage("data:image/jpeg;base64," + Convert.ToBase64String(new byte[] { 1, 2, 3 })),
            "jpeg accepted");
        Assert(ProfilePolicy.IsValidProfileImage("data:image/webp;base64," + Convert.ToBase64String(new byte[] { 1 })),
            "webp accepted");
        Assert(!ProfilePolicy.IsValidProfileImage("data:image/svg+xml;base64,AAAA"),
            "svg refused (scriptable)");
        Assert(!ProfilePolicy.IsValidProfileImage("data:image/png;base64,"), "empty payload refused");
        Assert(!ProfilePolicy.IsValidProfileImage("data:image/png;base64,!!!not-base64!!!"),
            "non-base64 payload refused");
        Assert(!ProfilePolicy.IsValidProfileImage("http://example.test/avatar.png"),
            "remote URL refused");
        var overCap = "data:image/png;base64," + new string('A', (ProfilePolicy.MaxProfileImageBytes / 3 + 1) * 4 + 8);
        Assert(!ProfilePolicy.IsValidProfileImage(overCap),
            "oversized payload refused before decode");
    }

    private static void Test_SchemaV4Migration()
    {
        // Build a CURRENT database, then rewind it to the v3 shape (drop the
        // ProfileImage column + the v4 marker). Re-opening must re-apply the
        // v4 ALTER additively — accounts/credentials/devices survive untouched.
        var dbPath = Path.Combine(Path.GetTempPath(), $"duluka-test-{Guid.NewGuid():N}.db");
        var db = new Database(dbPath, NullLogger<Database>.Instance);
        db.Bootstrap();
        var (_, hash) = NewDeviceKey();
        var account = db.CreateNativeAccount("migrate4", "Migrate Four", Secrets.HashPassword("correct-horse-1"));
        db.CreateDevice(account.AccountId, "dev", hash);
        db.UpdateAccountProfile(account.AccountId, "Pre", TinyPngDataUrl());
        db.DisposeAsync().AsTask().GetAwaiter().GetResult();

        ExecRaw(dbPath, "DELETE FROM SchemaHistory WHERE Version=4");
        ExecRaw(dbPath, "ALTER TABLE DulukaAccount DROP COLUMN ProfileImage");

        var db2 = new Database(dbPath, NullLogger<Database>.Instance);
        db2.Bootstrap();
        try
        {
            var reloaded = db2.GetAccount(account.AccountId);
            Assert(reloaded is not null, "account survives the v4 migration");
            Assert(reloaded!.ProfileImage is null,
                "profile image column re-created empty (the column was dropped with the rewind)");
            Assert(db2.FindNativeCredentialByUsername("migrate4") is not null,
                "credential untouched by the migration");
            Assert(db2.DevicesForAccount(account.AccountId).Count == 1, "device untouched by the migration");

            // The migrated store is fully profile-capable again.
            db2.UpdateAccountProfile(account.AccountId, "Post", TinyPngDataUrl());
            Assert(db2.GetAccount(account.AccountId)!.ProfileImage == TinyPngDataUrl(),
                "profile writes work after the migration");
        }
        finally
        {
            db2.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Cleanup(dbPath);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ─── HTTP integration: the §7.1 wire contract against the REAL server ───
    // The service-level tests above cannot see Program.cs (minimal APIs) —
    // the redacted-state defect shipped through exactly that blind spot.

    private static void RunHttpIntegrationTests()
    {
        var serverCsproj = LocateServerProject();
        var serverDir = Path.GetDirectoryName(serverCsproj)!;
        var port = FreeTcpPort();
        var dbPath = Path.Combine(Path.GetTempPath(), $"duluka-http-{Guid.NewGuid():N}.db");
        var cfg =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        // Launch the BUILT DLL directly — no `dotnet run` build layer in the
        // child (the parent's ProjectReference build already produced it, and
        // a nested build/MSBuild layer here is nondeterministic output-wise).
        // NB: Environment.ProcessPath here is the TESTS' APPhOST exe — an
        // apphost cannot `exec` another dll (it just re-runs the tests), so
        // the real dotnet host must be resolved from the loaded runtime.
        var serverDll = Path.Combine(serverDir, "bin", cfg, "net10.0", "Duluka.Server.dll");
        Assert(File.Exists(serverDll), $"server build output missing: {serverDll}");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = DotnetHostPath(),
            Arguments = $"exec \"{serverDll}\" --urls http://127.0.0.1:{port}",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["DULUKA_Database__Path"] = dbPath;
        psi.Environment["DULUKA_GitHub__ClientId"] = "test-client-id";
        psi.Environment["DULUKA_GitHub__ClientSecret"] = "test-client-secret";
        psi.Environment["DULUKA_GitHub__RedirectUri"] = "http://localhost:8517/v1/auth/github/callback";

        System.Diagnostics.Process? server = null;
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        try
        {
            Console.WriteLine($"  [http-int] child: \"{psi.FileName}\" {psi.Arguments}");
            server = System.Diagnostics.Process.Start(psi);
            server!.OutputDataReceived += (_, e) => stdout.AppendLine(e.Data);
            server.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();

            var basePath = $"http://127.0.0.1:{port}";
            try
            {
                WaitForLive(basePath, server, stdout, stderr);
            }
            catch (Exception ex)
            {
                // Server never came up — fail the HTTP group without crashing
                // the whole suite, so the rest of the results still report.
                Console.WriteLine(ex.Message);
                Failures.Add("HTTP-INT group — server startup failed: " + ex.Message);
                _failed += 4;
                return;
            }
            using var http = new HttpClient { BaseAddress = new Uri(basePath) };
            const string deviceKey = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789AB";

            Run("HTTP-INT-1: /start returns RAW state + ok envelope + reqId echo", () =>
            {
                using var resp = PostJson(http, "/v1/auth/github/start", "reqid-http-int-1",
                    $$"""{"deviceName":"it-dev","deviceKey":"{{deviceKey}}"}""");
                var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert((int)resp.StatusCode == 200, $"expected 200, got {(int)resp.StatusCode}: {body}");
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                Assert(root.GetProperty("ok").GetBoolean(), "ok=true on success envelope");
                Assert(root.GetProperty("reqId").GetString() == "reqid-http-int-1", "reqId echoed verbatim");
                var resource = root.GetProperty("resource");
                var state = resource.GetProperty("state").GetString() ?? "";
                var url = resource.GetProperty("authorizationUrl").GetString() ?? "";
                Assert(state.StartsWith("duluka_state_") && !state.Contains('…'),
                    "state returned RAW (not redacted) — the client must be able to echo it");
                Assert(url.Contains("state=" + Uri.EscapeDataString(state)),
                    "authorizationUrl carries the SAME state value");
            });

            Run("HTTP-INT-2: 401 envelope with registry code auth.session_expired", () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/account/me");
                req.Headers.Add("X-ReqId", "reqid-http-int-2");
                using var resp = http.SendAsync(req).GetAwaiter().GetResult();
                var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert((int)resp.StatusCode == 401, $"expected 401, got {(int)resp.StatusCode}: {body}");
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                Assert(!root.GetProperty("ok").GetBoolean(), "ok=false on error envelope");
                Assert(root.GetProperty("errorCode").GetString() == "auth.session_expired",
                    "registry code auth.session_expired on the wire");
                Assert(root.GetProperty("httpStatus").GetInt32() == 401, "httpStatus echoed");
                Assert(!root.GetProperty("retryable").GetBoolean(), "401 is not retryable");
                Assert(root.GetProperty("reqId").GetString() == "reqid-http-int-2", "reqId echoed on errors too");
            });

            Run("HTTP-INT-3: NVIDIA start → 501 provider_reserved envelope", () =>
            {
                using var resp = PostJson(http, "/v1/auth/nvidia/start", "reqid-http-int-3",
                    $$"""{"deviceName":"it-dev","deviceKey":"{{deviceKey}}"}""");
                var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert((int)resp.StatusCode == 501, $"expected 501, got {(int)resp.StatusCode}: {body}");
                using var doc = JsonDocument.Parse(body);
                Assert(doc.RootElement.GetProperty("errorCode").GetString() == "provider_reserved",
                    "reserved provider code on the wire");
            });

            Run("HTTP-INT-4: native multi-device + foreign-key conflict has no orphan/session", () =>
            {
                const string password = "native-http-pass-123";
                const string keyA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                const string keyB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
                const string keyC = "cccccccccccccccccccccccccccccccccccccccccccccccc";
                using var registerA = PostJson(http, "/v1/auth/register", "reqid-http-int-4a",
                    $$"""{"username":"alice-http","password":"{{password}}","deviceKey":"{{keyA}}","deviceName":"client-a"}""");
                using var registerADoc = JsonDocument.Parse(registerA.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var accountA = registerADoc.RootElement.GetProperty("resource").GetProperty("accountId").GetString();
                var deviceA = registerADoc.RootElement.GetProperty("resource").GetProperty("deviceId").GetString();

                using var loginB = PostJson(http, "/v1/auth/login", "reqid-http-int-4b",
                    $$"""{"username":"alice-http","password":"{{password}}","deviceKey":"{{keyB}}","deviceName":"client-b"}""");
                using var loginBDoc = JsonDocument.Parse(loginB.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var loginAccount = loginBDoc.RootElement.GetProperty("resource").GetProperty("accountId").GetString();
                var deviceB = loginBDoc.RootElement.GetProperty("resource").GetProperty("deviceId").GetString();
                Assert(accountA == loginAccount && deviceA != deviceB,
                    "same native credentials must resolve one account with a new device");

                using var registerOther = PostJson(http, "/v1/auth/register", "reqid-http-int-4c",
                    $$"""{"username":"bob-http","password":"{{password}}","deviceKey":"{{keyC}}","deviceName":"other"}""");
                using var otherDoc = JsonDocument.Parse(registerOther.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var otherToken = otherDoc.RootElement.GetProperty("resource").GetProperty("sessionToken").GetString()!;
                using var devicesBeforeRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/account/devices");
                devicesBeforeRequest.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", otherToken);
                using var devicesBefore = http.SendAsync(devicesBeforeRequest).GetAwaiter().GetResult();
                using var devicesBeforeDoc = JsonDocument.Parse(
                    devicesBefore.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var deviceCountBefore = devicesBeforeDoc.RootElement.GetProperty("resource")
                    .GetProperty("devices").GetArrayLength();

                using var collision = PostJson(http, "/v1/auth/login", "reqid-http-int-4d",
                    $$"""{"username":"bob-http","password":"{{password}}","deviceKey":"{{keyA}}","deviceName":"collision"}""");
                var collisionBody = collision.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert((int)collision.StatusCode == 409, "foreign device key must return HTTP 409");
                using var collisionDoc = JsonDocument.Parse(collisionBody);
                Assert(collisionDoc.RootElement.GetProperty("errorCode").GetString() == "conflict.link_conflict",
                    "foreign device key must return conflict.link_conflict");
                Assert(!collisionDoc.RootElement.TryGetProperty("resource", out _),
                    "conflicting login must not return a session resource");
                using var devicesAfterRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/account/devices");
                devicesAfterRequest.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", otherToken);
                using var devicesAfter = http.SendAsync(devicesAfterRequest).GetAwaiter().GetResult();
                using var devicesAfterDoc = JsonDocument.Parse(
                    devicesAfter.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert(devicesAfterDoc.RootElement.GetProperty("resource").GetProperty("devices").GetArrayLength() == deviceCountBefore,
                    "conflicting login must not create an orphan device");
            });

            Run("HTTP-INT-5: rate limiter answers with the §7.1 envelope (429)", () =>
            {
                var saw429 = false;
                for (var i = 0; i < 14 && !saw429; i++)
                {
                    using var resp = PostJson(http, "/v1/auth/github/start", $"reqid-http-int-4-{i}",
                        $$"""{"deviceName":"it-dev","deviceKey":"{{deviceKey}}"}""");
                    if ((int)resp.StatusCode != 429) continue;
                    saw429 = true;
                    var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    Assert(root.GetProperty("errorCode").GetString() == "server.rate_limited",
                        "429 carries server.rate_limited");
                    Assert(root.GetProperty("retryable").GetBoolean(), "429 is retryable");
                    Assert(root.GetProperty("reqId").GetString() == $"reqid-http-int-4-{i}", "429 echoes reqId");
                }
                Assert(saw429, "expected ≥1 429 within the fixed 10/min auth-start window");
            });
            Run("HTTP-INT-5: PUT /v1/account/profile unauthenticated → 401 envelope", () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Put, "/v1/account/profile")
                {
                    Content = new StringContent("""{"displayName":"X"}""", Encoding.UTF8, "application/json"),
                };
                req.Headers.Add("X-ReqId", "reqid-http-int-5");
                using var resp = http.SendAsync(req).GetAwaiter().GetResult();
                var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert((int)resp.StatusCode == 401, $"expected 401, got {(int)resp.StatusCode}: {body}");
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                Assert(!root.GetProperty("ok").GetBoolean(), "ok=false on the profile surface too");
                Assert(root.GetProperty("errorCode").GetString() == "auth.session_expired",
                    "registry 401 code on the profile surface");
                Assert(root.GetProperty("reqId").GetString() == "reqid-http-int-5", "reqId echoed");
            });
        }
        finally
        {
            if (server is not null && !server.HasExited)
            {
                try { server.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }
            // Kill() returns before the OS releases the child's file handles —
            // wait for the actual exit or the store delete below races it.
            try { server?.WaitForExit(5000); } catch { /* best effort */ }
            server?.Dispose();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var p = dbPath + suffix;
                if (File.Exists(p)) File.Delete(p);
            }
        }
    }    private static HttpResponseMessage PostJson(HttpClient http, string url, string reqId, string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("X-ReqId", reqId);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return http.SendAsync(req).GetAwaiter().GetResult();
    }

    private static string DotnetHostPath()
    {
        // The loaded runtime's directory sits <dotnet-root>\shared\<fx>\<ver>;
        // the host that launched THIS process lives at the dotnet root.
        var dir = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        for (var i = 0; i < 3 && dir?.Parent is not null; i++) dir = dir.Parent;
        var host = Path.Combine(dir!.FullName, "dotnet.exe");
        return File.Exists(host) ? host : "dotnet";
    }

    private static string LocateServerProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "Server", "Duluka.Server", "Duluka.Server.csproj");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Duluka.Server.csproj not found above the test binary directory");
    }

    private static int FreeTcpPort()
    {
        var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        t.Start();
        try { return ((System.Net.IPEndPoint)t.LocalEndpoint).Port; }
        finally { t.Stop(); }
    }

    private static void WaitForLive(string basePath, System.Diagnostics.Process server,
        System.Text.StringBuilder stdout, System.Text.StringBuilder stderr)
    {
        using var http = new HttpClient { BaseAddress = new Uri(basePath) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (server.HasExited)
                throw new InvalidOperationException($"server exited early.\nSTDOUT:{stdout}\nSTDERR:{stderr}");
            try
            {
                using var resp = http.GetAsync("/healthz").GetAwaiter().GetResult();
                if ((int)resp.StatusCode == 200) return;
            }
            catch { /* not up yet */ }
            Thread.Sleep(300);
        }
        throw new InvalidOperationException($"server not live within 90s.\nSTDOUT:{stdout}\nSTDERR:{stderr}");
    }

    // ─── CWD independence: state must not follow the working directory ────
    // `Database:Path` is relative by default and the content root used to
    // follow the process CWD — a server started from an unrelated directory
    // silently created a FRESH database there and split state (C/1 finding).
    // These tests launch the REAL server binary from three different working
    // directories and pin: the database always materializes at the deployed
    // application root, nothing appears in any unrelated CWD, relative CONFIG
    // paths resolve against the app root, and seeded state survives a restart
    // from yet another CWD.

    private static System.Diagnostics.Process StartCwdServer(
        string serverDll, int port, string workingDir, string? envRelDb,
        System.Text.StringBuilder stdout, System.Text.StringBuilder stderr)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = DotnetHostPath(),
            Arguments = $"exec \"{serverDll}\" --urls http://127.0.0.1:{port}",
            WorkingDirectory = workingDir,   // ← the variable under test
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        if (envRelDb is not null) psi.Environment["DULUKA_Database__Path"] = envRelDb;
        psi.Environment["DULUKA_GitHub__ClientId"] = "test-client-id";
        psi.Environment["DULUKA_GitHub__ClientSecret"] = "test-client-secret";
        psi.Environment["DULUKA_GitHub__RedirectUri"] = "http://localhost:8517/v1/auth/github/callback";
        var server = System.Diagnostics.Process.Start(psi)!;
        server.OutputDataReceived += (_, e) => stdout.AppendLine(e.Data);
        server.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        WaitForLive($"http://127.0.0.1:{port}", server, stdout, stderr);
        return server;
    }

    private static void StopCwdServer(System.Diagnostics.Process? server)
    {
        if (server is not null && !server.HasExited)
        {
            try { server.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
        server?.Dispose();
        // Give Windows a beat to release the file locks before assertions/cleanup.
        Thread.Sleep(300);
    }

    private static void DeleteDatabaseFiles(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var p = dbPath + suffix;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static void RunCwdIndependenceTests()
    {
        Console.WriteLine("  [cwd-int] real-server CWD-independence group");
        var cfg =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        var serverBinDir = Path.Combine(
            Path.GetDirectoryName(LocateServerProject())!, "bin", cfg, "net10.0");
        var serverDll = Path.Combine(serverBinDir, "Duluka.Server.dll");
        Assert(File.Exists(serverDll), $"server build output missing: {serverDll}");

        // The DEFAULT Database:Path ("AppData/DulukaAccount.db"), resolved the
        // CWD-independent way: under the deployed application root.
        var stableDb = Path.Combine(serverBinDir, "AppData", "DulukaAccount.db");
        var envDb = Path.Combine(serverBinDir, "AppData", "CwdEnvRel.db");
        var stableDbPreExisted = File.Exists(stableDb);
        var envDbPreExisted = File.Exists(envDb);
        var unrelatedA = Path.Combine(Path.GetTempPath(), $"duluka-cwd-a-{Guid.NewGuid():N}");
        var unrelatedB = Path.Combine(Path.GetTempPath(), $"duluka-cwd-b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(unrelatedA);
        Directory.CreateDirectory(unrelatedB);

        System.Diagnostics.Process? server = null;
        try
        {
            Run("CWD-1: deployment dir as CWD → ready OK, DB at the app root", () =>
            {
                var port = FreeTcpPort();
                server = StartCwdServer(serverDll, port, serverBinDir, null, new(), new());
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                using var resp = http.GetAsync("/healthz/ready").GetAwaiter().GetResult();
                var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert((int)resp.StatusCode == 200, $"ready expected 200, got {(int)resp.StatusCode}: {body}");
                Assert(File.Exists(stableDb), $"default DB must materialize at the app root: {stableDb}");
            });
            StopCwdServer(server); server = null;

            Run("CWD-2: unrelated CWD → SAME DB at the app root, nothing in the CWD", () =>
            {
                var port = FreeTcpPort();
                server = StartCwdServer(serverDll, port, unrelatedA, null, new(), new());
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                using var resp = http.GetAsync("/healthz/ready").GetAwaiter().GetResult();
                Assert((int)resp.StatusCode == 200, $"ready expected 200, got {(int)resp.StatusCode}");
                Assert(File.Exists(stableDb), "the default DB location must not follow the CWD");
                Assert(!Directory.Exists(Path.Combine(unrelatedA, "AppData")),
                    "no database may leak into an unrelated working directory");
            });
            StopCwdServer(server); server = null;

            Run("CWD-3: relative CONFIG DB path resolves against the app root", () =>
            {
                var port = FreeTcpPort();
                server = StartCwdServer(serverDll, port, unrelatedB, "AppData/CwdEnvRel.db", new(), new());
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                using var resp = http.GetAsync("/healthz/ready").GetAwaiter().GetResult();
                Assert((int)resp.StatusCode == 200, $"ready expected 200, got {(int)resp.StatusCode}");
                Assert(File.Exists(envDb), $"relative config path must resolve at the app root: {envDb}");
                Assert(!Directory.Exists(Path.Combine(unrelatedB, "AppData")),
                    "no database may leak into an unrelated working directory");
            });
            StopCwdServer(server); server = null;

            Run("CWD-4: seeded state survives a restart from a third CWD", () =>
            {
                // Seed with the production data layer while the server is STOPPED,
                // then start from yet another unrelated directory and use the state.
                string token, accountId;
                var seedDb = new Database(stableDb, NullLogger<Database>.Instance);
                try
                {
                    seedDb.Bootstrap(); // idempotent on the server-created store
                    var account = seedDb.CreateAccount("cwd-persist");
                    var device = seedDb.CreateDevice(account.AccountId, "cwd-dev",
                        Secrets.Sha256Hex("cwd-device-key-" + Guid.NewGuid().ToString("N")));
                    var link = seedDb.CreateLink(account.AccountId, ProviderKeys.GitHub,
                        "cwd-persist-" + Guid.NewGuid().ToString("N"), null, null);
                    token = Secrets.NewToken(SessionService.TokenPrefix);
                    accountId = account.AccountId;
                    seedDb.CreateSession(account.AccountId, device.DeviceId, link.LinkId,
                        Secrets.Sha256Hex(token), DateTimeOffset.UtcNow + TimeSpan.FromDays(30));
                }
                finally { seedDb.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

                var port = FreeTcpPort();
                server = StartCwdServer(serverDll, port, unrelatedB, null, new(), new());
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/account/me");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var resp = http.SendAsync(req).GetAwaiter().GetResult();
                var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert((int)resp.StatusCode == 200,
                    $"seeded session must validate after restart: {(int)resp.StatusCode} {body}");
                using var doc = JsonDocument.Parse(body);
                Assert(doc.RootElement.GetProperty("resource").GetProperty("accountId").GetString() == accountId,
                    "/me must resolve the account from the SAME stable-root database");
                Assert(!Directory.Exists(Path.Combine(unrelatedB, "AppData")),
                    "no database may leak into an unrelated working directory");
            });
        }
        finally
        {
            StopCwdServer(server);
            // Cleanup only what THIS group created — never a pre-existing operator DB.
            if (!stableDbPreExisted)
            {
                DeleteDatabaseFiles(stableDb);
                TryDeleteEmptyDir(Path.GetDirectoryName(stableDb)!);
            }
            if (!envDbPreExisted)
            {
                DeleteDatabaseFiles(envDb);
                TryDeleteEmptyDir(Path.GetDirectoryName(envDb)!);
            }
            try { Directory.Delete(unrelatedA, true); } catch { /* best effort */ }
            try { Directory.Delete(unrelatedB, true); } catch { /* best effort */ }
        }
    }

    private static void TryDeleteEmptyDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { /* best effort */ }
    }

    /// <summary>Intentionally inert — documents the reserved provider contract.</summary>
    public static class NvidiaProviderStub { }
}
