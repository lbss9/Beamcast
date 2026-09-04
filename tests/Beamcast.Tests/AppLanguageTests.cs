using Xunit;

namespace Beamcast.Tests;

public class AppLanguageTests
{
    [Theory]
    [InlineData("pt-BR", "en-US", "pt-BR")]
    [InlineData("en", "pt-BR", "en")]
    [InlineData("System", "pt-BR", "pt-BR")]
    [InlineData("System", "en-GB", "en")]
    [InlineData(null, "pt-PT", "pt-BR")]
    [InlineData("garbage", "fr-FR", "en")]
    public void ResolvesAgainstStoredValueAndOs(string? stored, string os, string expected)
    {
        Assert.Equal(expected, AppLanguage.Resolve(stored, os));
    }
}
