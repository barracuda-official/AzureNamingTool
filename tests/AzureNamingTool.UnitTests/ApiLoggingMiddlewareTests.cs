using System.Text;
using AzureNamingTool.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzureNamingTool.UnitTests;

public class ApiLoggingMiddlewareTests
{
    [Fact]
    public async Task ApiAuditLogsMetadataWithoutCredentialOrBodyMaterial()
    {
        const string key = "synthetic-secret-prefix-and-suffix";
        const string requestMarker = "synthetic-request-private-marker";
        const string responseMarker = "synthetic-response-private-marker";
        var logger = new CapturingLogger();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/test";
        context.Request.Headers["APIKey"] = key;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestMarker));
        context.Request.ContentLength = context.Request.Body.Length;
        context.Response.Body = new MemoryStream();

        var middleware = new ApiLoggingMiddleware(async http =>
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync(responseMarker);
        }, logger);

        await middleware.InvokeAsync(context);

        var logged = string.Join("\n", logger.Messages);
        Assert.Contains("APIKey: present", logged);
        Assert.False(logged.Contains(key, StringComparison.Ordinal), "A complete API key appeared in the audit log.");
        Assert.False(logged.Contains(key[..8], StringComparison.Ordinal), "An API key prefix appeared in the audit log.");
        Assert.False(logged.Contains(key[^8..], StringComparison.Ordinal), "An API key suffix appeared in the audit log.");
        Assert.False(logged.Contains(requestMarker, StringComparison.Ordinal), "A request body appeared in the audit log.");
        Assert.False(logged.Contains(responseMarker, StringComparison.Ordinal), "A response body appeared in the audit log.");
    }

    private sealed class CapturingLogger : ILogger<ApiLoggingMiddleware>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
