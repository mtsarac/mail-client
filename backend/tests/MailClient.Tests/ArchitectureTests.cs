namespace MailClient.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Domain_HasNoInfrastructureReferences()
    {
        var references = typeof(MailClient.Domain.Entities.MailAccount).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name is "Microsoft.EntityFrameworkCore" or "MailKit" or "Microsoft.AspNetCore");
    }
}
