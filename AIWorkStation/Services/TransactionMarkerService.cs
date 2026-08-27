using System.Text.Json;

namespace AIWorkStation.Services;

public sealed record TransactionMarker(string Stage, string BackupDirectory, IReadOnlyList<string> Targets, string ClashExecutable, string RuntimeConfigPath);
public enum TransactionMarkerReadStatus { None, Valid, Corrupt }
public sealed record TransactionMarkerReadResult(TransactionMarkerReadStatus Status, TransactionMarker? Marker);

public sealed class TransactionMarkerService
{
    public TransactionMarkerService(string? markerPath = null)
    {
        MarkerPath = markerPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIWorkStation", "transaction.json");
    }

    public string MarkerPath { get; }

    public async Task WriteAsync(TransactionMarker marker, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(marker));
        await new AtomicFileWriter().WriteAsync(MarkerPath, bytes, token);
    }

    public TransactionMarkerReadResult ReadSafe()
    {
        if (!File.Exists(MarkerPath)) return new(TransactionMarkerReadStatus.None, null);
        try
        {
            var marker = JsonSerializer.Deserialize<TransactionMarker>(File.ReadAllText(MarkerPath));
            return marker is null
                ? new(TransactionMarkerReadStatus.Corrupt, null)
                : new(TransactionMarkerReadStatus.Valid, marker);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 损坏的事务记录可能代表一次未完成写入，必须保留原文件并失败关闭，不能猜测或自动删除。
            return new(TransactionMarkerReadStatus.Corrupt, null);
        }
    }

    public void Delete()
    {
        if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
    }
}
