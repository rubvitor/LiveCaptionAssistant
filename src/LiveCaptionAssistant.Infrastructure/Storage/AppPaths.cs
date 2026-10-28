namespace LiveCaptionAssistant.Infrastructure.Storage;

public static class AppPaths
{
    private const string ApplicationFolder = "LiveCaptionAssistant";

    /// <summary>Roaming folder for settings and credentials.</summary>
    public static string RoamingDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ApplicationFolder);

    /// <summary>Machine-local folder for large files (speech models) and logs.</summary>
    public static string LocalDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ApplicationFolder);

    public static string LogsDirectory { get; } = Path.Combine(LocalDataDirectory, "logs");

    public static string ModelsDirectory { get; } = Path.Combine(LocalDataDirectory, "models");
}

internal static class AtomicFile
{
    /// <summary>Writes to a temporary file first so a crash never leaves a truncated file behind.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> contents)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("The path must include a directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
