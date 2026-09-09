using System.Xml.Linq;
using NexaFramework.Context;
using NexaOne.Common.Caching;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class MemoryCacheHostLifetimeTests
{
    [Theory]
    [InlineData("server.xml")]
    [InlineData("server.sqlite.xml")]
    public async Task Actual_cache_bean_is_disposed_by_the_host_context(string configuration)
    {
        XNamespace spring = "http://www.springframework.net";
        var source = XDocument.Load(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "host", configuration));
        var bean = source.Root!.Elements(spring + "object")
            .Single(element => (string?)element.Attribute("id") == "cacheService");
        var path = Path.GetTempFileName();
        try
        {
            // Exercise the real host bean and context disposal, without opening its unrelated
            // database/PLC/message-bus beans. Full module boot has a separate child-host test.
            new XDocument(new XElement(source.Root.Name, source.Root.Attributes(), new XElement(bean))).Save(path);
            using var context = new FileSystemApplicationContext(false, path);
            context.Refresh();
            var cache = Assert.IsType<MemoryCacheService>(context.GetObject("cacheService"));
            Assert.Equal(1, await cache.GetOrCreateAsync("ready", () => Task.FromResult(1)));
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = cache.GetOrCreateAsync("pending", () => completion.Task);
            context.Dispose();
            completion.SetResult(2);
            Assert.Equal(2, await pending);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.GetOrCreateAsync("ready", () => Task.FromResult(3)));
        }
        finally { File.Delete(path); }
    }
}
