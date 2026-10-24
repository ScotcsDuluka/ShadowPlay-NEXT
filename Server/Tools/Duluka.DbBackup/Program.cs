using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

if (args.Length != 2) { Console.Error.WriteLine("Usage: Duluka.DbBackup <source.db> <destination.db>"); return 2; }
var source = Path.GetFullPath(args[0]);
var dest = Path.GetFullPath(args[1]);
if (!File.Exists(source) || File.Exists(dest) || string.Equals(source, dest, StringComparison.OrdinalIgnoreCase))
{ Console.Error.WriteLine("Source missing or destination already exists."); return 3; }
try
{
    var read = new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly };
    var write = new SqliteConnectionStringBuilder { DataSource = dest, Mode = SqliteOpenMode.ReadWriteCreate };
    using (var input = new SqliteConnection(read.ToString()))
    using (var output = new SqliteConnection(write.ToString()))
    {
        input.Open();
        output.Open();
        input.BackupDatabase(output);
        using var sourceQuery = input.CreateCommand();
        using var destQuery = output.CreateCommand();
        sourceQuery.CommandText = "SELECT COUNT(*) FROM DulukaAccount";
        destQuery.CommandText = "SELECT COUNT(*) FROM DulukaAccount";
        var sourceAccounts = Convert.ToInt64(sourceQuery.ExecuteScalar());
        var destAccounts = Convert.ToInt64(destQuery.ExecuteScalar());
        if (sourceAccounts != destAccounts) throw new Exception("Account row count mismatch");
        using var check = output.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        var integrity = Convert.ToString(check.ExecuteScalar());
        if (integrity != "ok") throw new Exception("Backup integrity_check failed: " + integrity);
        Console.WriteLine("SQLITE_BACKUP_OK accounts=" + destAccounts + " integrity=ok");
    }
    using var digest = SHA256.Create();
    using var stream = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    Console.WriteLine("BACKUP_SHA256=" + Convert.ToHexString(digest.ComputeHash(stream)));
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine("SQLITE_BACKUP_FAILED: " + e.Message);
    try { if (File.Exists(dest)) File.Delete(dest); } catch { }
    return 1;
}