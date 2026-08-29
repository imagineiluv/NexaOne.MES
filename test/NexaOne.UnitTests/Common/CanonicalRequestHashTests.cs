using FluentAssertions;
using NexaOne.Application.Idempotency;
using System.Globalization;

namespace NexaOne.UnitTests.Common;

public sealed class CanonicalRequestHashTests
{
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
