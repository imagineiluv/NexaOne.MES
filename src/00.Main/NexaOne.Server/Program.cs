using System.Xml.Linq;
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
}
