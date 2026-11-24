using System.Security.Cryptography;

namespace LogiPluginBackup.Core;

internal static class FileUtil
{
    /// <summary>Opens a file for reading even when the Logi service keeps it open (shared read/write/delete).</summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);

    public static long DirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                total += f.Length;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>Copies a file with shared reading (the source may be locked by a running plugin).</summary>
    public static void CopyShared(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = OpenShared(source);
        using var output = File.Create(destination);
        input.CopyTo(output);
    }

    public static void CopyDirectoryShared(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            CopyShared(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    public static string Sha256Hex(Stream s)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(s));
    }

    /// <summary>Deletes a folder, retrying a few times: a plugin DLL may stay locked for a moment after its host stops.</summary>
    public static void DeleteDirectoryWithRetry(string dir)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 6)
            {
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>
    /// Archive paths use "/", never start with "/" and never contain "..": an entry can therefore not
    /// be written outside the folder it is extracted to ("zip slip").
    /// </summary>
    public static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.StartsWith('/') || name.StartsWith('\\') || name.Contains(':'))
        {
            return false;
        }
        return !name.Replace('\\', '/').Split('/').Any(part => part == "..");
    }
}
