using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace KSubMaker.App.Services;

/// <summary>
/// Win32 shell implementation of <see cref="IFileActionService"/>.
///
/// Delete goes through <c>SHFileOperation</c> with <c>FOF_ALLOWUNDO</c> so it lands in the Recycle
/// Bin, and the properties sheet through <c>ShellExecuteEx</c> with the <c>properties</c> verb —
/// there is no managed API for either. Rename is a plain <see cref="File.Move(string,string)"/>
/// after the same path validation the rest of the app uses.
/// </summary>
public sealed class FileActionService(ILogger<FileActionService> logger) : IFileActionService
{
    private readonly ILogger<FileActionService> _logger = logger;

    public string? Rename(string path, string newFileName)
    {
        if (!TryNormalizeExistingFile(path, out var full))
        {
            return null;
        }

        var cleaned = newFileName?.Trim();
        if (string.IsNullOrEmpty(cleaned) || cleaned.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            _logger.LogWarning("사용할 수 없는 파일 이름입니다: {Name}", newFileName);
            return null;
        }

        var directory = Path.GetDirectoryName(full)!;
        var target = Path.Combine(directory, cleaned);

        if (string.Equals(target, full, StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        if (File.Exists(target) || Directory.Exists(target))
        {
            _logger.LogWarning("같은 이름의 항목이 이미 있습니다: {Target}", target);
            return null;
        }

        try
        {
            File.Move(full, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "파일 이름을 바꾸지 못했습니다: {From} → {To}", full, target);
            return null;
        }
    }

    public bool Move(string sourcePath, string destinationPath)
    {
        if (!TryNormalizeExistingFile(sourcePath, out var full))
        {
            return false;
        }

        string destination;

        try
        {
            destination = Path.GetFullPath(destinationPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger.LogWarning(ex, "대상 경로를 해석하지 못했습니다: {Path}", destinationPath);
            return false;
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            _logger.LogWarning("이동 대상에 항목이 이미 있습니다: {Target}", destination);
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Move(full, destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "파일을 이동하지 못했습니다: {From} → {To}", full, destination);
            return false;
        }
    }

    public bool RecycleFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var existing = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (TryNormalizeExistingFile(path, out var full))
            {
                existing.Add(full);
            }
        }

        if (existing.Count == 0)
        {
            return false;
        }

        // SHFileOperation wants a double-null-terminated list. Marshalling a managed string as
        // LPWStr copies its whole length (not up to the first '\0'), so the embedded separators
        // survive the transition — it is only the native → managed direction that stops at a null.
        var from = string.Join('\0', existing) + "\0\0";

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = from,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
        };

        var result = SHFileOperation(ref op);

        if (result != 0 || op.fAnyOperationsAborted)
        {
            _logger.LogWarning("휴지통으로 보내지 못했습니다 (코드 {Code}, 중단 {Aborted}).", result, op.fAnyOperationsAborted);
            return false;
        }

        return true;
    }

    public bool ShowProperties(string path)
    {
        if (!TryNormalizeExistingFile(path, out var full))
        {
            return false;
        }

        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            lpVerb = "properties",
            lpFile = full,
            nShow = SW_SHOW,
            fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_NOASYNC
        };

        if (!ShellExecuteEx(ref info))
        {
            _logger.LogWarning("속성 창을 열지 못했습니다: {Path}", full);
            return false;
        }

        return true;
    }

    private bool TryNormalizeExistingFile(string? path, out string full)
    {
        full = string.Empty;

        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(path.Trim());

            if (!Path.IsPathRooted(candidate) || !File.Exists(candidate))
            {
                return false;
            }

            full = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            _logger.LogDebug(ex, "경로를 해석하지 못했습니다: {Path}", path);
            return false;
        }
    }

    // ---- Win32 -------------------------------------------------------------

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    private const int SW_SHOW = 5;
    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const uint SEE_MASK_NOASYNC = 0x00000100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);
}
