using Microsoft.Win32;

namespace NvGraphics.Abstractions;

/// <summary>
/// Minimal HKCU value read/write abstraction so automated tests never touch the real registry.
/// </summary>
public interface IRegistryApi
{
    /// <summary>Returns the raw value object and its kind, or (null, null) when absent.</summary>
    (object Value, RegistryValueKind? Kind) GetValue(string subKeyPath, string name);
    /// <summary>Lists the value names, data, and kinds in one key.</summary>
    IReadOnlyList<RegistryValueEntry> GetValues(string subKeyPath);
    /// <summary>Writes a value, creating intermediate keys as needed.</summary>
    void SetValue(string subKeyPath, string name, object value, RegistryValueKind kind);
    /// <summary>Deletes one value. Returns false when key or value does not exist (safe no-op).</summary>
    bool DeleteValue(string subKeyPath, string name);
}

public sealed record RegistryValueEntry(string Name, object Value, RegistryValueKind Kind);

/// <summary>Production implementation over real HKCU.</summary>
public sealed class HkcuRegistryApi : IRegistryApi
{
    public (object Value, RegistryValueKind? Kind) GetValue(string subKeyPath, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath);
        if (key is null) return (null, null);
        if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            return (null, null);
        return (key.GetValue(name), key.GetValueKind(name));
    }

    public IReadOnlyList<RegistryValueEntry> GetValues(string subKeyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath);
        if (key is null) return Array.Empty<RegistryValueEntry>();
        return key.GetValueNames()
            .Select(name => new RegistryValueEntry(name, key.GetValue(name), key.GetValueKind(name)))
            .ToArray();
    }

    public void SetValue(string subKeyPath, string name, object value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKeyPath);
        key.SetValue(name, value, kind);
    }

    public bool DeleteValue(string subKeyPath, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: true);
        if (key is null) return false;
        var actualName = key.GetValueNames().FirstOrDefault(valueName =>
            string.Equals(valueName, name, StringComparison.OrdinalIgnoreCase));
        if (actualName is null) return false;
        key.DeleteValue(actualName, throwOnMissingValue: false);
        return true;
    }
}

/// <summary>Isolated in-memory implementation for tests and design-time use.</summary>
public sealed class InMemoryRegistryApi : IRegistryApi
{
    private sealed record Entry(object Value, RegistryValueKind Kind);
    private readonly Dictionary<string, Dictionary<string, Entry>> _keys = new();

    public (object Value, RegistryValueKind? Kind) GetValue(string subKeyPath, string name)
    {
        if (_keys.TryGetValue(subKeyPath, out var values) && values.TryGetValue(name, out var e))
            return (e.Value, e.Kind);
        return (null, null);
    }

    public IReadOnlyList<RegistryValueEntry> GetValues(string subKeyPath)
    {
        if (!_keys.TryGetValue(subKeyPath, out var values)) return Array.Empty<RegistryValueEntry>();
        return values.Select(pair => new RegistryValueEntry(pair.Key, pair.Value.Value, pair.Value.Kind)).ToArray();
    }

    public void SetValue(string subKeyPath, string name, object value, RegistryValueKind kind)
    {
        if (!_keys.TryGetValue(subKeyPath, out var values))
            _keys[subKeyPath] = values = new();
        values[name] = new Entry(value, kind);
    }

    public bool DeleteValue(string subKeyPath, string name)
    {
        return _keys.TryGetValue(subKeyPath, out var values) && values.Remove(name);
    }
}

/// <summary>Wraps another IRegistryApi and throws on SetValue — for simulating write
/// or read-back failures in tests.</summary>
public sealed class FailingWriteRegistryApi : IRegistryApi
{
    private readonly IRegistryApi _inner;
    public FailingWriteRegistryApi(IRegistryApi inner) => _inner = inner;

    public (object Value, RegistryValueKind? Kind) GetValue(string subKeyPath, string name) =>
        _inner.GetValue(subKeyPath, name);

    public IReadOnlyList<RegistryValueEntry> GetValues(string subKeyPath) =>
        _inner.GetValues(subKeyPath);

    public void SetValue(string subKeyPath, string name, object value, RegistryValueKind kind) =>
        throw new IOException("simulated registry write failure");

    public bool DeleteValue(string subKeyPath, string name) => _inner.DeleteValue(subKeyPath, name);
}
