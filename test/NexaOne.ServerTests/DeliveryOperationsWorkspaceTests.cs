using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaOne.Server.Components.Pages;
using NexaOne.ServiceContracts.Sys;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using Radzen;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class DeliveryOperationsWorkspaceTests : BunitContext
{
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Organization = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private readonly Mock<IApiClient> _api = new(MockBehavior.Strict);

    public DeliveryOperationsWorkspaceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddRadzenComponents();
        var authorization = this.AddAuthorization();
        authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, "operator"));
        authorization.SetAuthorized("operator");
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(new UiTextService());
        Reads<DeliveryTemplate>(_ => new([], 0));
        Reads<DeliveryProfile>(_ => new([], 0));
    }

    [Fact]
    public void Empty_scope_response_is_a_successful_empty_state()
    {
        Reads<BusinessMembership>(_ => new([], 0));
        Reads<DeliveryRequest>(_ => new([], 0));

        var cut = Render<HostDeliveryOperations>();

        cut.WaitForAssertion(() => cut.Find("#delivery-scopes [data-empty]").TextContent
            .Should().Contain("접근 가능한"));
        Paths<BusinessMembership>().Should().Equal(
            "api/v1/collaboration/deliveries/scopes/me?offset=0&limit=100");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dead_letter_actions_require_the_explicit_management_grant(bool canManage)
    {
        var permissions = canManage
            ? new[] { "delivery.read", "delivery.manage-dead-letter" }
            : new[] { "delivery.read" };
        Reads<BusinessMembership>(_ => new([Scope(permissions)], 1));
        Reads<DeliveryRequest>(_ => new([DeadLetter()], 1));

        var cut = Render<HostDeliveryOperations>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-delivery]").Should().ContainSingle());
        cut.FindAll("[data-retry]").Count.Should().Be(canManage ? 1 : 0);
        cut.FindAll("[data-discard]").Count.Should().Be(canManage ? 1 : 0);
        cut.Markup.Should().Contain(canManage ? "재처리" : "조회 전용");
    }

    [Fact]
    public void Unknown_retry_outcome_reuses_the_same_operation_identifier()
    {
        var item = DeadLetter();
        Reads<BusinessMembership>(_ => new([Scope("delivery.read", "delivery.manage-dead-letter")], 1));
        Reads<DeliveryRequest>(_ => new([item], 1));
        _api.SetupSequence(api => api.WriteInventoryAsync<DeliveryRequest>(HttpMethod.Post,
                It.IsAny<string>(), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((null, 503, null, "unavailable"))
            .ReturnsAsync((item with { State = DeliveryState.Pending, Version = Guid.NewGuid(), AttemptCount = 0,
                LastLeaseId = null, LastErrorCode = null }, 200, null, null));

        var cut = Render<HostDeliveryOperations>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-retry]").Should().NotBeNull());
        cut.Find("[data-retry]").Click();
        cut.WaitForAssertion(() => cut.Find("#delivery-action-error").TextContent.Should().Contain("서비스"));
        cut.Find("[data-retry]").Click();
        cut.WaitForAssertion(() => _api.Invocations.Count(call =>
            call.Method.Name == nameof(IApiClient.WriteInventoryAsync)).Should().Be(2));

        var writes = _api.Invocations.Where(call => call.Method.Name == nameof(IApiClient.WriteInventoryAsync))
            .ToArray();
        OperationId(writes[0].Arguments[2]!).Should().Be(OperationId(writes[1].Arguments[2]!));
        ((string)writes[0].Arguments[1]!).Should().EndWith($"/{item.Id:D}/dead-letter/retry");
    }

    [Fact]
    public void Read_only_catalog_never_renders_template_body_or_credential_reference()
    {
        Reads<DeliveryTemplate>(_ => new([Template()], 1));
        Reads<DeliveryProfile>(_ => new([Profile()], 1));
        Reads<DeliveryRequest>(_ => new([], 0));

        var cut = Render<DeliveryCatalogPanel>(parameters => parameters
            .Add(value => value.Scope, Scope("delivery.read"))
            .Add(value => value.UserId, "operator"));

        cut.WaitForAssertion(() => cut.FindAll("[data-template]").Should().ContainSingle());
        cut.FindAll("[data-profile]").Should().ContainSingle();
        cut.Markup.Should().NotContain("TOP-SECRET-BODY");
        cut.Markup.Should().NotContain("vault/top-secret");
        cut.FindAll("#delivery-template-form").Should().BeEmpty();
        cut.FindAll("#delivery-profile-form").Should().BeEmpty();
        cut.FindAll("#delivery-request-form").Should().BeEmpty();
    }

    [Fact]
    public void Unknown_queue_outcome_reuses_the_same_operation_identifier()
    {
        var template = Template(); var profile = Profile();
        Reads<DeliveryTemplate>(_ => new([template], 1));
        Reads<DeliveryProfile>(_ => new([profile], 1));
        Reads<DeliveryRequest>(_ => new([], 0));
        var queued = DeadLetter() with { State = DeliveryState.Pending, AttemptCount = 0, LastErrorCode = null };
        _api.SetupSequence(api => api.WriteInventoryAsync<DeliveryRequest>(HttpMethod.Post,
                It.IsAny<string>(), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((null, 503, null, "unavailable"))
            .ReturnsAsync((queued, 200, null, null));

        var cut = Render<DeliveryCatalogPanel>(parameters => parameters
            .Add(value => value.Scope, Scope("delivery.read", "delivery.queue"))
            .Add(value => value.UserId, "operator"));
        cut.WaitForAssertion(() => cut.FindAll("#delivery-request-form select").Should().HaveCount(2));
        cut.FindAll("#delivery-request-form select")[0].Change(template.Id.ToString("D"));
        cut.FindAll("#delivery-request-form select")[1].Change(profile.Id.ToString("D"));
        cut.Find("#delivery-request-form input").Change("buyer@example.com");
        cut.Find("#delivery-request-form textarea").Change("name=Buyer");
        cut.Find("#delivery-request-queue").Click();
        cut.WaitForAssertion(() => cut.Find("[role=alert]").Should().NotBeNull());
        cut.Find("#delivery-request-queue").Click();
        cut.WaitForAssertion(() => _api.Invocations.Count(call =>
            call.Method.Name == nameof(IApiClient.WriteInventoryAsync)).Should().Be(2));

        var writes = _api.Invocations.Where(call => call.Method.Name == nameof(IApiClient.WriteInventoryAsync))
            .ToArray();
        OperationId(writes[0].Arguments[2]!).Should().Be(OperationId(writes[1].Arguments[2]!));
    }

    private void Reads<T>(Func<string, BusinessPage<T>> response)
        => _api.Setup(api => api.ReadInventoryAsync<BusinessPage<T>>(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) =>
                Task.FromResult<(BusinessPage<T>?, int, string?, string?)>((response(path), 200, null, null)));

    private string[] Paths<T>() => _api.Invocations.Where(call =>
            call.Method.Name == nameof(IApiClient.ReadInventoryAsync)
            && call.Method.GetGenericArguments()[0] == typeof(BusinessPage<T>))
        .Select(call => (string)call.Arguments[0]).ToArray();

    private static Guid OperationId(object body)
        => (Guid)body.GetType().GetProperty("operationId")!.GetValue(body)!;

    private static BusinessMembership Scope(params string[] permissions)
        => new(Tenant, Organization, "operator", Guid.Parse("30000000-0000-0000-0000-000000000001"),
            true, 1, permissions);

    private static DeliveryRequest DeadLetter()
    {
        var scope = new BusinessScope("NexaOne.MES", Tenant.ToString("D"), Organization.ToString("D"));
        var lease = Guid.NewGuid();
        return new(Guid.NewGuid(), scope, Guid.NewGuid(), Guid.NewGuid(),
            new(Guid.NewGuid(), Guid.NewGuid(), "buyer@example.com"), Guid.NewGuid(), Guid.NewGuid(),
            new(DeliveryChannel.Email, "buyer@example.com", "Subject", "Body", "smtp", "mail-primary"),
            new(1, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)), DeliveryState.DeadLetter, 1,
            new DateTimeOffset(2026, 9, 24, 5, 0, 0, TimeSpan.Zero), "operator",
            new DateTimeOffset(2026, 9, 24, 4, 0, 0, TimeSpan.Zero), LastLeaseId: lease,
            LastErrorCode: "DELIVERY_PROVIDER_SEND_FAILED");
    }

    private static DeliveryTemplate Template()
    {
        var scope = new BusinessScope("NexaOne.MES", Tenant.ToString("D"), Organization.ToString("D"));
        return new(Guid.NewGuid(), scope, Guid.NewGuid(), "Welcome", DeliveryChannel.Email,
            "Hello {{name}}", "TOP-SECRET-BODY", ["name"]);
    }

    private static DeliveryProfile Profile()
    {
        var scope = new BusinessScope("NexaOne.MES", Tenant.ToString("D"), Organization.ToString("D"));
        return new(Guid.NewGuid(), scope, Guid.NewGuid(), "Primary", DeliveryChannel.Email,
            "smtp", "vault/top-secret", new(3, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1)));
    }
}
