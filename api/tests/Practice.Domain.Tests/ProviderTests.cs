using Practice.Domain.Providers;

namespace Practice.Domain.Tests;

/// <summary>
/// The Provider is the clinician. One row today; the schema does not assume that
/// (docs/DATA_MODEL.md).
///
/// These run with no database and no framework — Practice.Domain references nothing — so
/// the rules protecting a clinician's professional identity are cheap enough to check on
/// every save.
/// </summary>
public sealed class ProviderTests
{
    private static Provider CreateValid() =>
        Provider.Create(
            identityUserId: "identity-user-1",
            displayName: "Michelle",
            credentials: "M.S., CCC-SLP",
            licenseNumber: "SLP-12345",
            licenseState: "MD");

    [Fact]
    public void Create_assigns_a_public_id()
    {
        var provider = CreateValid();

        Assert.NotEqual(Guid.Empty, provider.PublicId);
    }

    [Fact]
    public void Create_starts_active()
    {
        Assert.True(CreateValid().IsActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_requires_an_identity_user(string? identityUserId)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Provider.Create(identityUserId!, "Michelle", "M.S., CCC-SLP", "SLP-12345", "MD"));

        Assert.Contains("identity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_requires_a_display_name(string displayName)
    {
        Assert.Throws<ArgumentException>(() =>
            Provider.Create("identity-user-1", displayName, "M.S., CCC-SLP", "SLP-12345", "MD"));
    }

    [Fact]
    public void Create_trims_whitespace()
    {
        var provider = Provider.Create(
            " identity-user-1 ", "  Michelle  ", " M.S., CCC-SLP ", " SLP-12345 ", " md ");

        Assert.Equal("identity-user-1", provider.IdentityUserId);
        Assert.Equal("Michelle", provider.DisplayName);
        Assert.Equal("M.S., CCC-SLP", provider.Credentials);
        Assert.Equal("SLP-12345", provider.LicenseNumber);
    }

    /// <summary>
    /// Stored uppercase so "md" and "MD" cannot become two different states in a
    /// jurisdiction-sensitive field.
    /// </summary>
    [Fact]
    public void Create_normalises_the_licence_state_to_uppercase()
    {
        Assert.Equal("MD", Provider.Create(
            "identity-user-1", "Michelle", "M.S., CCC-SLP", "SLP-12345", "md").LicenseState);
    }

    [Theory]
    [InlineData("M")]
    [InlineData("MDX")]
    [InlineData("")]
    public void Create_rejects_a_licence_state_that_is_not_two_letters(string state)
    {
        Assert.Throws<ArgumentException>(() =>
            Provider.Create("identity-user-1", "Michelle", "M.S., CCC-SLP", "SLP-12345", state));
    }

    /// <summary>
    /// An NPI is exactly 10 digits. It appears on superbills (presearch §16), where a
    /// malformed one is rejected by whoever the parent submits it to — after the fact,
    /// and confusingly.
    /// </summary>
    [Theory]
    [InlineData("123456789")]
    [InlineData("12345678901")]
    [InlineData("12345678a0")]
    public void SetNpi_rejects_anything_that_is_not_ten_digits(string npi)
    {
        var provider = CreateValid();

        Assert.Throws<ArgumentException>(() => provider.SetNpi(npi));
    }

    [Fact]
    public void SetNpi_accepts_ten_digits()
    {
        var provider = CreateValid();

        provider.SetNpi("1234567893");

        Assert.Equal("1234567893", provider.Npi);
    }

    /// <summary>
    /// The NPI is optional — superbill generation is sequenced later, and the practice
    /// operates without one until then.
    /// </summary>
    [Fact]
    public void SetNpi_accepts_null_to_clear()
    {
        var provider = CreateValid();
        provider.SetNpi("1234567893");

        provider.SetNpi(null);

        Assert.Null(provider.Npi);
    }

    [Fact]
    public void Deactivate_marks_the_provider_inactive()
    {
        var provider = CreateValid();

        provider.Deactivate();

        Assert.False(provider.IsActive);
    }

    [Fact]
    public void Deactivate_is_idempotent()
    {
        var provider = CreateValid();

        provider.Deactivate();
        provider.Deactivate();

        Assert.False(provider.IsActive);
    }
}
