namespace AIWorkStation.Services;

public class AtomicFileWriter
{
    public virtual async Task WriteAsync(string targetPath, byte[] content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("目标文件没有父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // 临时文件必须落在目标同目录，Flush 到磁盘后再原子替换，避免断电留下半写文件。
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(targetPath)) File.Replace(temporaryPath, targetPath, null, ignoreMetadataErrors: false);
            else File.Move(temporaryPath, targetPath);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }
}
