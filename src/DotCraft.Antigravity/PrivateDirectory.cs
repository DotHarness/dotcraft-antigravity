using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace DotCraft.Antigravity;

internal sealed class PrivateDirectory : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private int _disposed;

    public string Path { get; }

    public PrivateDirectory(string root, string prefix = "run-") =>
        Path = Create(System.IO.Path.Combine(root, prefix + Guid.NewGuid().ToString("N")));

    public static string Create(string path)
    {
        if (Directory.Exists(path)) return path;
        var parent = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            FileSystemAclExtensions.Create(new DirectoryInfo(path), security);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    public string EnsureSubdirectory(params string[] segments)
    {
        var full = System.IO.Path.Combine([Path, .. segments]);
        Directory.CreateDirectory(full);
        return full;
    }

    public async Task<string> WriteAsync(string relativePath, string contents, CancellationToken ct)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, contents, Utf8NoBom, ct);
        ApplyPrivateFileMode(full);
        return full;
    }

    public async Task<string> WriteAtomicAsync(string relativePath, string contents, CancellationToken ct)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, contents, Utf8NoBom, ct);
        ApplyPrivateFileMode(temp);
        File.Move(temp, full, overwrite: true);
        return full;
    }

    public async Task<string> WriteBytesAsync(string relativePath, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes.ToArray(), ct);
        ApplyPrivateFileMode(full);
        return full;
    }

    public static void SweepStale(string root, string prefix = "run-")
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root, prefix + "*"))
            TryDeleteTree(dir);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        for (var attempt = 1; Directory.Exists(Path); attempt++)
        {
            if (TryDeleteTree(Path)) return;
            if (attempt >= 30) break;
            await Task.Delay(100);
        }
    }

    private static bool TryDeleteTree(string root)
    {
        try
        {
            DeleteTreeSafely(root);
            return !Directory.Exists(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Never follow symlinks during recursive removal so a linked credential or keychain stays untouched.
    private static void DeleteTreeSafely(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var dirInfo = new DirectoryInfo(directory);
        if (dirInfo.LinkTarget is not null)
        {
            dirInfo.Delete();
            return;
        }

        foreach (var entry in dirInfo.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                entry.Delete();
                continue;
            }
            if (entry is DirectoryInfo sub)
            {
                DeleteTreeSafely(sub.FullName);
            }
            else
            {
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }

        if ((dirInfo.Attributes & FileAttributes.ReadOnly) != 0)
            dirInfo.Attributes &= ~FileAttributes.ReadOnly;
        dirInfo.Delete();
    }

    private static void ApplyPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
