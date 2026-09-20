using KJWebsite.BuildingBlocks;

namespace KJWebsite.BuildingBlocks.UnitTests;

public sealed class LanguageResolverTests
{
    [Fact]
    public void ResolvePrefersAValidQueryLanguageOverTheHeader()
    {
        var language = LanguageResolver.Resolve("BN", "en-US,en;q=0.9");

        Assert.Equal(LanguageResolver.Bengali, language);
    }

    [Theory]
    [InlineData(null, "bn-BD,bn;q=0.9,en;q=0.8", "bn")]
    [InlineData("fr", "bn-BD,en;q=0.8", "bn")]
    [InlineData("fr", "fr-FR", "en")]
    public void ResolveUsesTheHeaderThenEnglishAsFallback(string? queryLanguage, string acceptLanguage, string expected)
    {
        var language = LanguageResolver.Resolve(queryLanguage, acceptLanguage);

        Assert.Equal(expected, language);
    }
}
