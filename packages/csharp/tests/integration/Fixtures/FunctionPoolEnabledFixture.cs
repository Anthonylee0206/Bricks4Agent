using System.Net;
using System.Net.Sockets;

namespace Integration.Tests.Fixtures;

/// <summary>
/// 開啟 Function Pool 的測試主機。pool 綁在 127.0.0.1 的一個空閒埠，
/// worker 簽章不強制（用這個主機的測試驗的是 pool 與觀測，不是 worker 認證）。
/// 子類別可以再加上自己的主機設定；每個實例各用一個埠，不同的 fixture 可以同時存在。
/// </summary>
public class FunctionPoolEnabledFixture : BrokerAuthorizationFixture
{
    public FunctionPoolEnabledFixture()
        : this(FreeTcpPort(), extraSettings: null)
    {
    }

    protected FunctionPoolEnabledFixture(int poolPort, IReadOnlyDictionary<string, string?>? extraSettings)
        : base(enforceWorkerAuth: false, hostSettings: BuildSettings(poolPort, extraSettings))
    {
        PoolPort = poolPort;
    }

    public int PoolPort { get; }

    protected static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Dictionary<string, string?> BuildSettings(int poolPort, IReadOnlyDictionary<string, string?>? extraSettings)
    {
        var settings = new Dictionary<string, string?>
        {
            ["FunctionPool:Enabled"] = "true",
            ["FunctionPool:BindAddress"] = "127.0.0.1",
            ["FunctionPool:ListenPort"] = poolPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["FunctionPool:HeartbeatTimeoutSeconds"] = "30",
            // 啟動時的法律條文 RAG 種子會從網路抓全文、逐條做嵌入，每次新資料庫都要好幾分鐘；這些測試用不到。
            ["RagSeed:Enabled"] = "false",
        };

        foreach (var (key, value) in extraSettings ?? new Dictionary<string, string?>())
            settings[key] = value;

        return settings;
    }
}
