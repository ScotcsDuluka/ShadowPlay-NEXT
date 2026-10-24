using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;

internal sealed class ApplicationOntologyClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _cachePath;
    private readonly Action<string> _log;
    private readonly object _catalogSync = new();
    private OntologyCatalogSnapshot _catalogSnapshot;

    internal ApplicationOntologyClient(string baseUrl, int timeoutSeconds,
        string cachePath, Action<string> log)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
            throw new ArgumentException("ApplicationOntology base URL must be an absolute HTTP(S) URL without credentials, query, or fragment.", nameof(baseUrl));

        _baseUrl = baseUri.ToString().TrimEnd('/') + "/";
        _cachePath = Path.GetFullPath(cachePath);
        _log = log;
        _http = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 2, 15))
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NvContainer-ApplicationOntology/1.0");
    }

    internal string BaseUrl => _baseUrl;

    internal OntologyCatalogSnapshot LoadCachedCatalog()
    {
        lock (_catalogSync)
        {
            if (_catalogSnapshot != null)
                return _catalogSnapshot;
            if (!File.Exists(_cachePath))
                return OntologyCatalogSnapshot.Failure(404, reachable: false,
                    "No local ApplicationOntology catalog cache exists yet.");

            try
            {
                var info = new FileInfo(_cachePath);
                if (info.Length > 5 * 1024 * 1024)
                    throw new InvalidDataException("Local catalog cache exceeds the 5 MiB limit.");
                var snapshot = ParseCatalog(File.ReadAllText(_cachePath), false);
                if (!snapshot.Ready)
                    throw new InvalidDataException(snapshot.Error);
                _catalogSnapshot = snapshot;
                _log("[ontology] loaded local catalog cache: version=" +
                    snapshot.CatalogVersion + " games=" + snapshot.GameCount);
                return snapshot;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                _log("[ontology] local catalog cache rejected: " +
                    ex.GetType().Name + ": " + ex.Message);
                return OntologyCatalogSnapshot.Failure(502, false,
                    "Local ApplicationOntology catalog cache is invalid.");
            }
        }
    }

    internal (int StatusCode, object Body) GetStatus()
    {
        var snapshot = LoadCachedCatalog();
        return (snapshot.Ready ? 200 : 503, new
        {
            configured = true,
            source = snapshot.Ready ? "local-cache" : "unavailable",
            reachable = snapshot.Ready,
            onlineReachable = snapshot.Reachable,
            ready = snapshot.Ready,
            baseUrl = _baseUrl,
            cachePath = _cachePath,
            catalogVersion = snapshot.CatalogVersion,
            gameCount = snapshot.GameCount,
            catalogEmpty = snapshot.Ready && snapshot.GameCount == 0,
            error = snapshot.Error
        });
    }

    private (int StatusCode, object Body) GetRemoteStatus()
    {
        try
        {
            using var healthResponse = _http.GetAsync("health").GetAwaiter().GetResult();
            var healthText = healthResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!healthResponse.IsSuccessStatusCode)
            {
                _log("[ontology] health failed: HTTP " + (int)healthResponse.StatusCode);
                return (502, new
                {
                    configured = true,
                    reachable = true,
                    ready = false,
                    baseUrl = _baseUrl,
                    upstreamStatusCode = (int)healthResponse.StatusCode,
                    error = "ApplicationOntology health endpoint returned a non-success status."
                });
            }

            using var healthDocument = JsonDocument.Parse(healthText);
            var health = healthDocument.RootElement.Clone();
            var healthReady = health.TryGetProperty("status", out var statusValue) &&
                statusValue.ValueKind == JsonValueKind.String &&
                string.Equals(statusValue.GetString(), "ready", StringComparison.OrdinalIgnoreCase);

            using var versionResponse = _http.GetAsync("v1/catalog/version").GetAwaiter().GetResult();
            var versionText = versionResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!versionResponse.IsSuccessStatusCode)
            {
                _log("[ontology] catalog version failed: HTTP " + (int)versionResponse.StatusCode);
                return (502, new
                {
                    configured = true,
                    reachable = true,
                    ready = false,
                    baseUrl = _baseUrl,
                    health,
                    upstreamStatusCode = (int)versionResponse.StatusCode,
                    error = "ApplicationOntology catalog version endpoint returned a non-success status."
                });
            }

            using var versionDocument = JsonDocument.Parse(versionText);
            var catalog = versionDocument.RootElement.Clone();
            var version = ReadString(catalog, "catalogVersion");
            var gameCount = ReadInt(catalog, "gameCount", -1);
            if (string.IsNullOrWhiteSpace(version) || gameCount < 0)
            {
                _log("[ontology] catalog version response failed schema validation");
                return (502, new
                {
                    configured = true,
                    reachable = true,
                    ready = false,
                    baseUrl = _baseUrl,
                    health,
                    catalog,
                    error = "ApplicationOntology returned an invalid catalog version response."
                });
            }

            _log("[ontology] connected: " + _baseUrl + " catalogVersion=" + version + " gameCount=" + gameCount);
            return (200, new
            {
                configured = true,
                reachable = true,
                ready = healthReady,
                baseUrl = _baseUrl,
                health,
                catalog,
                catalogEmpty = gameCount == 0
            });
        }
        catch (OperationCanceledException)
        {
            _log("[ontology] status request timed out: " + _baseUrl);
            return (504, new
            {
                configured = true,
                reachable = false,
                ready = false,
                baseUrl = _baseUrl,
                error = "ApplicationOntology request timed out."
            });
        }
        catch (Exception ex)
        {
            _log("[ontology] status request failed: " + ex.GetType().Name + ": " + ex.Message);
            return (502, new
            {
                configured = true,
                reachable = false,
                ready = false,
                baseUrl = _baseUrl,
                error = "ApplicationOntology could not be reached or returned invalid JSON."
            });
        }
    }

    internal async Task<OntologyCatalogSnapshot> GetExecutableCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var healthResponse = await _http.GetAsync("health", cancellationToken).ConfigureAwait(false);
            if (!healthResponse.IsSuccessStatusCode)
            {
                return OntologyCatalogSnapshot.Failure(
                    healthResponse.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ? 503 : 502,
                    reachable: true, "ApplicationOntology health endpoint returned HTTP " + (int)healthResponse.StatusCode + ".");
            }

            var healthText = await ReadBoundedContentAsync(healthResponse, 64 * 1024, cancellationToken).ConfigureAwait(false);
            using var healthDocument = JsonDocument.Parse(healthText);
            var healthRoot = healthDocument.RootElement;
            if (!string.Equals(ReadString(healthRoot, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                return OntologyCatalogSnapshot.Failure(503, reachable: true,
                    "ApplicationOntology health response was not ready.");
            }

            using var versionResponse = await _http.GetAsync("v1/catalog/version", cancellationToken).ConfigureAwait(false);
            if (!versionResponse.IsSuccessStatusCode)
            {
                return OntologyCatalogSnapshot.Failure(
                    versionResponse.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ? 503 : 502,
                    reachable: true, "ApplicationOntology catalog version returned HTTP " + (int)versionResponse.StatusCode + ".");
            }

            var versionText = await ReadBoundedContentAsync(versionResponse, 64 * 1024, cancellationToken).ConfigureAwait(false);
            using var versionDocument = JsonDocument.Parse(versionText);
            var versionRoot = versionDocument.RootElement;
            var version = ReadString(versionRoot, "catalogVersion");
            var declaredCount = ReadInt(versionRoot, "gameCount", -1);
            if (string.IsNullOrWhiteSpace(version) || version.Length > 64 || declaredCount < 0)
            {
                return OntologyCatalogSnapshot.Failure(502, reachable: true,
                    "ApplicationOntology returned an invalid catalog version response.");
            }

            using var catalogResponse = await _http.GetAsync(
                "v1/catalog/download/" + Uri.EscapeDataString(version), cancellationToken).ConfigureAwait(false);
            if (!catalogResponse.IsSuccessStatusCode)
            {
                return OntologyCatalogSnapshot.Failure(
                    catalogResponse.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ? 503 : 502,
                    reachable: true, "ApplicationOntology catalog download returned HTTP " + (int)catalogResponse.StatusCode + ".");
            }

            var catalogText = await ReadBoundedContentAsync(catalogResponse, 5 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            var result = ParseCatalog(catalogText, reachable: true);
            if (!result.Ready ||
                !string.Equals(result.CatalogVersion, version, StringComparison.Ordinal) ||
                result.GameCount != declaredCount)
            {
                return OntologyCatalogSnapshot.Failure(502, reachable: true,
                    "ApplicationOntology catalog download failed schema validation or did not match the version endpoint.");
            }

            PersistCatalog(catalogText);
            lock (_catalogSync)
                _catalogSnapshot = result;
            _log("[ontology] refreshed local catalog cache: version=" + version +
                " games=" + result.GameCount + " executableNames=" + result.ExecutableNames.Count);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OntologyCatalogSnapshot.Failure(504, reachable: false,
                "ApplicationOntology catalog refresh was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return OntologyCatalogSnapshot.Failure(504, reachable: false,
                "ApplicationOntology catalog refresh timed out.");
        }
        catch (Exception ex)
        {
            _log("[ontology] catalog refresh failed: " + ex.GetType().Name + ": " + ex.Message);
            return OntologyCatalogSnapshot.Failure(502, reachable: false,
                "ApplicationOntology catalog is unavailable or invalid.");
        }
    }

    private OntologyCatalogSnapshot ParseCatalog(string catalogText, bool reachable)
    {
        using var catalogDocument = JsonDocument.Parse(catalogText);
        var catalogRoot = catalogDocument.RootElement;
        var version = ReadString(catalogRoot, "catalogVersion");
        if (string.IsNullOrWhiteSpace(version) || version.Length > 64 ||
            !catalogRoot.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            return OntologyCatalogSnapshot.Failure(502, reachable,
                "ApplicationOntology catalog document failed schema validation.");

        var executableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var games = new List<OntologyGame>();
        var gameIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            var gameId = ReadString(entry, "gameId");
            var displayName = ReadString(entry, "displayName");
            if (string.IsNullOrWhiteSpace(gameId) || gameId.Length > 128 ||
                string.IsNullOrWhiteSpace(displayName) || displayName.Length > 128 ||
                !gameIds.Add(gameId) ||
                !entry.TryGetProperty("executableNames", out var names) ||
                names.ValueKind != JsonValueKind.Array || names.GetArrayLength() == 0)
                return OntologyCatalogSnapshot.Failure(502, reachable,
                    "ApplicationOntology catalog entry failed schema validation.");

            var gameExecutableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var nameElement in names.EnumerateArray())
            {
                if (nameElement.ValueKind != JsonValueKind.String)
                    return OntologyCatalogSnapshot.Failure(502, reachable,
                        "ApplicationOntology executable name has an invalid type.");
                var name = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(name) || name.Length > 260 ||
                    name.Contains('\\') || name.Contains('/') || name.Contains(':'))
                    return OntologyCatalogSnapshot.Failure(502, reachable,
                        "ApplicationOntology catalog contains an invalid executable name.");
                gameExecutableNames.Add(name);
                executableNames.Add(name);
            }
            var renderApis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (entry.TryGetProperty("renderApis", out var apiArray))
            {
                if (apiArray.ValueKind != JsonValueKind.Array)
                    return OntologyCatalogSnapshot.Failure(502, reachable, "Catalog renderApis must be an array.");
                foreach (var api in apiArray.EnumerateArray())
                {
                    var apiName = api.ValueKind == JsonValueKind.String ? api.GetString() : "";
                    if (apiName is not ("d3d9" or "d3d10" or "d3d11" or "d3d12" or "vulkan" or "opengl") ||
                        !renderApis.Add(apiName))
                        return OntologyCatalogSnapshot.Failure(502, reachable, "Invalid or duplicate render API.");
                }
            }

            var pinnedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!entry.TryGetProperty("sha256", out var hashes) || hashes.ValueKind != JsonValueKind.Array)
                return OntologyCatalogSnapshot.Failure(502, reachable, "Catalog sha256 must be an array.");
            foreach (var hash in hashes.EnumerateArray())
            {
                var value = hash.ValueKind == JsonValueKind.String ? hash.GetString() : "";
                if (value.Length != 64 || !System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Fa-f0-9]{64}$") ||
                    !pinnedHashes.Add(value))
                    return OntologyCatalogSnapshot.Failure(502, reachable, "Invalid or duplicate SHA-256 hash.");
            }
            games.Add(new OntologyGame(gameId, displayName, gameExecutableNames, renderApis, pinnedHashes));
        }

        var declaredCount = ReadInt(catalogRoot, "gameCount", entries.GetArrayLength());
        if (declaredCount != entries.GetArrayLength())
            return OntologyCatalogSnapshot.Failure(502, reachable,
                "ApplicationOntology catalog game count did not match its entries.");
        return new OntologyCatalogSnapshot(200, reachable, true, version,
            declaredCount, executableNames, games, null);
    }

    private void PersistCatalog(string catalogText)
    {
        var directory = Path.GetDirectoryName(_cachePath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidDataException("Local catalog cache path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _cachePath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, catalogText, new UTF8Encoding(false));
            File.Move(temporaryPath, _cachePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async Task<string> ReadBoundedContentAsync(
        HttpResponseMessage response, int maximumBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long declaredLength && declaredLength > maximumBytes)
            throw new InvalidDataException("ApplicationOntology response exceeded the configured size limit.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (target.Length + read > maximumBytes)
                throw new InvalidDataException("ApplicationOntology response exceeded the configured size limit.");
            target.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(target.ToArray());
    }

    internal (int StatusCode, object Body) Resolve(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("executablePath", out var executablePath) ||
            executablePath.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(executablePath.GetString()) ||
            executablePath.GetString().Length > 2048)
        {
            return (400, new
            {
                error = new
                {
                    code = "invalid_request",
                    message = "executablePath is required and must be at most 2048 characters."
                }
            });
        }

        if (payload.TryGetProperty("sha256", out var hash) &&
            hash.ValueKind != JsonValueKind.Null &&
            (hash.ValueKind != JsonValueKind.String ||
             !System.Text.RegularExpressions.Regex.IsMatch(hash.GetString() ?? "", "^[A-Fa-f0-9]{64}$")))
        {
            return (400, new
            {
                error = new
                {
                    code = "invalid_request",
                    message = "sha256 must contain exactly 64 hexadecimal characters."
                }
            });
        }

        var executableName = Path.GetFileName(executablePath.GetString());
        var suppliedHash = payload.TryGetProperty("sha256", out var hashValue) &&
            hashValue.ValueKind == JsonValueKind.String ? hashValue.GetString() : null;
        var resolution = ResolveExecutable(executableName, suppliedHash);
        if (resolution == null)
            return (503, new
            {
                error = new
                {
                    code = "local_catalog_unavailable",
                    message = "No valid local game catalog is available."
                }
            });
        return (200, new
        {
            status = resolution.Matched ? "matched" : "unknown",
            gameId = resolution.Matched ? resolution.GameId : null,
            displayName = resolution.Matched ? resolution.DisplayName : null,
            reason = resolution.Reason,
            catalogVersion = resolution.CatalogVersion,
            matchMethod = resolution.MatchMethod,
            identityVerified = resolution.Matched && resolution.MatchMethod == "sha256",
            renderApis = resolution.Matched ? resolution.RenderApis : Array.Empty<string>()
        });
    }

    internal OntologyResolution ResolveExecutable(string executableName, string suppliedSha256 = null)
    {
        var snapshot = LoadCachedCatalog();
        if (!snapshot.Ready || string.IsNullOrWhiteSpace(executableName))
            return null;
        var matches = snapshot.Games.Where(game => game.ExecutableNames.Contains(executableName))
            .Take(2).ToArray();
        if (matches.Length != 1)
            return new OntologyResolution(false, "", "",
                matches.Length > 1 ? "ambiguous" : "not_found",
                snapshot.CatalogVersion, Array.Empty<string>(), "none");
        var game = matches[0];
        if (game.Sha256.Count > 0)
        {
            if (string.IsNullOrEmpty(suppliedSha256))
                return new OntologyResolution(false, "", "", "hash_required",
                    snapshot.CatalogVersion, Array.Empty<string>(), "none");
            if (!game.Sha256.Contains(suppliedSha256))
                return new OntologyResolution(false, "", "", "hash_mismatch",
                    snapshot.CatalogVersion, Array.Empty<string>(), "none");
            return new OntologyResolution(true, game.GameId, game.DisplayName, "",
                snapshot.CatalogVersion, game.RenderApis.OrderBy(x => x).ToArray(), "sha256");
        }
        // A supplied hash does not prove identity when none is pinned.
        if (!string.IsNullOrEmpty(suppliedSha256))
            return new OntologyResolution(false, "", "", "hash_unverified",
                snapshot.CatalogVersion, Array.Empty<string>(), "none");
        return new OntologyResolution(true, game.GameId, game.DisplayName, "",
            snapshot.CatalogVersion, game.RenderApis.OrderBy(x => x).ToArray(), "executableName");
    }

    private static string ReadString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int ReadInt(JsonElement element, string property, int fallback)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.TryGetInt32(out var result)
            ? result
            : fallback;
    }

    public void Dispose() => _http.Dispose();
}


internal sealed record OntologyGame(
    string GameId,
    string DisplayName,
    IReadOnlySet<string> ExecutableNames,
    IReadOnlySet<string> RenderApis,
    IReadOnlySet<string> Sha256);

internal sealed record OntologyResolution(
    bool Matched,
    string GameId,
    string DisplayName,
    string Reason,
    string CatalogVersion,
    IReadOnlyList<string> RenderApis,
    string MatchMethod);

internal sealed record OntologyCatalogSnapshot(
    int StatusCode,
    bool Reachable,
    bool Ready,
    string CatalogVersion,
    int GameCount,
    IReadOnlySet<string> ExecutableNames,
    IReadOnlyList<OntologyGame> Games,
    string Error)
{
    internal static OntologyCatalogSnapshot Failure(int statusCode, bool reachable, string error) =>
        new(statusCode, reachable, false, "", 0,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<OntologyGame>(), error);
}
