using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>전용 Host 관리 화면의 폼 컨트롤이 placeholder만으로 이름을 전달하는 회귀를 막는다.</summary>
public sealed class HostPageAccessibilityContractTests
{
    [Theory]
    [InlineData("HostRoleManagement.razor")]
    [InlineData("HostVirtualEventManagement.razor")]
    [InlineData("HostDashboardLayoutEdit.razor")]
    [InlineData("HostSoCatalog.razor")]
    [InlineData("HostUserRequests.razor")]
    public void Dedicated_management_page_inputs_have_explicit_accessible_names(string fileName)
    {
        var source = File.ReadAllText(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "Components", "Pages", fileName));

        var controls = Regex.Matches(
                source,
                @"<(?:input|select|textarea)\b[^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Select(match => match.Value)
            .ToArray();

        controls.Should().NotBeEmpty();
        controls.Should().OnlyContain(
            control => control.Contains("aria-label=", StringComparison.OrdinalIgnoreCase)
                || control.Contains("aria-labelledby=", StringComparison.OrdinalIgnoreCase),
            $"{fileName}의 모든 폼 컨트롤은 placeholder와 별개인 accessible name을 가져야 한다");
    }

    [Fact]
    public void Role_permission_remove_buttons_name_the_role_and_permission()
    {
        var source = File.ReadAllText(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "Components", "Pages", "HostRoleManagement.razor"));

        source.Should().Contain("aria-label=\"@($\"{roleId} 역할에서 {perm} 권한 회수\")\"");
    }

}
