using System.IO;
using System.Text.Json;
using StorageBalancer.App.Domain;

namespace StorageBalancer.App.Subsystems.Storage;

public class JsonStateRepository
{
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonStateRepository()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }

    public void SaveSnapshot(PoolSnapshot snapshot, string filePath)
    {
        var directory = Path.GetDirectoryName(filePath) ?? throw new ArgumentException("Snapshot path must include a directory.", nameof(filePath));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, snapshot, _jsonOptions);

            File.Move(temporaryPath, filePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public PoolSnapshot? LoadSnapshot(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        using var stream = File.OpenRead(filePath);
        return JsonSerializer.Deserialize<PoolSnapshot>(stream, _jsonOptions);
    }
}