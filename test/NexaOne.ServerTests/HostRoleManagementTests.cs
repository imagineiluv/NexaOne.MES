using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaOne.Common.Security;
using NexaOne.Server.Components.Pages;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class HostRoleManagementTests : BunitContext
{
    private readonly Mock<IApiClient> _api = new(MockBehavior.Strict);
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Organization = Guid.Parse("20000000-0000-0000-0000-000000000001");

    public HostRoleManagementTests()
    {
        var authorization = this.AddAuthorization();
        authorization.SetClaims(
            new Claim(ClaimTypes.NameIdentifier, "admin"),
            new Claim(Permissions.ClaimType, Permissions.SysManage));
        authorization.SetAuthorized("admin");
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(new UiTextService());
        _api.Setup(api => api.ExecuteQueryAsync("SYS.ListRoles", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _api.Setup(api => api.GetBusinessOperationPermissionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["crm.read", "crm.deal.manage", "stock.read"]);
    }

    [Fact]
    public void Loaded_revision_can_add_crm_preset_and_save_the_same_scope()
    {
        var membership = new BusinessMembershipAdminDto(
            Tenant, Organization, "operator", Guid.NewGuid(), true, 4, ["stock.read"]);
        _api.Setup(api => api.ReadBusinessMembershipAsync(
                Tenant, Organization, "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((membership, 200, null));
        BusinessMembershipAdminChange? saved = null;
        _api.Setup(api => api.SaveBusinessMembershipAsync(
                Tenant, Organization, "operator", It.IsAny<BusinessMembershipAdminChange>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, string, BusinessMembershipAdminChange, CancellationToken>(
                (_, _, _, change, _) => saved = change)
            .ReturnsAsync((membership with
            {
                Version = 5,
                Permissions = ["crm.deal.manage", "crm.read", "stock.read"]
            }, 200, null));

        var cut = Render<HostRoleManagement>();
        cut.WaitForElement("#business-membership-editor");
        cut.Find("#membership-tenant").Change(Tenant.ToString("D"));
        cut.Find("#membership-organization").Change(Organization.ToString("D"));
        cut.Find("#membership-user").Change("operator");
        cut.Find("#membership-reload").Click();
        cut.WaitForAssertion(() => cut.Find("#membership-permissions").Should().NotBeNull());

        cut.FindAll("button").Single(button => button.TextContent.Contains("CRM 전체 선택")).Click();
        cut.Find("#membership-save").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("revision 5"));
        saved.Should().NotBeNull();
        saved!.ExpectedVersion.Should().Be(4);
        saved.Permissions.Should().Equal("crm.deal.manage", "crm.read", "stock.read");
    }

    [Fact]
    public void Changed_scope_key_requires_a_fresh_read_before_save()
    {
        var membership = new BusinessMembershipAdminDto(
            Tenant, Organization, "operator", Guid.NewGuid(), true, 2, ["crm.read"]);
        _api.Setup(api => api.ReadBusinessMembershipAsync(
                Tenant, Organization, "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((membership, 200, null));

        var cut = Render<HostRoleManagement>();
        cut.Find("#membership-tenant").Change(Tenant.ToString("D"));
        cut.Find("#membership-organization").Change(Organization.ToString("D"));
        cut.Find("#membership-user").Change("operator");
        cut.Find("#membership-reload").Click();
        cut.WaitForElement("#membership-save");

        cut.Find("#membership-user").Change("another-user");
        cut.Find("#membership-save").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("다시 조회"));
        _api.Verify(api => api.SaveBusinessMembershipAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<BusinessMembershipAdminChange>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Mismatched_membership_response_is_rejected_before_it_becomes_editable_authority()
    {
        var mismatched = new BusinessMembershipAdminDto(
            Tenant, Guid.NewGuid(), "operator", Guid.NewGuid(), true, 2, ["crm.read"]);
        _api.Setup(api => api.ReadBusinessMembershipAsync(
                Tenant, Organization, "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((mismatched, 200, null));

        var cut = Render<HostRoleManagement>();
        cut.Find("#membership-tenant").Change(Tenant.ToString("D"));
        cut.Find("#membership-organization").Change(Organization.ToString("D"));
        cut.Find("#membership-user").Change("operator");
        cut.Find("#membership-reload").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("일치하지 않습니다"));
        cut.FindAll("#membership-save").Should().BeEmpty();
    }
}
