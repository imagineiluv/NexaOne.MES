using NexaOne.Common.Security;

namespace NexaOne.UnitTests.Common;

public sealed class RolePermissionDefaultsTests
{
    [Fact]
    public void Viewer_keeps_the_existing_least_privilege_fdc_read_default()
    {
        var permissions = RolePermissionDefaults.For("VIEWER");

        permissions.Should().Equal(Permissions.FdcRead);
        permissions.Should().NotContain(permission => permission.EndsWith(":manage", StringComparison.Ordinal));
    }

    [Fact]
    public void Operator_has_only_the_reads_needed_for_shop_floor_execution()
    {
        var permissions = RolePermissionDefaults.For("OPERATOR");

        permissions.Should().Contain(new[]
        {
            Permissions.FdcRead, Permissions.MdmRead, Permissions.EstRead,
            Permissions.PomRead, Permissions.RmsRead,
        });
        permissions.Should().Contain(Permissions.FdcControl);
        permissions.Should().Contain(Permissions.PomExecute);
        permissions.Should().Contain(Permissions.PomRoutingRequest);
        permissions.Should().NotContain(Permissions.PomRoutingApprove,
            "현장 작업자는 자신의 Flexible 라우팅 예외 요청을 승인할 수 없어야 한다");
        permissions.Should().NotContain(new[]
        {
            Permissions.ComRead, Permissions.EmsRead, Permissions.IvtRead,
            Permissions.PrcRead, Permissions.QmsRead,
            Permissions.ShpRead, Permissions.SlsRead, Permissions.SysRead,
        });
    }

    [Fact]
    public void Maintenance_has_no_runtime_legacy_fallback()
    {
        var permissions = RolePermissionDefaults.For("MAINTENANCE");

        permissions.Should().BeEmpty(
            "V118이 MAINTENANCE 권한을 SYS_ROLE에 시드하므로 신규 역할에 코드 fallback이 있으면 DB revoke를 무효화한다");
    }
}
