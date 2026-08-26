namespace CalendarMcp.HttpServer;

internal sealed record McpOAuthOptions(
    string Issuer,
    string MetadataAddress,
    string Resource,
    string ToolsScope)
{
    internal static McpOAuthOptions FromConfiguration(IConfiguration configuration)
    {
        var issuer = Value(configuration, "OAuth:Issuer", "http://localhost:9080").TrimEnd('/');
        return new McpOAuthOptions(
            issuer,
            Value(configuration, "OAuth:MetadataAddress", $"{issuer}/.well-known/oauth-authorization-server"),
            Value(configuration, "OAuth:Resource", "http://localhost:8080/"),
            Value(configuration, "OAuth:ToolsScope", "mcp:tools"));
    }

    private static string Value(IConfiguration configuration, string key, string fallback) =>
        string.IsNullOrWhiteSpace(configuration[key]) ? fallback : configuration[key]!.Trim();
}
