namespace Querithm.Core.Store;

/// <summary>
/// L4 (§13 rule 1, §8): on Unix the data folder, <c>store.db</c> (with its -wal / -shm) and the logs belong to the user
/// alone — folders 0700, files 0600 — whatever the umask. Existing ones with wider permissions are narrowed at start.
/// Windows: the folders sit in the user's profile, whose ACL already keeps other users out; nothing changes there.
/// </summary>
public static class PrivateFiles
{
    public const UnixFileMode FolderMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode GroupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.SetGroup | UnixFileMode.SetUser | UnixFileMode.StickyBit;

    /// <summary>Creates the folder (0700; its parents as usual) or narrows an existing one.</summary>
    public static void Folder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        if (!Directory.Exists(path))
        {
            if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
            Directory.CreateDirectory(path, FolderMode);
        }
        Narrow(path, FolderMode);
    }

    /// <summary>Creates an empty file 0600 when it is missing (SQLite opens an empty file as a new database and gives its
    /// -wal / -shm the main file's mode), or narrows an existing one.</summary>
    public static void File(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        if (!System.IO.File.Exists(path))
        {
            try
            {
                using var _ = new FileStream(path, new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = FileMode });
            }
            catch (IOException) when (System.IO.File.Exists(path)) { /* created meanwhile: narrowed below */ }
        }
        Narrow(path, FileMode);
    }

    /// <summary>Narrows a file that exists (a store's -wal / -shm); a missing one is left alone.</summary>
    public static void NarrowIfExists(string path)
    {
        if (!OperatingSystem.IsWindows() && System.IO.File.Exists(path)) Narrow(path, FileMode);
    }

    static void Narrow(string path, UnixFileMode wanted)
    {
        if (OperatingSystem.IsWindows()) return;
        var mode = System.IO.File.GetUnixFileMode(path);
        if ((mode & GroupOrOther) != 0) System.IO.File.SetUnixFileMode(path, mode & wanted);
    }
}
