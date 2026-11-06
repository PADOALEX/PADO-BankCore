using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BankCore.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class AuthenticationIntegrationTests(DatabaseFixture fixture)
{
    [Theory]
    [InlineData(null, "password")]
    [InlineData("username", null)]
    [InlineData("", "password")]
    [InlineData("username", "")]
    public async Task NullOrEmptyCredentialsReturnUnauthorized(string? username, string? password)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoginNormalizesUsernameIndependentlyOfCurrentCulture()
    {
        _ = fixture;
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var username = "identity" + Guid.NewGuid().ToString("N")[..12];
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new { username, password })).StatusCode);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var response = await client.PostAsJsonAsync("/api/auth/login", new { username = " " + username + " ", password });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    private sealed class ApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:BankCore", Environment.GetEnvironmentVariable("BANKCORE_TEST_CONNECTION"));
            builder.UseSetting("Jwt:SigningKey", Convert.ToHexString(RandomNumberGenerator.GetBytes(48)));
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Database:SeedDemoData", "false");
        }
    }
}
