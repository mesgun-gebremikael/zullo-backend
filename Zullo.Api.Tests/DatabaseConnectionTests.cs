using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
using Zullo.Api.Data;

namespace Zullo.Api.Tests;

public class DatabaseConnectionTests
{
    [Fact]
    public void ExistingLocalConnectionStringsArePreserved()
    {
        const string local = "Host=localhost;Database=synthetic;Username=test;Password=test-only;SSL Mode=Disable";
        Assert.Equal(local, DatabaseConnection.Normalize(local));
    }

    [Fact]
    public void RenderUrlDecodesCredentialsWithoutTurningThemIntoConnectionOptions()
    {
        var parsed = new NpgsqlConnectionStringBuilder(DatabaseConnection.Normalize(
            "postgresql://test%20user:p%40ss%3BDatabase%3Dother%3A%2B%2F@private-host:5433/zullo%20staging"));
        Assert.Equal("test user", parsed.Username);
        Assert.Equal("p@ss;Database=other:+/", parsed.Password);
        Assert.Equal("zullo staging", parsed.Database);
        Assert.Equal("private-host", parsed.Host);
        Assert.Equal(5433, parsed.Port);
        Assert.Equal(SslMode.Require, parsed.SslMode);
    }

    [Theory]
    [InlineData("postgres://user:password@host/db?sslmode=disable", SslMode.Disable)]
    [InlineData("postgresql://user:password@host/db?sslmode=verify-full", SslMode.VerifyFull)]
    [InlineData("postgresql://user:password@host/db?sslmode=require", SslMode.Require)]
    public void ExplicitSslSettingsAreHonored(string url, SslMode expected)
    {
        var parsed = new NpgsqlConnectionStringBuilder(DatabaseConnection.Normalize(url));
        Assert.Equal(expected, parsed.SslMode);
        Assert.Equal(5432, parsed.Port);
    }

    [Fact]
    public void Ipv6HostIsAccepted()
    {
        var parsed = new NpgsqlConnectionStringBuilder(DatabaseConnection.Normalize("postgres://user:password@[::1]:5432/db"));
        Assert.Equal("::1", parsed.Host);
    }

    [Theory]
    [InlineData("postgresql://user:private-test-password@host/")]
    [InlineData("postgresql://user@host/db")]
    [InlineData("postgresql://user:private-test-password@host/db?unsupported=true")]
    [InlineData("postgresql://user:private-test-password@host/db?sslmode=unknown")]
    [InlineData("postgresql://user:private-test-password@host/db?sslmode=require&sslmode=disable")]
    [InlineData("postgresql://user:private-test-password@host/db#fragment")]
    public void MalformedUrlsFailWithoutEchoingCredentials(string url)
    {
        var error = Assert.Throws<InvalidOperationException>(() => DatabaseConnection.Normalize(url));
        Assert.DoesNotContain("private-test-password", error.ToString());
        Assert.DoesNotContain(url, error.ToString());
    }

    [Theory]
    [InlineData("zullo_staging", true)]
    [InlineData("unrelated_library", false)]
    public void ExpectedDatabaseGuardPreventsMigrationsAgainstAnUnrelatedDatabase(string database, bool allowed)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"postgres://user:test-only@host/{database}",
            ["Deployment:ExpectedDatabaseName"] = "zullo_staging"
        }).Build();
        if (allowed) Assert.Contains("Database=zullo_staging", DatabaseConnection.Resolve(configuration));
        else Assert.Throws<InvalidOperationException>(() => DatabaseConnection.Resolve(configuration));
    }

    [Fact]
    public void MissingDatabaseConfigurationFailsClearly()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Assert.Throws<InvalidOperationException>(() => DatabaseConnection.Resolve(configuration));
    }
}
