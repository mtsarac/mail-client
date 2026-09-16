namespace MailClient.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Domain_HasNoInfrastructureReferences()
    {
        var references = typeof(MailClient.Domain.Entities.MailAccount).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name is "Microsoft.EntityFrameworkCore" or "MailKit" or "Microsoft.AspNetCore");
    }

    private static string ReadApiSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "MailClient.Api", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate MailClient.Api/{fileName} from test output.");
    }
}
