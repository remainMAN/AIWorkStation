using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json;
using AIWorkStation.Models;

namespace AIWorkStation.Services;

public interface IMihomoRuntimeClient
{
    Task<JsonDocument> GetConfigsAsync(CancellationToken token = default);
    Task<JsonDocument> GetProxiesAsync(CancellationToken token = default);
    Task<JsonDocument> GetRulesAsync(CancellationToken token = default);
    Task<int> GetProxyDelayAsync(string proxyName, CancellationToken token = default);
}

public interface IMihomoApplyClient : IMihomoRuntimeClient
{
    Task PutInlineConfigAsync(string yamlPayload, CancellationToken token = default);
    Task SelectProxyAsync(string groupName, string proxyName, CancellationToken token = default);
    Task<IReadOnlyList<ProxySelection>> GetProxySelectionsAsync(CancellationToken token = default);
    Task<IReadOnlyList<RouteObservation>> GetRouteObservationsAsync(CancellationToken token = default);
}

public sealed class MihomoControllerException : IOException
{
    public MihomoControllerException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

public sealed class MihomoNamedPipeClient : IMihomoApplyClient
{
    private readonly string _pipeName;
    private readonly TimeSpan _timeout;

    public MihomoNamedPipeClient(string configuredPipePath, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(configuredPipePath))
            throw new ArgumentException("运行配置未声明 Mihomo Named Pipe。", nameof(configuredPipePath));
        _pipeName = NormalizePipeName(configuredPipePath);
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
    }

    public Task<JsonDocument> GetConfigsAsync(CancellationToken token = default) => GetJsonAsync("/configs", token);
    public Task<JsonDocument> GetProxiesAsync(CancellationToken token = default) => GetJsonAsync("/proxies", token);
    public Task<JsonDocument> GetRulesAsync(CancellationToken token = default) => GetJsonAsync("/rules", token);
    public Task<JsonDocument> GetConnectionsAsync(CancellationToken token = default) => GetJsonAsync("/connections", token);

    public async Task SelectProxyAsync(string groupName, string proxyName, CancellationToken token = default)
    {
        // Selector 会保留历史选择，Reload 后必须通过现有 Named Pipe API 显式固定本次路径。
        var path = "/proxies/" + Uri.EscapeDataString(groupName);
        var response = await SendAsync("PUT", path, JsonSerializer.Serialize(new { name = proxyName }), token);
        if (response.StatusCode is < 200 or >= 300)
            throw new MihomoControllerException(
                response.StatusCode,
                $"Mihomo 无法选择指定静态网络路径（HTTP {response.StatusCode}）。");
    }

    public async Task<int> GetProxyDelayAsync(string proxyName, CancellationToken token = default)
    {
        var url = Uri.EscapeDataString("https://api.ipify.org");
        using var json = await GetJsonAsync(
            $"/proxies/{Uri.EscapeDataString(proxyName)}/delay?url={url}&timeout=5000&expected=200-299", token);
        if (!TryGetProperty(json.RootElement, "delay", out var delay) || !delay.TryGetInt32(out var value) || value <= 0)
            throw new IOException("Mihomo 未能确认静态网络路径可用。");
        return value;
    }

    public async Task<IReadOnlyList<ProxySelection>> GetProxySelectionsAsync(CancellationToken token = default)
    {
        using var json = await GetProxiesAsync(token);
        if (!TryGetProperty(json.RootElement, "proxies", out var proxies) || proxies.ValueKind != JsonValueKind.Object) return [];
        var result = new List<ProxySelection>();
        foreach (var property in proxies.EnumerateObject())
        {
            if (!TryGetProperty(property.Value, "type", out var type) || !string.Equals(type.GetString(), "Selector", StringComparison.OrdinalIgnoreCase)) continue;
            var now = TryGetProperty(property.Value, "now", out var selection) ? selection.GetString() : null;
            var members = TryGetProperty(property.Value, "all", out var all) && all.ValueKind == JsonValueKind.Array
                ? all.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray()
                : [];
            result.Add(new(property.Name, now ?? "未选择") { Members = members });
        }
        return result;
    }

    public async Task<IReadOnlyList<RouteObservation>> GetRouteObservationsAsync(CancellationToken token = default)
    {
        using var json = await GetConnectionsAsync(token);
        if (!TryGetProperty(json.RootElement, "connections", out var connections) || connections.ValueKind != JsonValueKind.Array) return [];
        var result = new List<RouteObservation>();
        foreach (var connection in connections.EnumerateArray())
        {
            if (!TryGetProperty(connection, "metadata", out var metadata)) continue;
            var process = ReadString(metadata, "process") ?? ReadString(metadata, "processPath") ?? string.Empty;
            var rule = ReadString(connection, "rule") ?? string.Empty;
            var chains = TryGetProperty(connection, "chains", out var chainElement) && chainElement.ValueKind == JsonValueKind.Array
                ? chainElement.EnumerateArray().Select(value => value.GetString() ?? string.Empty).Where(value => value.Length > 0).ToArray()
                : [];
            var remote = ReadString(metadata, "destinationIP") ?? ReadString(metadata, "host");
            result.Add(new(process, rule, chains, remote)
            {
                ConnectionId = ReadString(connection, "id")
            });
        }
        return result;
    }

