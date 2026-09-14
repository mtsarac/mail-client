using MailClient.Application.Auth;

namespace MailClient.Infrastructure.Tests.Auth;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("short")]
    [InlineData("")]
    [InlineData("       ")]
    public void ValidatePassword_RejectsWeakPasswords(string password)
    {
        Assert.Throws<ArgumentException>(() => PasswordPolicy.ValidatePassword(password));
    }

    [Fact]
    public void ValidatePassword_AcceptsLongEnoughPassword()
    {
        PasswordPolicy.ValidatePassword("long-enough-pass");
    }

    [Fact]
    public void ValidatePassword_RejectsOverlongPassword()
    {
        Assert.Throws<ArgumentException>(() => PasswordPolicy.ValidatePassword(new string('p', 129)));
    }

    [Fact]
    public void ValidatePassword_AcceptsMaxLengthPassword()
    {
        PasswordPolicy.ValidatePassword(new string('p', 128));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign.example.com")]
    [InlineData("Display Name <trimmed@example.com> ")]
    public void ValidateEmail_RejectsMalformedAddresses(string email)
    {
        Assert.Throws<ArgumentException>(() => PasswordPolicy.ValidateEmail(email));
    }

    [Fact]
    public void ValidateEmail_AcceptsWellFormedAddress()
    {
        PasswordPolicy.ValidateEmail("account@example.com");
    }
}
