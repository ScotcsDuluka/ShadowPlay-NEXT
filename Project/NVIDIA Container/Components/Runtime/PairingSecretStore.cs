#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Windows-only, per-user DPAPI pairing secret persistence.
// NEVER log, serialize to control API, or print Secret bytes.
internal static class PairingSecretStore
{
    private const int MaxBlobBytes = 4096;
    private const int CryptProtectUiForbidden = 0x1;
    internal const string FileName = "pairing-current-user.dpapi";

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob source,
        string description, IntPtr entropy, IntPtr reserved, IntPtr prompt,
        int flags, out DataBlob protectedData);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob source,
        IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt,
        int flags, out DataBlob unprotectedData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShadowPlay NEXT", "Secrets", FileName);

    internal static bool Valid(string? secret)
    {
        if (secret == null || secret.Length != 32) return false;
        foreach (char digit in secret)
            if (!Uri.IsHexDigit(digit)) return false;
        return true;
    }

    internal static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static byte[] Crypt(byte[] source, bool protect)
    {
        if (source.Length == 0 || source.Length > MaxBlobBytes)
            throw new PairingSecretException("invalid_secret_blob");
        IntPtr input = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, input, source.Length);
            var blob = new DataBlob { Size = source.Length, Data = input };
            DataBlob output;
            bool success = protect
                ? CryptProtectData(ref blob, "ShadowPlay NEXT Pairing",
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref blob, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);
            if (!success) throw new PairingSecretException("dpapi_operation_failed");
            try
            {
                if (output.Size <= 0 || output.Size > MaxBlobBytes)
                    throw new PairingSecretException("invalid_secret_blob");
                var bytes = new byte[output.Size];
                Marshal.Copy(output.Data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                {
                    // Clear decrypted data in native DPAPI output memory before freeing.
                    if (!protect && output.Size > 0 && output.Size <= MaxBlobBytes)
                        for (int i = 0; i < output.Size; i++)
                            Marshal.WriteByte(output.Data, i, 0);
                    LocalFree(output.Data);
                }
            }
        }
        finally
        {
            // No secret-containing unmanaged memory is left allocated.
            for (int i = 0; i < source.Length; i++)
                Marshal.WriteByte(input, i, 0);
            Marshal.FreeHGlobal(input);
        }
    }

    internal static string Read(string path)
    {
        if (!File.Exists(path))
            throw new PairingSecretException("protected_secret_missing");
        try
        {
            var encrypted = File.ReadAllBytes(path);
            byte[] clear = Crypt(encrypted, false);
            try
            {
                string secret = Encoding.ASCII.GetString(clear);
                if (!Valid(secret)) throw new PairingSecretException("invalid_secret_format");
                return secret;
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (PairingSecretException) { throw; }
        catch (Exception) { throw new PairingSecretException("protected_secret_unreadable"); }
    }

    // Runtime resolution never generates or rotates. Existing source installations
    // still work with the legacy JSON until an explicit offline migration.
    internal static string Resolve(JsonElement pairing, string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path)) return Read(path); // invalid DPAPI blocks legacy fallback
        if (pairing.ValueKind == JsonValueKind.Object &&
            pairing.TryGetProperty("secret", out var value) &&
            value.ValueKind == JsonValueKind.String &&
            Valid(value.GetString()))
            return value.GetString()!;
        throw new PairingSecretException("pairing_secret_not_provisioned");
    }

    // Explicit one-time enrollment only; NEVER called during startup.
    internal static string Enroll(string path, string secret)
    {
        if (!Valid(secret)) throw new PairingSecretException("invalid_secret_format");
        if (File.Exists(path))
        {
            // A concurrent enrollment or stale value must not silently rotate.
            if (!string.Equals(Read(path), secret, StringComparison.Ordinal))
                throw new PairingSecretException("protected_secret_mismatch");
            return "already_enrolled";
        }
        byte[] plaintext = Encoding.ASCII.GetBytes(secret);
        byte[] protectedBytes;
        try { protectedBytes = Crypt(plaintext, true); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(protectedBytes, 0, protectedBytes.Length);
            file.Flush(true);
            return "enrolled";
        }
        catch (IOException)
        {
            // When CreateNew lost a race, verify the existing store without logging data.
            if (File.Exists(path) && string.Equals(Read(path), secret, StringComparison.Ordinal))
                return "already_enrolled";
            throw new PairingSecretException("protected_secret_conflict");
        }
        catch (UnauthorizedAccessException) { throw new PairingSecretException("protected_secret_access_denied"); }
        finally { CryptographicOperations.ZeroMemory(protectedBytes); }
    }

    internal static string EnrollLegacy(JsonElement pairing, string path)
    {
        if (pairing.ValueKind != JsonValueKind.Object ||
            !pairing.TryGetProperty("secret", out var legacy) ||
            legacy.ValueKind != JsonValueKind.String || !Valid(legacy.GetString()))
            throw new PairingSecretException("legacy_secret_missing");
        return Enroll(path, legacy.GetString()!);
    }

    // Rotation is offline-only. Calling this cannot update a running process's
    // MMF, already-written NvNode init, or an older binary's cached credentials.
    internal static void ReplaceOffline(string path, string replacement)
    {
        if (!File.Exists(path)) throw new PairingSecretException("protected_secret_missing");
        _ = Read(path);
        if (!Valid(replacement)) throw new PairingSecretException("invalid_secret_format");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        byte[] plaintext = Encoding.ASCII.GetBytes(replacement);
        byte[] encrypted;
        try { encrypted = Crypt(plaintext, true); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        string candidate = Path.Combine(directory, FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(encrypted, 0, encrypted.Length); file.Flush(true); }
            File.Move(candidate, path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(candidate)) File.Delete(candidate);
        }
    }
}

internal sealed class PairingSecretException : Exception
{
    internal PairingSecretException(string code) : base(code) { }
}
