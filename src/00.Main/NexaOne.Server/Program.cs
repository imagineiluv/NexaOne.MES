using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        // .NET Generic Host — 비웹 background 워커(모듈 소유 + Server 실행)의 호스트(ADR-006 Phase 1~2).
        // 수명주기 관리(Ctrl+C/SIGTERM)와 게이트 기반 워커 호스팅을 담당한다. 모듈 워커의 FDC 타입은 plugin ALC에서
        // 해석돼야 하므로(NexaOne.FDC.dll은 ClassLoader가 전담 로드) 워커는 nexaone.xml 자식 컨텍스트에서 조립하고,
        // 여기서는 IHostedService(공유 프레임워크 타입)로만 당겨 등록한다 — Server가 FDC 타입을 직접 참조하지 않게.
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddSingleton(_server);   // 워커가 컨테이너 빈을 조회할 수 있도록 호스트 DI에 노출

        // FDC 실시간 수집 워커 게이트(ADR-006 Phase 2). 기본 OFF — 미설정 시 워커가 등록조차 되지 않아
        // 부팅이 기존과 동일하다(회귀 0). true일 때만 nexaone.xml의 fdcCollectionWorker 빈을 호스트에 등록한다.
        if (builder.Configuration.GetValue("Worker:Fdc:Enabled", false))
        {
            var fdcWorker = (IHostedService)_server.GetBean("NexaOne", "fdcCollectionWorker");
            builder.Services.AddSingleton<IHostedService>(fdcWorker);
            Console.WriteLine("[NexaOne.Server] FDC collection worker registered (Worker:Fdc:Enabled=true).");
        }

        using var host = builder.Build();

        Console.WriteLine("[NexaOne.Server] Ready. Press Ctrl+C to stop.");
        await host.RunAsync();

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
