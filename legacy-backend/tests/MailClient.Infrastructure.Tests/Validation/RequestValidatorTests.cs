using MailClient.Application.Validation;
using MailClient.Domain.Enums;

namespace MailClient.Infrastructure.Tests.Validation;

public class RequestValidatorTests
{
    [Fact]
    public void RequireDefinedEnum_RejectsUndefinedMailSecurity()
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequireDefinedEnum((MailSecurity)999, "imapSecurity", errors);

        Assert.Equal(["Value is invalid."], errors["imapSecurity"]);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("user@")]
    [InlineData("@example.com")]
    [InlineData("user name@example.com")]
    public void RequireEmail_RejectsInvalidValues(string? value)
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequireEmail(value, "email", errors);

        Assert.Contains("email", errors.Keys);
    }

    [Fact]
    public void RequireEmail_AcceptsValidAddress()
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequireEmail("  User@Example.com ", "email", errors);

        Assert.Empty(errors);
    }

    [Fact]
    public void RequireEmail_RejectsOverlongAddress()
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequireEmail(new string('a', 310) + "@example.com", "email", errors);

        Assert.Contains("email", errors.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RequireUsername_RejectsMissingValues(string? value)
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequireUsername(value, "username", errors);

        Assert.Contains("username", errors.Keys);
    }

    [Fact]
    public void RequireUsername_RejectsOverlongValue()
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequireUsername(new string('u', 321), "username", errors);

        Assert.Contains("username", errors.Keys);
    }

    [Fact]
    public void RequireMailboxPassword_RejectsOverlongValue()
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequireMailboxPassword(new string('p', 1025), "password", errors);

        Assert.Contains("password", errors.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RequireIdempotencyKey_RejectsMissingValues(string? value)
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequireIdempotencyKey(value, "idempotencyKey", errors);

        Assert.Contains("idempotencyKey", errors.Keys);
    }

    [Fact]
    public void RequireIdempotencyKey_RejectsOverlongValue()
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequireIdempotencyKey(new string('k', 201), "idempotencyKey", errors);

        Assert.Contains("idempotencyKey", errors.Keys);
    }

    [Fact]
    public void RequireIdempotencyKey_AcceptsValidValue()
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequireIdempotencyKey("  key-1 ", "idempotencyKey", errors);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RequirePassword_RejectsMissingValues(string? value)
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequirePassword(value, "password", errors);

        Assert.Contains("password", errors.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space.com")]
    [InlineData("-leading.example.com")]
    [InlineData("trailing-.example.com")]
    [InlineData("double..dot.com")]
    public void RequireHost_RejectsInvalidValues(string? value)
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequireHost(value, "host", errors);

        Assert.Contains("host", errors.Keys);
    }

    [Theory]
    [InlineData("mail.example.com")]
    [InlineData("imap.hosting-provider.net")]
    [InlineData("10.0.0.5")]
    public void RequireHost_AcceptsValidValues(string value)
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequireHost(value, "host", errors);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void RequirePort_RejectsOutOfRangeValues(int value)
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequirePort(value, "port", errors);

        Assert.Contains("port", errors.Keys);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(993)]
    [InlineData(65535)]
    public void RequirePort_AcceptsValidValues(int value)
    {
        var errors = new Dictionary<string, string[]>();

        RequestValidator.RequirePort(value, "port", errors);

        Assert.Empty(errors);
    }
}
