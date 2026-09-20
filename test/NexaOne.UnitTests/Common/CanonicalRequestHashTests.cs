using FluentAssertions;
using NexaOne.Application.Idempotency;
using System.Globalization;

namespace NexaOne.UnitTests.Common;

public sealed class CanonicalRequestHashTests
{
    [Fact]
    public void Existing_public_api_preserves_literal_hashes_and_truncated_ids()
    {
        // Captured from MES 81946eda before extracting the implementation to Framework.
        CanonicalRequestHash.Compute(double.Epsilon).Should().Be(
            "629B9E00540A3EAF0D5F9F54F502E30777A03284360B296595EB3307F6720030");
        CanonicalRequestHash.Compute(-0.0f).Should().Be(
            "5CE3254C6F28736F5978296817B2CDBB4CD67C99680F9A31078B81C9834E3E6F");
        CanonicalRequestHash.Compute(DayOfWeek.Monday).Should().Be(
            "319AEF89E5E7F832E2C4AAB284838CE7BA50FD0DFEADAC3D13BE37C6D7584449");
        CanonicalRequestHash.CreateId(" ID_ ", 32).Should().Be(
            " ID_ F2091875B884564A9C5D27C4E57BBF02");
    }

    [Fact]
    public void Existing_public_api_preserves_argument_errors_and_validation_order()
    {
        Assert.Throws<ArgumentNullException>(() => CanonicalRequestHash.Compute(null!))
            .ParamName.Should().Be("values");
        Assert.Throws<ArgumentException>(() => CanonicalRequestHash.CreateId(" ", 0, new object()))
            .ParamName.Should().Be("prefix");
        Assert.Throws<ArgumentOutOfRangeException>(() => CanonicalRequestHash.CreateId("ID_", 0, new object()))
            .ParamName.Should().Be("hashCharacters");
        Assert.Throws<ArgumentException>(() => CanonicalRequestHash.Compute(new object()))
            .ParamName.Should().Be("value");
    }

    [Fact]
    public void Compute_distinguishes_values_that_collide_with_delimiter_joining()
    {
        var left = CanonicalRequestHash.Compute("a\u001fb", "c");
        var right = CanonicalRequestHash.Compute("a", "b\u001fc");

        left.Should().NotBe(right);
    }

    [Fact]
    public void Compute_distinguishes_null_empty_and_value_types()
    {
        CanonicalRequestHash.Compute((object?)null).Should().NotBe(CanonicalRequestHash.Compute(string.Empty));
        CanonicalRequestHash.Compute(1).Should().NotBe(CanonicalRequestHash.Compute("1"));
        CanonicalRequestHash.Compute(1m).Should().NotBe(CanonicalRequestHash.Compute(1d));
    }

    [Fact]
    public void Compute_is_culture_invariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ko-KR");
            var first = CanonicalRequestHash.Compute(1234.56m, new DateTime(2026, 8, 26, 1, 2, 3, DateTimeKind.Utc));

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var second = CanonicalRequestHash.Compute(1234.56m, new DateTime(2026, 8, 26, 1, 2, 3, DateTimeKind.Utc));

            second.Should().Be(first);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
