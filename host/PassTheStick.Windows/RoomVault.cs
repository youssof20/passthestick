using System.Security.Cryptography;
using System.Text.Json;

namespace PassTheStick.Windows;

public sealed record SavedRoom(string Server, string Code, string HostToken);

/// <summary>
/// Keeps the current room's code and host token across an app restart so a crash or an update does not end the session.
/// The file is encrypted for the current Windows user, because the host token lets whoever holds it act as the host.
/// </summary>
public sealed class RoomVault
{
    private readonly string _path;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;

    public RoomVault(string path, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
    {
        _path = path;
        _protect = protect ?? (data => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
        _unprotect = unprotect ?? (data => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));
    }

    public void Save(SavedRoom room)
    {
        var bytes = _protect(JsonSerializer.SerializeToUtf8Bytes(room));
        AtomicFile.WriteAllBytes(_path, bytes);
    }

    /// <summary>The saved room, or null when there is none, it cannot be decrypted (another user, a damaged file) or it is malformed.</summary>
    public SavedRoom? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var room = JsonSerializer.Deserialize<SavedRoom>(_unprotect(File.ReadAllBytes(_path)));
            return room is { Code.Length: > 0, HostToken.Length: > 0, Server.Length: > 0 } ? room : null;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or IOException)
        {
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover file only offers a resume that will fail and be replaced.
        }
    }
}
