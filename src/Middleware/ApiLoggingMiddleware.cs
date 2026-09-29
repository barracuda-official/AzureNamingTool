using System.Diagnostics;
using System.Text;

namespace AzureNamingTool.Middleware
{
    /// <summary>
    /// Middleware that logs API requests and responses for auditing and troubleshooting.
    /// </summary>
    public class ApiLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ApiLoggingMiddleware> _logger;

        /// <summary>
        /// Sanitizes user input for safe logging by removing newlines and control characters.
        /// </summary>
        private static string SanitizeForLog(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;
            
            // Remove newlines and carriage returns
            var sanitized = input.Replace("\r", "").Replace("\n", "");
            
            // Remove other control characters
            sanitized = new string(sanitized.Where(c => !char.IsControl(c) || c == ' ').ToArray());
            
            return sanitized;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ApiLoggingMiddleware"/> class.
        /// </summary>
        /// <param name="next">The next middleware in the pipeline.</param>
        /// <param name="logger">The logger instance.</param>
        public ApiLoggingMiddleware(RequestDelegate next, ILogger<ApiLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        /// <summary>
        /// Processes the HTTP request and logs request/response information for API endpoints.
        /// </summary>
        /// <param name="context">The HTTP context.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task InvokeAsync(HttpContext context)
        {
            // Only log API requests (not static files, health checks, etc.)
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                await _next(context);
                return;
            }

            var correlationId = context.Items["CorrelationId"]?.ToString() ?? context.TraceIdentifier;
            var stopwatch = Stopwatch.StartNew();

            LogRequest(context, correlationId);
            try
            {
                await _next(context);
                stopwatch.Stop();
                LogResponse(context, correlationId, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception)
            {
                stopwatch.Stop();
                _logger.LogError(
                    "API request failed. Method: {Method}, Path: {Path}, CorrelationId: {CorrelationId}, Duration: {Duration}ms",
                    SanitizeForLog(context.Request.Method),
                    SanitizeForLog(context.Request.Path.ToString()),
                    correlationId,
                    stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        private void LogRequest(HttpContext context, string correlationId)
        {
            var request = context.Request;

            // Build request details
            var requestDetails = new StringBuilder();
            requestDetails.AppendLine($"API Request:");
            requestDetails.AppendLine($"  Method: {SanitizeForLog(request.Method)}");
            requestDetails.AppendLine($"  Path: {SanitizeForLog(request.Path.ToString())}");
            requestDetails.AppendLine($"  CorrelationId: {SanitizeForLog(correlationId)}");
            
            if (request.Headers.ContainsKey("APIKey"))
            {
                requestDetails.AppendLine("  APIKey: present");
            }

            _logger.LogInformation(requestDetails.ToString());
        }

        private void LogResponse(HttpContext context, string correlationId, long durationMs)
        {
            var response = context.Response;

            // Build response details
            var logLevel = response.StatusCode >= 500 ? LogLevel.Error :
                          response.StatusCode >= 400 ? LogLevel.Warning :
                          LogLevel.Information;

            var responseDetails = new StringBuilder();
            responseDetails.AppendLine($"API Response:");
            responseDetails.AppendLine($"  StatusCode: {response.StatusCode}");
            responseDetails.AppendLine($"  CorrelationId: {correlationId}");
            responseDetails.AppendLine($"  Duration: {durationMs}ms");

            _logger.Log(logLevel, responseDetails.ToString());

            // Log structured data for monitoring/alerting
            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Method"] = SanitizeForLog(context.Request.Method),
                ["Path"] = SanitizeForLog(context.Request.Path.ToString()),
                ["StatusCode"] = response.StatusCode,
                ["DurationMs"] = durationMs
            }))
            {
                if (logLevel == LogLevel.Error)
                {
                    _logger.LogError(
                        "API request completed with error. Method: {Method}, Path: {Path}, StatusCode: {StatusCode}, Duration: {Duration}ms",
                        SanitizeForLog(context.Request.Method),
                        SanitizeForLog(context.Request.Path.ToString()),
                        response.StatusCode,
                        durationMs);
                }
                else if (logLevel == LogLevel.Warning)
                {
                    _logger.LogWarning(
                        "API request completed with client error. Method: {Method}, Path: {Path}, StatusCode: {StatusCode}, Duration: {Duration}ms",
                        SanitizeForLog(context.Request.Method),
                        SanitizeForLog(context.Request.Path.ToString()),
                        response.StatusCode,
                        durationMs);
                }
                else if (durationMs > 5000)
                {
                    _logger.LogWarning(
                        "Slow API request detected. Method: {Method}, Path: {Path}, StatusCode: {StatusCode}, Duration: {Duration}ms",
                        SanitizeForLog(context.Request.Method),
                        SanitizeForLog(context.Request.Path.ToString()),
                        response.StatusCode,
                        durationMs);
                }
            }
        }
    }

    /// <summary>
    /// Extension methods for registering the API logging middleware.
    /// </summary>
    public static class ApiLoggingMiddlewareExtensions
    {
        /// <summary>
        /// Adds API logging middleware to the application pipeline.
        /// </summary>
        public static IApplicationBuilder UseApiLogging(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<ApiLoggingMiddleware>();
        }
    }
}
