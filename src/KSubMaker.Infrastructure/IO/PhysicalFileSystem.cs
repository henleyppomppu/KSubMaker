using KSubMaker.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Infrastructure.IO;

/// <summary>
/// <see cref="IFileSystem"/> over <see cref="System.IO"/>.
///
/// The scanner walks user-chosen trees that routinely contain unreadable system folders, dead
/// network shares and junctions pointing at themselves. The contract is therefore "degrade, never
/// throw" for enumeration: a folder we cannot read contributes nothing instead of aborting the scan.
/// Mutating operations (create/move/delete) still throw, because the caller must know they failed.
/// </summary>
public sealed class PhysicalFileSystem(ILogger<PhysicalFileSystem> logger) : IFileSystem
{
    private static readonly EnumerationOptions Options = new()
    {
        // Reparse points are returned so the *caller* can decide; the scanner needs to see them in
        // order to run its cycle guard.
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    private readonly ILogger<PhysicalFileSystem> _logger = logger;

    public bool DirectoryExists(string path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    public bool FileExists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public IEnumerable<string> EnumerateDirectories(string path) =>
        SafeEnumerate(path, static p => Directory.EnumerateDirectories(p, "*", Options), "하위 폴더");

    public IEnumerable<string> EnumerateFiles(string path) =>
        SafeEnumerate(path, static p => Directory.EnumerateFiles(p, "*", Options), "파일");

    /// <summary>
    /// FileInfo objects from an enumeration come with size, dates and attributes already filled in
    /// from the directory listing, so this costs one listing however many files there are.
    /// </summary>
    public IEnumerable<FileEntry> EnumerateFileEntries(string path) =>
        SafeEnumerate(
            path,
            static p => new DirectoryInfo(p).EnumerateFiles("*", Options).Select(static f => new FileEntry(
                f.FullName,
                f.Length,
                f.LastWriteTimeUtc,
                (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)),
            "파일");

    /// <summary>
    /// Materialises the enumeration inside the try/catch. <c>Directory.EnumerateX</c> is lazy, so a
    /// naive <c>try { yield return ... }</c> would let the exception escape at the *caller's*
    /// MoveNext, which is exactly the failure mode this method exists to prevent. IgnoreInaccessible
    /// covers denials on children; the try/catch covers a denial on the directory itself.
    /// </summary>
    private T[] SafeEnumerate<T>(string path, Func<string, IEnumerable<T>> enumerate, string what)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        try
        {
            return enumerate(path).ToArray();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "{What} 목록을 읽을 권한이 없습니다: {Path}", what, path);
            return [];
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger.LogDebug(ex, "{What} 목록을 읽는 중 폴더가 사라졌습니다: {Path}", what, path);
            return [];
        }
        catch (IOException ex)
        {
            // Disconnected network share, device not ready, path too long on a legacy volume.
            _logger.LogDebug(ex, "{What} 목록을 읽지 못했습니다: {Path}", what, path);
            return [];
        }
    }

    public bool IsHidden(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(ex, "숨김 속성을 확인하지 못했습니다: {Path}", path);
            return false;
        }
    }

    public bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(ex, "링크 여부를 확인하지 못했습니다: {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Canonical identity for cycle detection. Symlinks and junctions are followed to their final
    /// target, so <c>C:\A\link -&gt; C:\A</c> resolves to the same string as <c>C:\A</c> and the
    /// scanner's visited-set rejects it on the second visit.
    /// </summary>
    public string GetRealPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var resolved = path;

        try
        {
            if (IsReparsePoint(path))
            {
                // returnFinalTarget walks a whole chain of links, not just the first hop.
                var target = Directory.Exists(path)
                    ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)
                    : File.ResolveLinkTarget(path, returnFinalTarget: true);

                if (target is not null)
                {
                    resolved = target.FullName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A broken link (or a link to an offline share) still needs a stable identity, so fall
            // back to the literal path rather than failing the walk.
            _logger.LogDebug(ex, "링크 대상을 확인하지 못해 원본 경로를 사용합니다: {Path}", path);
        }

        try
        {
            resolved = Path.GetFullPath(resolved);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger.LogDebug(ex, "경로를 정규화하지 못했습니다: {Path}", resolved);
        }

        return Normalize(resolved);
    }

    /// <summary>
    /// Trailing separators and case are normalised so <c>C:\Videos\</c>, <c>C:\videos</c> and
    /// <c>C:\Videos</c> compare equal. The volume root (<c>C:\</c>, <c>/</c>) keeps its separator
    /// because stripping it would produce a drive-relative path with a completely different meaning.
    /// </summary>
    private static string Normalize(string path)
    {
        if (path.Length > 1)
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // "C:" alone is drive-relative; keep "C:\". "/" trims to "" so keep the original too.
            if (trimmed.Length > 0 && !trimmed.EndsWith(':'))
            {
                path = trimmed;
            }
        }

        // Windows and macOS are case-insensitive; the scanner's HashSet already uses
        // OrdinalIgnoreCase, so lower-casing here only guarantees identical strings for identical
        // entries on Linux-hosted tests as well.
        return OperatingSystem.IsLinux() ? path : path.ToLowerInvariant();
    }

    public long GetFileSize(string path) => new FileInfo(path).Length;

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// <see cref="FileMode.CreateNew"/> on purpose: every caller writes to a <c>*.tmp</c> that must
    /// not already exist, so a collision is a bug we want to hear about rather than silently
    /// truncating someone else's in-flight file.
    /// </summary>
    public Stream CreateNew(string path) =>
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    public void Move(string source, string destination, bool overwrite) =>
        File.Move(source, destination, overwrite);

    public void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            return;
        }

        File.Delete(path);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public long GetAvailableFreeSpace(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return 0L;
            }

            // AvailableFreeSpace honours per-user disk quotas; TotalFreeSpace does not.
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "여유 디스크 공간을 확인하지 못했습니다: {Path}", path);
            return 0L;
        }
    }
}
