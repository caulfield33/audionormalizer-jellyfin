using System;
using System.IO;

namespace Jellyfin.Plugin.AudioNormalizer.Services;

/// <summary>
/// Tests whether the plugin can actually put a file next to a video. Jellyfin only discovers
/// external audio in the media's own folder, so this is a hard requirement rather than a
/// preference, and it is worth answering precisely.
/// </summary>
public static class FolderAccess
{
    /// <summary>Checks that a file can be created and then removed in a folder.</summary>
    /// <param name="folder">Folder to test.</param>
    /// <param name="reason">The operating system's own message when the test fails, otherwise null.</param>
    /// <returns>True when the folder accepts a new file.</returns>
    public static bool IsWritable(string folder, out string? reason)
    {
        var probe = Path.Combine(folder, ".audionormalizer-probe-" + Guid.NewGuid().ToString("N"));

        try
        {
            // Create, close, then delete - deliberately NOT FileOptions.DeleteOnClose with
            // FileShare.None. Those map onto a delete-on-close share request that SMB/CIFS and
            // some FUSE mounts refuse outright, so a perfectly writable network share came back
            // read-only. Create-then-delete is also exactly what generating a track does.
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            reason = ex.Message;
            return false;
        }

        try
        {
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating but not deleting means the temp .an-part file could never be cleaned up,
            // so this counts as a failure - and it names the stray file rather than hiding it.
            reason = "a file could be created but not deleted (" + probe + "): " + ex.Message;
            return false;
        }

        reason = null;
        return true;
    }
}
