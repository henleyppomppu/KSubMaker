namespace KSubMaker.Application.Abstractions;

/// <summary>Minimal file-system surface used by the scanner, so recursion can be unit tested.</summary>
public interface IFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);

    /// <summary>Immediate subdirectories. Implementations must not throw on access denial.</summary>
    IEnumerable<string> EnumerateDirectories(string path);

    /// <summary>Immediate files.</summary>
    IEnumerable<string> EnumerateFiles(string path);

    /// <summary>
    /// Immediate files with the facts the scanner needs, read in the same pass as the listing.
    /// On a network share every separate size / date / attribute lookup is a round trip; the
    /// listing already carries them. The default composes the single calls, for test doubles.
    /// </summary>
    IEnumerable<FileEntry> EnumerateFileEntries(string path) =>
        EnumerateFiles(path).Select(f => new FileEntry(f, GetFileSize(f), GetLastWriteTimeUtc(f), IsHidden(f)));

    bool IsHidden(string path);

    /// <summary>True when the entry is a symbolic link / junction / mount point.</summary>
    bool IsReparsePoint(string path);

    /// <summary>
    /// Canonical identity used for cycle detection. On Windows this is the resolved full path with
    /// links followed; a stable string is all the scanner needs.
    /// </summary>
    string GetRealPath(string path);

    long GetFileSize(string path);
    DateTime GetLastWriteTimeUtc(string path);

    Stream OpenRead(string path);
    Stream CreateNew(string path);
    void Move(string source, string destination, bool overwrite);
    void Delete(string path);
    void CreateDirectory(string path);

    /// <summary>Free bytes on the volume that contains <paramref name="path"/>.</summary>
    long GetAvailableFreeSpace(string path);
}

/// <summary>One file from a directory listing. <see cref="IsHidden"/> includes the system flag.</summary>
public sealed record FileEntry(string Path, long SizeBytes, DateTime LastWriteTimeUtc, bool IsHidden);
