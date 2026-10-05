using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StorageBalancer.App.Domain;

namespace StorageBalancer.App.Subsystems.Storage;

public class JsonStateRepository
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonStateRepository(string filePath)
    {
        _filePath = filePath;

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            // Required to properly serialize/deserialize the abstract FileSystemNode hierarchy
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public void SaveSnapshot(PoolSnapshot snapshot)
    {
        // Because the tree can be large, we use a FileStream directly to avoid memory spikes
        using var stream = File.Create(_filePath);
        JsonSerializer.Serialize(stream, snapshot, _jsonOptions);
    }

    public PoolSnapshot? LoadSnapshot()
    {
        if (!File.Exists(_filePath))
            return null;

        using var stream = File.OpenRead(_filePath);
        return JsonSerializer.Deserialize<PoolSnapshot>(stream, _jsonOptions);
    }
}