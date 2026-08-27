using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using AIWorkStation.Models;
using AIWorkStation.Services;

namespace AIWorkStation.Tests;

public sealed class NodeLatencyTests
{
    [Fact]
    public async Task CurrentSelectedNode_IsTestedFirst()
    {
        var order = new List<string>();
        var client = new FakeRuntimeClient((name, _) =>
        {
            lock (order) order.Add(name);
            return Task.FromResult(32);
        });
        var nodes = Nodes("普通 01", "当前节点", "普通 02");

        await new NodeLatencyTester(client).TestAllAsync(nodes, "当前节点");

        Assert.Equal("当前节点", order[0]);
        Assert.All(nodes, node => Assert.Equal(LatencyStatus.Available, node.LatencyStatus));
    }

    [Fact]
    public async Task TestAllNodes_UsesBoundedConcurrency()
    {
        var active = 0;
        var maximum = 0;
        var client = new FakeRuntimeClient(async (_, token) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximum, current);
            try
            {
                await Task.Delay(40, token);
                return 24;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        await new NodeLatencyTester(client).TestAllAsync(
            Enumerable.Range(1, 12).Select(index => Node($"节点 {index:00}")).ToArray(), null);

        Assert.Equal(NodeLatencyTester.DefaultMaxConcurrency, maximum);
        Assert.True(maximum <= 4);
    }

    [Fact]
    public async Task SingleNodeTimeout_DoesNotAbortAll()
    {
        var client = new FakeRuntimeClient(async (name, token) =>
        {
            if (name == "超时节点") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 18;
        });
        var nodes = Nodes("可用 01", "超时节点", "可用 02");
        var tester = new NodeLatencyTester(client, maxConcurrency: 2, nodeTimeout: TimeSpan.FromMilliseconds(40));

        await tester.TestAllAsync(nodes, null);

        Assert.Equal(LatencyStatus.Timeout, nodes.Single(node => node.Name == "超时节点").LatencyStatus);
        Assert.All(nodes.Where(node => node.Name != "超时节点"),
            node => Assert.Equal(LatencyStatus.Available, node.LatencyStatus));
    }

    [Fact]
    public async Task LatencyResult_DoesNotBlockApply()
    {
        var node = Node("失败节点");
        var subscription = new SubscriptionInfo("uid", "profile", "profile.yaml", "profile.yaml", "hash",
            [node], ExtensionOwnership.NoneOrEmpty, null, null, null);
        var machine = new MachineInfo("Windows", "11", "26100", "x64", "UTC", "UTC", TimeSpan.Zero, true);
        var snapshot = new EnvironmentSnapshot(EnvironmentSupport.Supported, "可继续", machine, null, subscription, null);
        var client = new FakeRuntimeClient((_, _) => Task.FromException<int>(new IOException("单节点失败")));

        await new NodeLatencyTester(client).TestAllAsync([node], node.Name);

        Assert.Equal(LatencyStatus.Failed, node.LatencyStatus);
        Assert.Equal(EnvironmentSupport.Supported, snapshot.Support);
        Assert.Equal("可继续", snapshot.ReasonZh);
    }

    [Fact]
    public async Task ProxyName_IsUrlEncoded()
    {
        var pipeName = "aiws-delay-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        string? request = null;
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            request = await ReadHeadersAsync(server);
            var body = Encoding.UTF8.GetBytes("{\"delay\":32}");
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await server.WriteAsync(headers);
            await server.WriteAsync(body);
            await server.FlushAsync();
        });

        var delay = await new MihomoNamedPipeClient(@"\\.\pipe\" + pipeName)
            .GetProxyDelayAsync("台湾 031");
        await serverTask;

        Assert.Equal(32, delay);
        Assert.Contains(
            "GET /proxies/%E5%8F%B0%E6%B9%BE%20031/delay?url=https%3A%2F%2Fapi.ipify.org&timeout=5000&expected=200-299 HTTP/1.1",
            Assert.IsType<string>(request));
    }

    [Fact]
    public async Task LatencyCancellation_StopsRemainingTests()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var client = new FakeRuntimeClient(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 1;
        });
        var nodes = Nodes("节点 01", "节点 02", "节点 03");
        var tester = new NodeLatencyTester(client, maxConcurrency: 1);
        using var cancellation = new CancellationTokenSource();

        var testTask = tester.TestAllAsync(nodes, null, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => testTask);
        Assert.Equal(1, calls);
        Assert.All(nodes, node => Assert.Equal(LatencyStatus.NotTested, node.LatencyStatus));
    }

    [Fact]
    public void ProxyNodeInfo_LatencyFieldsNotifyAndUseChineseDisplay()
    {
        var node = Node("节点");
        var changes = new List<string?>();
        node.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        node.LatencyMs = 156;
        node.LatencyTestedAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        node.LatencyStatus = LatencyStatus.Available;

        Assert.Equal("156 ms", node.LatencyDisplay);
        Assert.Equal("可用", node.Status);
        Assert.Contains(nameof(ProxyNodeInfo.LatencyDisplay), changes);
        Assert.Contains(nameof(ProxyNodeInfo.LatencyTestedAtDisplay), changes);
        Assert.Contains(nameof(ProxyNodeInfo.Status), changes);
    }

    private static ProxyNodeInfo[] Nodes(params string[] names) => names.Select(Node).ToArray();

    private static ProxyNodeInfo Node(string name) => new(name, "ss", "node.example", Array.Empty<IPAddress>());

    private static void UpdateMaximum(ref int maximum, int current)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (current <= observed || Interlocked.CompareExchange(ref maximum, current, observed) == observed) return;
        }
    }

    private static async Task<string> ReadHeadersAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 64 * 1024)
        {
            if (await stream.ReadAsync(one) == 0) break;
            bytes.Add(one[0]);
            var count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 &&
                bytes[count - 2] == 13 && bytes[count - 1] == 10) break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private sealed class FakeRuntimeClient(
        Func<string, CancellationToken, Task<int>> delay) : IMihomoRuntimeClient
    {
        public Task<int> GetProxyDelayAsync(string proxyName, CancellationToken token = default)
            => delay(proxyName, token);

        public Task<JsonDocument> GetConfigsAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<JsonDocument> GetProxiesAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<JsonDocument> GetRulesAsync(CancellationToken token = default) => throw new NotSupportedException();
    }
}
