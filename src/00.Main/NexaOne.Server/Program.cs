using System.Xml.Linq;
using NexaOne.Infrastructure.Persistence;
using NexusFramework;
using NexusFramework.Utils;

namespace NexaOne.Server;

internal static class Program
{
    private static readonly ApplicationServer _server = ApplicationServer.GetInstance();

    public static async Task Main(string[] args)
    {
        Console.Title = "NexaOne Server";
        Console.WriteLine("[NexaOne.Server] Starting...");

        // SQLite 모드면 컨텍스트 생성 전에 스키마를 부트스트랩한다(빈 DB일 때만, idempotent).
        // server.xml의 eesDataSource가 가리키는 Provider 타입으로 판별한다 — XML만 바꾸면 자동 적용된다.
        EnsureSqliteSchemaIfConfigured("server.xml");

        _server.CreateServer(new[] { "server.xml" });
        Console.WriteLine("[NexaOne.Server] Server context initialized.");

        XDocument doc = XDomUtility.Load("app.xml");
        XElement root = XDomUtility.GetRoot(doc);
        XElement services = XDomUtility.GetElement(root, "Services");

        foreach (XElement service in XDomUtility.GetElements(services, "Service"))
        {
            var name = service.Attribute("name")?.Value
                ?? throw new InvalidOperationException("Service element missing 'name' attribute.");
            var configFilesAttr = service.Attribute("configFiles")?.Value
                ?? throw new InvalidOperationException($"Service '{name}' missing 'configFiles' attribute.");
            var classPathsAttr = service.Attribute("classPaths")?.Value
                ?? throw new InvalidOperationException($"Service '{name}' missing 'classPaths' attribute.");

            // configFiles/classPaths 모두 ';'로 다중 항목을 지정할 수 있다. classPaths의 각 항목은 ClassLoader가
            // plugin ALC로 로드할 모듈 DLL 파일 경로다(도메인 모듈 9개).
            var splitOptions = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;
            var configFiles = configFilesAttr.Split(';', splitOptions);
            var classPaths = classPathsAttr.Split(';', splitOptions);

            _server.AddService(name, configFiles, classPaths);
            Console.WriteLine($"[NexaOne.Server] Service '{name}' registered ({classPaths.Length} module(s)).");
        }

        Console.WriteLine("[NexaOne.Server] Ready. Press Ctrl+C to stop.");

        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            tcs.TrySetResult();
        };

        await tcs.Task;

        Console.WriteLine("[NexaOne.Server] Shutting down...");
        _server.Dispose();
    }

    /// <summary>
    /// server.xml의 eesDataSource가 SQLite 공급자를 가리키면, 해당 ConnectionString에 스키마를 부트스트랩한다.
    /// MSSQL 모드면 아무 일도 하지 않는다(운영은 마이그레이션을 외부 적용). XML 파싱만으로 판별해 Spring 컨텍스트와 분리한다.
    /// </summary>
    private static void EnsureSqliteSchemaIfConfigured(string serverXmlPath)
    {
        XNamespace ns = "http://www.springframework.net";
        var doc = XDocument.Load(serverXmlPath);
        var objects = doc.Root?.Elements(ns + "object").ToList() ?? new List<XElement>();

        var dataSource = objects.FirstOrDefault(o => (string?)o.Attribute("id") == "eesDataSource");
        if (dataSource is null) return;

        var props = dataSource.Elements(ns + "property").ToList();
        var connStr = props.FirstOrDefault(p => (string?)p.Attribute("name") == "ConnectionString")?.Attribute("value")?.Value;
        var providerRef = props.FirstOrDefault(p => (string?)p.Attribute("name") == "Provider")?.Attribute("ref")?.Value;

        var providerType = objects
            .FirstOrDefault(o => (string?)o.Attribute("id") == providerRef)?
            .Attribute("type")?.Value ?? string.Empty;

        if (!providerType.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(connStr))
            throw new InvalidOperationException("SQLite 공급자가 설정됐으나 eesDataSource ConnectionString이 비어 있습니다.");

        Console.WriteLine($"[NexaOne.Server] SQLite mode — ensuring schema ({connStr})...");
        SqliteSchemaInitializer.EnsureSchema(connStr);
        Console.WriteLine("[NexaOne.Server] Schema ready.");
    }
}
