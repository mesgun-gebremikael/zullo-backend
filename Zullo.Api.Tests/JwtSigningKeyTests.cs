using System.Security.Cryptography;
using Xunit;
using Zullo.Api.Services;

namespace Zullo.Api.Tests;

public class JwtSigningKeyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("too-short-key")]
    public void MissingOrShortConfigurationFailsWithoutLeakingItsValue(string? key)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => JwtSigningKey.Require(key, false));
        Assert.Contains("JWT_SIGNING_KEY_REQUIRED", failure.Message);
        if (!string.IsNullOrWhiteSpace(key)) Assert.DoesNotContain(key, failure.Message);
    }

    [Fact]
    public void RepositoryExampleIsAllowedOnlyInDevelopment()
    {
        const string example = "SUPER_SECRET_DEV_KEY_CHANGE_THIS_123456789";
        Assert.Equal(example, JwtSigningKey.Require(example, true));
        foreach (var value in new[] { example, example.ToLowerInvariant(), " " + example + " " })
        {
            var failure = Assert.Throws<InvalidOperationException>(() => JwtSigningKey.Require(value, false));
            Assert.Contains("JWT_DEVELOPMENT_KEY_FORBIDDEN", failure.Message);
            Assert.DoesNotContain(example, failure.Message);
        }
    }

    [Fact]
    public void ConfiguredRandomKeyIsPreservedExactly()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        Assert.Equal(key, JwtSigningKey.Require(key, false));
        Assert.Equal(key, JwtSigningKey.Require(key, true));
    }

    [Fact]
    public void HmacMinimumUsesEncodedByteCount()
    {
        Assert.Throws<InvalidOperationException>(() => JwtSigningKey.Require(new string('a', 31), false));
        Assert.Equal(new string('a', 32), JwtSigningKey.Require(new string('a', 32), false));
        Assert.Equal(new string('å', 16), JwtSigningKey.Require(new string('å', 16), false));
    }
}
