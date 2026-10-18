using System;
using System.IO;

namespace PlayerApp;

internal static class MediaLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlayerApp", "logs");
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "media-worker.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must not interrupt recovery when the user's disk is unavailable.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
