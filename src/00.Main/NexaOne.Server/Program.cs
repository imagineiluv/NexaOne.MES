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
            var configFiles = service.Attribute("configFiles")?.Value
                ?? throw new InvalidOperationException($"Service '{name}' missing 'configFiles' attribute.");
            var classPaths = service.Attribute("classPaths")?.Value
                ?? throw new InvalidOperationException($"Service '{name}' missing 'classPaths' attribute.");

            _server.AddService(name, new[] { configFiles }, new[] { classPaths });
            Console.WriteLine($"[NexaOne.Server] Service '{name}' registered.");
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