    public async Task PutInlineConfigAsync(string yamlPayload, CancellationToken token = default)
    {
        var body = JsonSerializer.Serialize(new { path = "", payload = yamlPayload });
        var response = await SendAsync("PUT", "/configs?force=true", body, token);
        if (response.StatusCode is < 200 or >= 300)
            throw new MihomoControllerException(
                response.StatusCode,
                $"Mihomo 返回 HTTP {response.StatusCode}: {Limit(response.Body)}");
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken token)
    {
        var response = await SendAsync("GET", path, null, token);
        if (response.StatusCode is < 200 or >= 300)
            throw new MihomoControllerException(
                response.StatusCode,
                $"Mihomo 返回 HTTP {response.StatusCode}: {Limit(response.Body)}");
        return JsonDocument.Parse(response.Body);
    }

    private async Task<PipeHttpResponse> SendAsync(string method, string path, string? jsonBody, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_timeout);
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);

        var bodyBytes = jsonBody is null ? [] : Encoding.UTF8.GetBytes(jsonBody);
        var request = new StringBuilder()
            .Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n")
            .Append("Host: localhost\r\n")
            .Append("Accept: application/json\r\n")
            .Append("Connection: close\r\n");
        if (jsonBody is not null)
            request.Append("Content-Type: application/json\r\nContent-Length: ").Append(bodyBytes.Length).Append("\r\n");
        request.Append("\r\n");
        await pipe.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), timeout.Token);
        if (bodyBytes.Length > 0) await pipe.WriteAsync(bodyBytes, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        return await ReadResponseAsync(pipe, timeout.Token);
    }

    private static async Task<PipeHttpResponse> ReadResponseAsync(Stream stream, CancellationToken token)
    {
        var headerBytes = new List<byte>(1024);
        var one = new byte[1];
        while (headerBytes.Count < 64 * 1024)
        {
            var read = await stream.ReadAsync(one, token);
            if (read == 0) throw new IOException("Mihomo 在返回 HTTP 头之前关闭连接。");
            headerBytes.Add(one[0]);
            var count = headerBytes.Count;
            if (count >= 4 && headerBytes[count - 4] == 13 && headerBytes[count - 3] == 10 && headerBytes[count - 2] == 13 && headerBytes[count - 1] == 10) break;
        }
        if (headerBytes.Count >= 64 * 1024) throw new InvalidDataException("Mihomo HTTP 响应头过大。");

        var headerText = Encoding.ASCII.GetString([.. headerBytes]);
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var statusParts = lines[0].Split(' ', 3);
        if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out var status)) throw new InvalidDataException("Mihomo HTTP 状态行无效。");
        var headers = lines.Skip(1)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

        byte[] body;
        if (status is 204 or 304 || headers.TryGetValue("Content-Length", out var lengthText) && lengthText == "0") body = [];
        else if (headers.TryGetValue("Content-Length", out lengthText) && int.TryParse(lengthText, out var length)) body = await ReadExactAsync(stream, length, token);
        else if (headers.TryGetValue("Transfer-Encoding", out var transfer) && transfer.Contains("chunked", StringComparison.OrdinalIgnoreCase)) body = await ReadChunkedAsync(stream, token);
        else body = await ReadToEndAsync(stream, token);
        return new(status, Encoding.UTF8.GetString(body));
    }

    private static async Task<byte[]> ReadChunkedAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream();
        while (true)
        {
            var line = await ReadLineAsync(stream, token);
            var sizeText = line.Split(';', 2)[0];
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, null, out var size)) throw new InvalidDataException("无效的 chunk 大小。");
            if (size == 0) break;
            var chunk = await ReadExactAsync(stream, size, token);
            await output.WriteAsync(chunk, token);
            await ReadExactAsync(stream, 2, token);
        }
        return output.ToArray();
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new EndOfStreamException();
            if (one[0] == 10) break;
            if (one[0] != 13) bytes.Add(one[0]);
        }
        return Encoding.ASCII.GetString([.. bytes]);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken token)
    {
        var result = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(result.AsMemory(offset), token);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        return result;
    }

    private static async Task<byte[]> ReadToEndAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, token);
        return output.ToArray();
    }

    private static string NormalizePipeName(string path)
    {
        var normalized = path.Trim().Replace('/', '\\');
        const string prefix = "\\\\.\\pipe\\";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) normalized = normalized[prefix.Length..];
        if (normalized.Length == 0 || normalized.Contains('\\')) throw new ArgumentException("Named Pipe 路径无效。", nameof(path));
        return normalized;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Limit(string value) => value.Length <= 500 ? value : value[..500];
    private sealed record PipeHttpResponse(int StatusCode, string Body);
}
