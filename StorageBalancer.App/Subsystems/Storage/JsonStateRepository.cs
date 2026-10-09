using System;
using System.IO;
using System.IO.Compression;
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
            using (var fileStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal, leaveOpen: true);
                JsonSerializer.Serialize(gzipStream, snapshot, _jsonOptions);
            }

            File.Move(temporaryPath, filePath + ".gz", overwrite: true);
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
        bool isGzip = false;

        // Auto-detect GZip compression via magic bytes 0x1F, 0x8B
        if (stream.Length >= 2)
        {
            int b1 = stream.ReadByte();
            int b2 = stream.ReadByte();
            stream.Position = 0;
            if (b1 == 0x1F && b2 == 0x8B)
            {
                isGzip = true;
            }
        }

        if (isGzip)
        {
            using var gzipStream = new GZipStream(stream, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<PoolSnapshot>(gzipStream, _jsonOptions);
        }
        else
        {
            return JsonSerializer.Deserialize<PoolSnapshot>(stream, _jsonOptions);
        }
    }
}