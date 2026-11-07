using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenConnect.Core.Runtime;

/// <summary>Loads untrusted runtime guidance from beneath an application content root.</summary>
public class InstructionLoader
{
    public const int DefaultMaximumBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _contentRoot;
    private readonly int _maximumBytes;

    public InstructionLoader(string contentRoot, int maximumBytes = DefaultMaximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        _contentRoot = Path.GetFullPath(contentRoot);
        _maximumBytes = maximumBytes;
    }

    /// <summary>Reads the current instructions; each call observes file changes and computes its SHA-256 revision.</summary>
    public async Task<LoadedInstructions> LoadAsync(string relativePath = "agent-runtime.md", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
            throw new UnauthorizedAccessException("Instruction path must be relative to the content root.");

        var fullPath = Path.GetFullPath(Path.Combine(_contentRoot, relativePath));
        var relativeToRoot = Path.GetRelativePath(_contentRoot, fullPath);
        if (relativeToRoot == ".." || relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relativeToRoot))
            throw new UnauthorizedAccessException("Instruction path escapes the content root.");

        EnsureNoEscapingLinks(fullPath);

        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        // Validate the object actually opened, not the path before opening it: a link
        // may be replaced between the lexical link walk above and CreateFile/open(2).
        EnsureOpenedFileIsContained(stream.SafeFileHandle);
        if (stream.Length > _maximumBytes)
            throw new InvalidDataException($"Instruction file exceeds the {_maximumBytes}-byte limit.");

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var content = StrictUtf8.GetString(bytes);
        var revision = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new LoadedInstructions(content, revision);
    }

    protected virtual string ResolveOpenedFilePath(SafeFileHandle handle)
    {
        string finalPath;
        if (OperatingSystem.IsWindows())
        {
            var buffer = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
                throw new IOException("Unable to verify the opened instruction file path.");
            finalPath = buffer.ToString();
            if (finalPath.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                finalPath = "\\\\" + finalPath[8..];
            else if (finalPath.StartsWith("\\\\?\\", StringComparison.Ordinal))
                finalPath = finalPath[4..];
        }
        else if (OperatingSystem.IsLinux())
        {
            // /proc/self/fd resolves the target of this specific open descriptor.
            var fdPath = new FileInfo($"/proc/self/fd/{handle.DangerousGetHandle().ToInt32()}").ResolveLinkTarget(true);
            if (fdPath is null)
                throw new IOException("Unable to verify the opened instruction file path.");
            finalPath = fdPath.FullName;
        }
        else
        {
            // Fail closed on platforms without an implemented handle-to-path verifier.
            throw new PlatformNotSupportedException("Secure instruction path verification is not supported on this platform.");
        }

        return finalPath;
    }

    private void EnsureOpenedFileIsContained(SafeFileHandle handle)
    {
        var finalPath = Path.GetFullPath(ResolveOpenedFilePath(handle));
        var relative = Path.GetRelativePath(_contentRoot, finalPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException("Instruction path resolves outside the content root.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);

    private void EnsureNoEscapingLinks(string fullPath)
    {
        var current = _contentRoot;
        foreach (var segment in Path.GetRelativePath(_contentRoot, fullPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists)
                continue;

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
                continue;

            var resolved = Path.GetFullPath(target.FullName);
            var relative = Path.GetRelativePath(_contentRoot, resolved);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new UnauthorizedAccessException("Instruction path resolves outside the content root.");
            current = resolved;
        }
    }
}

/// <summary>Instruction text and content-derived revision. Markdown is data only and grants no runtime authority.</summary>
public sealed record LoadedInstructions(string Content, string Revision);
