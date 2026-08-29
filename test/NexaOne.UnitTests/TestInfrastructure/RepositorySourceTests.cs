namespace NexaOne.UnitTests.TestInfrastructure;

public sealed class RepositorySourceTests
{
    [Fact]
    public void Build_recorded_root_resolves_when_test_output_is_external()
    {
        var externalOutput = Path.Combine(
            Path.GetTempPath(),
            "NexaOne.Tests",
            "external-artifacts",
            "bin");

        var repositoryRoot = RepositorySource.ResolveRepositoryRoot(
            RepositorySource.Root,
            externalOutput);

        RepositorySource.IsRepositoryRoot(repositoryRoot).Should().BeTrue();
    }

    [Fact]
    public void Invalid_build_recorded_root_fails_closed_instead_of_using_a_different_checkout()
    {
        var invalidConfiguredRoot = Path.Combine(
            Path.GetTempPath(),
            "NexaOne.Tests",
            "invalid-checkout");

        var act = () => RepositorySource.ResolveRepositoryRoot(
            invalidConfiguredRoot,
            RepositorySource.Root);

        act.Should().Throw<DirectoryNotFoundException>()
            .WithMessage("*Build-recorded*NexaOne repository root is invalid*");
    }

    [Fact]
    public void Validated_process_location_remains_a_compatibility_fallback_without_metadata()
    {
        var nestedProcessLocation = Path.Combine(
            RepositorySource.Root,
            "test",
            "NexaOne.UnitTests");

        var repositoryRoot = RepositorySource.ResolveRepositoryRoot(
            configuredRoot: null,
            nestedProcessLocation);

        repositoryRoot.Should().Be(RepositorySource.Root);
    }

    [Fact]
    public void Repository_paths_cannot_escape_the_build_recorded_checkout()
    {
        var act = () => RepositorySource.GetFile("..", "outside.txt");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*escapes the checkout root*");
    }
}
