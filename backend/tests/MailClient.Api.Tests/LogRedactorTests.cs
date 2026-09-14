using System.Text.Json.Nodes;
using MailClient.Api.Logging;

namespace MailClient.Api.Tests;

public sealed class LogRedactorTests
{
    [Fact]
    public void Redact_TopLevelPassword_BecomesRedacted()
    {
        var node = JsonNode.Parse("""{"email":"a@example.com","password":"secret-1"}""");
        var redacted = (JsonObject)LogRedactor.Redact(node)!;
        Assert.Equal("a@example.com", redacted["email"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", redacted["password"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_NestedKeys_CaseInsensitive()
    {
        var node = JsonNode.Parse("""{"user":{"Password":"x","nested":{"REFRESHTOKEN":"y","keep":"z"}}}""");
        var redacted = (JsonObject)LogRedactor.Redact(node)!;
        var user = (JsonObject)redacted["user"]!;
        Assert.Equal("[REDACTED]", user["Password"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", ((JsonObject)user["nested"]!)["REFRESHTOKEN"]!.GetValue<string>());
        Assert.Equal("z", ((JsonObject)user["nested"]!)["keep"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_ArrayItems_AreRedacted()
    {
        var node = JsonNode.Parse("""{"items":[{"token":"a"},{"token":"b","n":1}]}""");
        var redacted = (JsonObject)LogRedactor.Redact(node)!;
        var items = (JsonArray)redacted["items"]!;
        Assert.Equal("[REDACTED]", ((JsonObject)items[0]!)["token"]!.GetValue<string>());
        Assert.Equal(1, ((JsonObject)items[1]!)["n"]!.GetValue<int>());
    }

    [Fact]
    public void Redact_MailboxAndAuthKeys()
    {
        var node = JsonNode.Parse("""{"password":"p","encryptedPassword":"e","authorization":"Bearer x","apiKey":"k","emailAddress":"a@b.c"}""");
        var redacted = (JsonObject)LogRedactor.Redact(node)!;
        Assert.Equal("[REDACTED]", redacted["password"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", redacted["encryptedPassword"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", redacted["authorization"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", redacted["apiKey"]!.GetValue<string>());
        Assert.Equal("a@b.c", redacted["emailAddress"]!.GetValue<string>());
    }

    [Fact]
    public void ParseAndRedact_InvalidJson_ReturnsRawString()
    {
        Assert.Equal("not-json{{{", LogRedactor.ParseAndRedact("not-json{{{")!.GetValue<string>());
    }

    [Fact]
    public void ParseAndRedact_ValidJson_ReturnsStructuredNode()
    {
        var result = LogRedactor.ParseAndRedact("""{"email":"a@b.c","password":"x"}""");
        Assert.IsType<JsonObject>(result);
        Assert.Equal("a@b.c", ((JsonObject)result!)["email"]!.GetValue<string>());
    }
}
