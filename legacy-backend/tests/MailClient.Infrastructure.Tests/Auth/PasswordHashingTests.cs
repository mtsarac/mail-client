using MailClient.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace MailClient.Infrastructure.Tests.Auth;

public class PasswordHashingTests
{
    [Fact]
    public void PasswordHasher_VerifiesValidPassword()
    {
        var user = new User();
        var hasher = new PasswordHasher<User>();
        var hash = hasher.HashPassword(user, "correct-horse-battery-staple");

        var result = hasher.VerifyHashedPassword(user, hash, "correct-horse-battery-staple");

        Assert.NotEqual(PasswordVerificationResult.Failed, result);
    }
}
