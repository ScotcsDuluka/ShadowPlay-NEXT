using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;

internal sealed class ApplicationOntologyClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Action<string> _log;

    internal ApplicationOntologyClient(string baseUrl, int timeoutSeconds, Action<string> log)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
            throw new ArgumentException("ApplicationOntology base URL must be an absolute HTTP(S) URL without credentials, query, or fragment.", nameof(baseUrl));

        _baseUrl = baseUri.ToString().TrimEnd('/') + "/";
        _log = log;
        _http = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 2, 15))
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NvContainer-ApplicationOntology/1.0");
    }

    internal string BaseUrl => _baseUrl;

    internal (int StatusCode, object Body) GetStatus()
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
            using var catalogDocument = JsonDocument.Parse(catalogText);
            var catalogRoot = catalogDocument.RootElement;
            if (!string.Equals(ReadString(catalogRoot, "catalogVersion"), version, StringComparison.Ordinal) ||
                !catalogRoot.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != declaredCount)
            {
                return OntologyCatalogSnapshot.Failure(502, reachable: true,
                    "ApplicationOntology catalog download did not match the version endpoint.");
            }

            var executableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var gameIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                var gameId = ReadString(entry, "gameId");
                var displayName = ReadString(entry, "displayName");
                if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(displayName) ||
                    !gameIds.Add(gameId) ||
                    !entry.TryGetProperty("executableNames", out var names) ||
                    names.ValueKind != JsonValueKind.Array || names.GetArrayLength() == 0)
                {
                    return OntologyCatalogSnapshot.Failure(502, reachable: true,
                        "ApplicationOntology catalog entry failed schema validation.");
                }

                foreach (var nameElement in names.EnumerateArray())
                {
                    if (nameElement.ValueKind != JsonValueKind.String) return OntologyCatalogSnapshot.Failure(
                        502, reachable: true, "ApplicationOntology executable name has an invalid type.");
                    var name = nameElement.GetString();
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 260 ||
                        name.Contains('\\') || name.Contains('/') || name.Contains(':'))
                    {
                        return OntologyCatalogSnapshot.Failure(502, reachable: true,
                            "ApplicationOntology catalog contains an invalid executable name.");
                    }
                    executableNames.Add(name);
                }
            }

            var result = new OntologyCatalogSnapshot(
                200, true, true, version, declaredCount, executableNames, null);
            _log("[ontology] catalog snapshot ready: version=" + version + " games=" + declaredCount +
                " executableNames=" + executableNames.Count);
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

        try
        {
            using var content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");
            using var response = _http.PostAsync("v1/games/resolve", content).GetAwaiter().GetResult();
            var responseText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            object responseBody;
            try
            {
                using var document = JsonDocument.Parse(responseText);
                responseBody = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                _log("[ontology] resolve returned invalid JSON: HTTP " + (int)response.StatusCode);
                return (502, new { error = "ApplicationOntology returned invalid JSON.", upstreamStatusCode = (int)response.StatusCode });
            }

            if (!response.IsSuccessStatusCode)
                _log("[ontology] resolve returned HTTP " + (int)response.StatusCode);

            return ((int)response.StatusCode, responseBody);
        }
        catch (OperationCanceledException)
        {
            _log("[ontology] resolve request timed out");
            return (504, new { error = new { code = "ontology_timeout", message = "ApplicationOntology request timed out." } });
        }
        catch (Exception ex)
        {
            _log("[ontology] resolve request failed: " + ex.GetType().Name + ": " + ex.Message);
            return (502, new { error = new { code = "ontology_unavailable", message = "ApplicationOntology could not be reached." } });
        }
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


internal sealed record OntologyCatalogSnapshot(
    int StatusCode,
    bool Reachable,
    bool Ready,
    string CatalogVersion,
    int GameCount,
    IReadOnlySet<string> ExecutableNames,
    string Error)
{
    internal static OntologyCatalogSnapshot Failure(int statusCode, bool reachable, string error) =>
        new(statusCode, reachable, false, "", 0,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), error);
}
