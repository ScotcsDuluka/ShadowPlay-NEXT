using System;
using System.Collections.Generic;

var passed = 0;
void Check(string name, int expected, string method, params (string, string)[] pairs)
{
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (key, value) in pairs) headers[key] = value;
    var result = ControlRequestSecurity.Validate(method, headers);
    if (result.Status != expected)
        throw new Exception(name + ": expected " + expected + ", got " + result.Status + " (" + result.Error + ")");
    Console.WriteLine("PASS " + name);
    passed++;
}

Check("Native loopback GET allowed", 200, "GET", ("Host", "127.0.0.1:59012"));
Check("Native JSON POST allowed", 200, "POST", ("Host", "127.0.0.1:59012"),
    ("Content-Type", "application/json; charset=utf-8"));
Check("Loopback NodeJS proxy allowed", 200, "POST", ("Host", "localhost:59012"),
    ("Content-Type", "application/json"));
Check("Origin on local Node API allowed", 200, "POST", ("Host", "127.0.0.1:59012"),
    ("Origin", "http://127.0.0.1:59011"), ("Content-Type", "application/json"));
Check("Remote Host blocked", 403, "GET", ("Host", "evil.example"));
Check("Missing Host blocked", 403, "GET");
Check("DNS rebinding Host blocked", 403, "POST", ("Host", "evil.example:59012"),
    ("Content-Type", "application/json"));
Check("External Origin blocked", 403, "POST", ("Host", "127.0.0.1:59012"),
    ("Origin", "https://evil.example"), ("Content-Type", "application/json"));
Check("Null Origin blocked", 403, "POST", ("Host", "127.0.0.1:59012"),
    ("Origin", "null"), ("Content-Type", "application/json"));
Check("Cross-site fetch blocked", 403, "GET", ("Host", "127.0.0.1:59012"),
    ("Sec-Fetch-Site", "cross-site"));
Check("Form POST blocked", 415, "POST", ("Host", "127.0.0.1:59012"),
    ("Content-Type", "application/x-www-form-urlencoded"));
Check("Text POST blocked", 415, "POST", ("Host", "127.0.0.1:59012"),
    ("Content-Type", "text/plain"));
Check("No content-type POST blocked", 415, "POST", ("Host", "127.0.0.1:59012"));
Console.WriteLine("PASS: " + passed + " control request security tests");
