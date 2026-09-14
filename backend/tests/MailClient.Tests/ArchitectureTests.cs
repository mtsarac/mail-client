namespace MailClient.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Domain_HasNoInfrastructureReferences()
    {
        var references = typeof(MailClient.Domain.Entities.MailAccount).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name is "Microsoft.EntityFrameworkCore" or "MailKit" or "Microsoft.AspNetCore");
    }

    [Fact]
    public void Program_ContainsNoRouteDeclarations()
    {
        var source = ReadApiSource("Program.cs");

        Assert.DoesNotContain(".MapGroup(", source);
        Assert.DoesNotContain(".MapGet(", source);
        Assert.DoesNotContain(".MapPost(", source);
        Assert.DoesNotContain(".MapPatch(", source);
        Assert.DoesNotContain(".MapDelete(", source);
        Assert.DoesNotContain(".MapPut(", source);
    }

    [Fact]
    public void Program_RegistersEndpointModules()
    {
        var source = ReadApiSource("Program.cs");

        Assert.Contains("MapAccountEndpoints()", source);
        Assert.Contains("MapAuthEndpoints()", source);
        Assert.Contains("MapFolderEndpoints()", source);
        Assert.Contains("MapMailEndpoints()", source);
        Assert.Contains("MapDeviceEndpoints()", source);
        Assert.Contains("MapHealthEndpoints()", source);
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
