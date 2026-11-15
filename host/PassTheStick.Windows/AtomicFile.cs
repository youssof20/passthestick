namespace PassTheStick.Windows;

/// <summary>Writes a file so a crash leaves either the old content or the new content, never half of it.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content) => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(content));

    public static void WriteAllBytes(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
