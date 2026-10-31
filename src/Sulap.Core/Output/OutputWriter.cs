using System.Runtime.InteropServices;

namespace Sulap.Core.Output;

/// <summary>The output folder can't be written to (Program Files, a read-only drive). Ask for another one.</summary>
public sealed class OutputFolderNotWritableException(string folder, Exception inner)
    : IOException($"Can't save files in {folder}.", inner)
{
    public string Folder { get; } = folder;
}

/// <summary>
/// Writes results the safe way: into a hidden temp file (or folder) next to the final name, renamed
/// into place only once it's complete, without ever replacing an existing file. Explorer is told
/// about the new item right away.
/// </summary>
public static class OutputWriter
{
    private const int ErrorWriteProtect = unchecked((int)0x80070013);
    private const int ErrorAccessDenied = unchecked((int)0x80070005);

    /// <summary>Fails early with <see cref="OutputFolderNotWritableException"/> when the folder can't be written to.</summary>
    public static void EnsureWritable(string folder)
    {
        var probe = Path.Combine(folder, TempName(".probe"));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception ex) when (IsNotWritable(ex))
        {
            throw new OutputFolderNotWritableException(folder, ex);
        }
    }

    /// <returns>The path of the finished file.</returns>
    public static async Task<string> WriteFileAsync(
        string folder, string baseName, string extension, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(folder, TempName(extension));
        FileStream stream;
        try
        {
            stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (IsNotWritable(ex))
        {
            throw new OutputFolderNotWritableException(folder, ex);
        }

        try
        {
            await using (stream)
            {
                File.SetAttributes(tempPath, FileAttributes.Hidden);
                await write(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            return MoveIntoPlace(tempPath, folder, baseName, extension, isFolder: false);
        }
        catch
        {
            TryDelete(tempPath, isFolder: false);
            throw;
        }
    }

    /// <summary>For writers that insist on creating the file themselves (Word): they get a path to save to.</summary>
    public static async Task<string> WriteFileByPathAsync(
        string folder, string baseName, string extension, Func<string, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        EnsureWritable(folder);
        var tempPath = Path.Combine(folder, TempName(extension));
        try
        {
            await write(tempPath, cancellationToken);
            if (!File.Exists(tempPath))
            {
                throw new IOException("The converter didn't produce a file.");
            }

            return MoveIntoPlace(tempPath, folder, baseName, extension, isFolder: false);
        }
        catch
        {
            TryDelete(tempPath, isFolder: false);
            throw;
        }
    }

    /// <summary>Writes several files into a hidden temp folder, then renames the folder into place.</summary>
    public static async Task<string> WriteFolderAsync(
        string parent, string folderName, Func<string, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        var tempFolder = Path.Combine(parent, TempName(string.Empty));
        try
        {
            Directory.CreateDirectory(tempFolder);
            File.SetAttributes(tempFolder, FileAttributes.Directory | FileAttributes.Hidden);
        }
        catch (Exception ex) when (IsNotWritable(ex))
        {
            throw new OutputFolderNotWritableException(parent, ex);
        }

        try
        {
            await write(tempFolder, cancellationToken);
            return MoveIntoPlace(tempFolder, parent, folderName, string.Empty, isFolder: true);
        }
        catch
        {
            TryDelete(tempFolder, isFolder: true);
            throw;
        }
    }

    /// <summary>The temp names end with the real extension, because some writers (Word) check it.</summary>
    internal static string TempName(string extension) => $"~{AppIdentity.Name}-{Guid.NewGuid():N}.tmp{extension}";

    private static string MoveIntoPlace(string tempPath, string folder, string baseName, string extension, bool isFolder)
    {
        for (var attempt = 1; attempt <= 10_000; attempt++)
        {
            var target = Path.Combine(folder, OutputNames.Candidate(baseName, extension, attempt));
            if (File.Exists(target) || Directory.Exists(target))
            {
                continue;
            }

            try
            {
                // Neither move replaces an existing item, so a name taken a moment ago just moves on to the next one.
                if (isFolder)
                {
                    Directory.Move(tempPath, target);
                }
                else
                {
                    File.Move(tempPath, target, overwrite: false);
                }
            }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target))
            {
                continue;
            }

            var attributes = File.GetAttributes(target);
            File.SetAttributes(target, attributes & ~FileAttributes.Hidden);
            ShellNotify.Created(target, isFolder);
            return target;
        }

        throw new IOException($"Couldn't find a free name for {baseName}{extension}.");
    }

    private static bool IsNotWritable(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex is IOException io && io.HResult is ErrorWriteProtect or ErrorAccessDenied);

    private static void TryDelete(string path, bool isFolder)
    {
        // Another process (Word, an antivirus scan) may hold the file for a moment.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (isFolder)
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                }
                else if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leaving a hidden temp file behind is better than hiding the real error.
                Thread.Sleep(100 * attempt);
            }
        }
    }
}

/// <summary>Tells File Explorer about new items so they appear without waiting for its next refresh.</summary>
internal static class ShellNotify
{
    private const int SHCNE_CREATE = 0x00000002;
    private const int SHCNE_MKDIR = 0x00000008;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_FLUSHNOWAIT = 0x3000;

    public static void Created(string path, bool isFolder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            SHChangeNotify(isFolder ? SHCNE_MKDIR : SHCNE_CREATE, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, path, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);
}
