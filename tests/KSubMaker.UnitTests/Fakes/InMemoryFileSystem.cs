using KSubMaker.Application.Abstractions;

namespace KSubMaker.UnitTests.Fakes;

/// <summary>
/// A purely in-memory <see cref="IFileSystem"/>.
///
/// Paths always use '/' so that <see cref="Path.GetDirectoryName(string)"/> and friends — which the
/// production code calls directly — behave identically on Linux and Windows.
///
/// The interesting capabilities beyond "a dictionary of files" are the ones the scanner has to cope
/// with: directories that deny access, and symbolic links whose <see cref="GetRealPath"/> collapses
/// back onto an ancestor, producing a structurally infinite tree.
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    private readonly Dictionary<string, DirectoryEntry> _directories = new(Comparer);
    private readonly Dictionary<string, FileEntry> _files = new(Comparer);

    /// <summary>link path → target path. Applied repeatedly by <see cref="ResolveReal"/>.</summary>
    private readonly Dictionary<string, string> _links = new(Comparer);

    private readonly HashSet<string> _accessDeniedDirectories = new(Comparer);
    private readonly HashSet<string> _realPathFailures = new(Comparer);

    public InMemoryFileSystem() => _directories["/"] = new DirectoryEntry("/");

    public long AvailableFreeSpace { get; set; } = 500L * 1024 * 1024 * 1024;

    /// <summary>Every path handed to <see cref="GetRealPath"/>, in call order. Handy for diagnostics.</summary>
    public List<string> RealPathCalls { get; } = [];

    // -----------------------------------------------------------------------
    // building
    // -----------------------------------------------------------------------

    public InMemoryFileSystem AddDirectory(string path, bool hidden = false)
    {
        EnsureDirectory(Normalize(path)).Hidden = hidden;
        return this;
    }

    public InMemoryFileSystem AddFile(
        string path,
        long size = 4096,
        bool hidden = false,
        DateTime? lastWriteUtc = null,
        byte[]? content = null)
    {
        var normalized = Normalize(path);
        var parent = ParentOf(normalized) ?? "/";
        var name = NameOf(normalized);

        var directory = EnsureDirectory(parent);
        if (!directory.Files.Contains(name, Comparer))
        {
            directory.Files.Add(name);
        }

        _files[normalized] = new FileEntry
        {
            Hidden = hidden,
            Size = content?.LongLength ?? size,
            LastWriteUtc = lastWriteUtc ?? new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Content = content ?? []
        };

        return this;
    }

    /// <summary>
    /// Registers a directory symlink. Enumerating <paramref name="linkPath"/> yields the *target's*
    /// children re-rooted under the link, so a link pointing at an ancestor produces an infinitely
    /// deep tree — exactly what the scanner's cycle guard has to survive.
    /// </summary>
    public InMemoryFileSystem AddSymlinkDirectory(string linkPath, string targetPath)
    {
        var link = Normalize(linkPath);
        var target = Normalize(targetPath);

        var parent = ParentOf(link) ?? "/";
        var directory = EnsureDirectory(parent);
        var name = NameOf(link);

        if (!directory.Directories.Contains(name, Comparer))
        {
            directory.Directories.Add(name);
        }

        _links[link] = target;
        return this;
    }

    /// <summary>Makes both enumeration methods throw for this directory, as a denied ACL would.</summary>
    public InMemoryFileSystem DenyAccess(string path)
    {
        _accessDeniedDirectories.Add(Normalize(path));
        return this;
    }

    /// <summary>Makes <see cref="GetRealPath"/> throw for this directory.</summary>
    public InMemoryFileSystem FailRealPath(string path)
    {
        _realPathFailures.Add(Normalize(path));
        return this;
    }

    private DirectoryEntry EnsureDirectory(string path)
    {
        if (_directories.TryGetValue(path, out var existing))
        {
            return existing;
        }

        var entry = new DirectoryEntry(path);
        _directories[path] = entry;

        var parent = ParentOf(path);
        if (parent is not null)
        {
            var parentEntry = EnsureDirectory(parent);
            var name = NameOf(path);
            if (!parentEntry.Directories.Contains(name, Comparer))
            {
                parentEntry.Directories.Add(name);
            }
        }

        return entry;
    }

    // -----------------------------------------------------------------------
    // IFileSystem
    // -----------------------------------------------------------------------

    public bool DirectoryExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && _directories.ContainsKey(ResolveReal(Normalize(path)));

    public bool FileExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && _files.ContainsKey(ResolveReal(Normalize(path)));

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        var literal = Normalize(path);
        ThrowIfDenied(literal);

        var real = ResolveReal(literal);
        ThrowIfDenied(real);

        return _directories.TryGetValue(real, out var entry)
            ? entry.Directories.Select(name => Join(literal, name)).ToArray()
            : [];
    }

    /// <summary>How many times each folder's files were listed. A network listing is a round trip.</summary>
    public Dictionary<string, int> FileListings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> EnumerateFiles(string path)
    {
        FileListings[path] = FileListings.GetValueOrDefault(path) + 1;
        var literal = Normalize(path);
        ThrowIfDenied(literal);

        var real = ResolveReal(literal);
        ThrowIfDenied(real);

        return _directories.TryGetValue(real, out var entry)
            ? entry.Files.Select(name => Join(literal, name)).ToArray()
            : [];
    }

    public bool IsHidden(string path)
    {
        var real = ResolveReal(Normalize(path));

        if (_files.TryGetValue(real, out var file))
        {
            return file.Hidden;
        }

        return _directories.TryGetValue(real, out var directory) && directory.Hidden;
    }

    public bool IsReparsePoint(string path) => _links.ContainsKey(Normalize(path));

    public string GetRealPath(string path)
    {
        var literal = Normalize(path);
        RealPathCalls.Add(literal);

        if (_realPathFailures.Contains(literal))
        {
            throw new UnauthorizedAccessException($"실제 경로를 확인할 수 없습니다: {literal}");
        }

        return ResolveReal(literal);
    }

    public long GetFileSize(string path) => Require(path).Size;

    public DateTime GetLastWriteTimeUtc(string path) => Require(path).LastWriteUtc;

    public Stream OpenRead(string path) => new MemoryStream(Require(path).Content, writable: false);

    public Stream CreateNew(string path)
    {
        var normalized = Normalize(path);
        if (_files.ContainsKey(normalized))
        {
            throw new IOException($"파일이 이미 존재합니다: {normalized}");
        }

        AddFile(normalized, size: 0, content: []);
        return new CapturingStream(this, normalized);
    }

    public void Move(string source, string destination, bool overwrite)
    {
        var from = Normalize(source);
        var to = Normalize(destination);

        if (!_files.TryGetValue(from, out var entry))
        {
            throw new FileNotFoundException("원본 파일이 없습니다.", from);
        }

        if (_files.ContainsKey(to) && !overwrite)
        {
            throw new IOException($"대상 파일이 이미 존재합니다: {to}");
        }

        Delete(from);
        AddFile(to, entry.Size, entry.Hidden, entry.LastWriteUtc, entry.Content);
    }

    public void Delete(string path)
    {
        var normalized = Normalize(path);

        if (_files.Remove(normalized))
        {
            var parent = ParentOf(normalized) ?? "/";
            if (_directories.TryGetValue(parent, out var parentEntry))
            {
                parentEntry.Files.RemoveAll(n => Comparer.Equals(n, NameOf(normalized)));
            }

            return;
        }

        if (!_directories.Remove(normalized))
        {
            return;
        }

        foreach (var key in _directories.Keys.Where(k => k.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _directories.Remove(key);
        }

        foreach (var key in _files.Keys.Where(k => k.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _files.Remove(key);
        }

        var owner = ParentOf(normalized);
        if (owner is not null && _directories.TryGetValue(owner, out var ownerEntry))
        {
            ownerEntry.Directories.RemoveAll(n => Comparer.Equals(n, NameOf(normalized)));
        }
    }

    public void CreateDirectory(string path) => EnsureDirectory(Normalize(path));

    public long GetAvailableFreeSpace(string path) => AvailableFreeSpace;

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    /// <summary>Reads back the bytes a caller wrote through <see cref="CreateNew"/>.</summary>
    public byte[] ReadAllBytes(string path) => Require(path).Content;

    public IReadOnlyCollection<string> AllFiles => _files.Keys.OrderBy(k => k, Comparer).ToArray();

    private FileEntry Require(string path)
    {
        var real = ResolveReal(Normalize(path));
        return _files.TryGetValue(real, out var entry)
            ? entry
            : throw new FileNotFoundException("파일을 찾을 수 없습니다.", real);
    }

    private void SetContent(string path, byte[] content)
    {
        if (_files.TryGetValue(path, out var entry))
        {
            entry.Content = content;
            entry.Size = content.LongLength;
        }
    }

    private void ThrowIfDenied(string path)
    {
        if (_accessDeniedDirectories.Contains(path))
        {
            throw new UnauthorizedAccessException($"폴더에 접근할 권한이 없습니다: {path}");
        }
    }

    /// <summary>
    /// Collapses link segments until a fixed point is reached. The iteration cap is a guard against a
    /// pathological *fake* configuration, never against the production cycle guard under test.
    /// </summary>
    private string ResolveReal(string path)
    {
        if (_links.Count == 0)
        {
            return path;
        }

        var current = path;

        for (var i = 0; i < 128; i++)
        {
            var replaced = false;

            foreach (var (link, target) in _links.OrderByDescending(l => l.Key.Length))
            {
                if (Comparer.Equals(current, link))
                {
                    current = target;
                    replaced = true;
                    break;
                }

                if (current.StartsWith(link + "/", StringComparison.OrdinalIgnoreCase))
                {
                    current = target + current[link.Length..];
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
            {
                break;
            }
        }

        return current;
    }

    internal static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var value = path.Replace('\\', '/');
        return value.Length > 1 ? value.TrimEnd('/') : value;
    }

    private static string Join(string directory, string name) =>
        directory == "/" ? "/" + name : directory + "/" + name;

    private static string? ParentOf(string path)
    {
        if (path == "/")
        {
            return null;
        }

        var index = path.LastIndexOf('/');
        return index switch
        {
            < 0 => null,
            0 => "/",
            _ => path[..index]
        };
    }

    private static string NameOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    private sealed class DirectoryEntry(string path)
    {
        public string Path { get; } = path;
        public bool Hidden { get; set; }
        public List<string> Directories { get; } = [];
        public List<string> Files { get; } = [];
    }

    private sealed class FileEntry
    {
        public bool Hidden { get; init; }
        public long Size { get; set; }
        public DateTime LastWriteUtc { get; init; }
        public byte[] Content { get; set; } = [];
    }

    /// <summary>Buffers writes and flushes them back into the dictionary on dispose.</summary>
    private sealed class CapturingStream(InMemoryFileSystem owner, string path) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                owner.SetContent(path, ToArray());
            }

            base.Dispose(disposing);
        }
    }
}
