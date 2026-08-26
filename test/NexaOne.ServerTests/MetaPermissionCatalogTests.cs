using FluentAssertions;
using NexaOne.Application.Query;
using NexaOne.Server.Gateway;
using NexaOne.Web.Services.Meta;
using Moq;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class MetaPermissionCatalogTests
{
    [Fact]
    public void ResolveRead_distinguishes_restricted_public_and_wrong_kind_bindings()
    {
        var registry = new StubQueryRegistry(
        [
            new QueryDefinition("Q.Restricted", "SELECT 1", "test", "qms:read"),
            new QueryDefinition("Q.Public", "SELECT 1", "test", IsPublic: true),
            new QueryDefinition("Q.Write", "UPDATE T SET X=1", "test", "qms:manage", IsWrite: true),
        ]);
        var catalog = new MetaPermissionCatalog(registry, Mock.Of<IMetaCommandDriverCatalog>());

        catalog.ResolveRead("Q.Restricted").Should().Be(MetaBindingPermission.Known("qms:read"));
        catalog.ResolveRead("Q.Public").Should().Be(MetaBindingPermission.Known(null));
        catalog.ResolveRead("Q.Write").Should().Be(MetaBindingPermission.Unknown);
        catalog.ResolveRead("Q.Missing").Should().Be(MetaBindingPermission.Unknown);
    }

    [Fact]
    public void ResolveWrite_supports_named_queries_and_typed_bridge_descriptors()
    {
        var registry = new StubQueryRegistry(
        [
            new QueryDefinition("Q.Read", "SELECT 1", "test", "pom:read"),
            new QueryDefinition("Q.Update", "UPDATE T SET X=1", "test", "pom:manage", IsWrite: true),
            new QueryDefinition("Q.PublicWrite", "UPDATE T SET X=1", "test", IsWrite: true, IsPublic: true),
        ]);
        var commands = new Mock<IMetaCommandDriverCatalog>();
        MetaCommandDescriptor? descriptor = new("bridge:pom.start", "pom:execute");
        commands.Setup(item => item.TryGetDescriptor("bridge:pom.start", out descriptor)).Returns(true);
        MetaCommandDescriptor? missingPermission = new("bridge:pom.unsafe");
        commands.Setup(item => item.TryGetDescriptor("bridge:pom.unsafe", out missingPermission)).Returns(true);
        var catalog = new MetaPermissionCatalog(registry, commands.Object);

        catalog.ResolveWrite("Q.Update").Should().Be(MetaBindingPermission.Known("pom:manage"));
        catalog.ResolveWrite("bridge:pom.start").Should().Be(MetaBindingPermission.Known("pom:execute"));
        catalog.ResolveWrite("Q.Read").Should().Be(MetaBindingPermission.Unknown);
        catalog.ResolveWrite("Q.PublicWrite").Should().Be(MetaBindingPermission.Unknown);
        catalog.ResolveWrite("bridge:pom.unsafe").Should().Be(MetaBindingPermission.Unknown);
        catalog.ResolveWrite("bridge:pom.missing").Should().Be(MetaBindingPermission.Unknown);
    }

    private sealed class StubQueryRegistry : IQueryRegistry
    {
        private readonly IReadOnlyDictionary<string, QueryDefinition> _items;

        public StubQueryRegistry(IEnumerable<QueryDefinition> items)
            => _items = items.ToDictionary(item => item.Id, StringComparer.Ordinal);

        public IReadOnlyCollection<string> Ids => _items.Keys.ToArray();
        public string Dialect => "test";

        public bool TryGet(string queryId, out QueryDefinition? definition)
            => _items.TryGetValue(queryId, out definition);
    }
}
