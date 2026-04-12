using System.Diagnostics;

namespace Platform.Api.Infrastructure.Middleware;

public class CorrelationMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    public CorrelationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(CorrelationIdHeader, out var correlationId)
            || !Guid.TryParse(correlationId, out _))
        {
            correlationId = Guid.NewGuid().ToString();
        }

        context.Items["CorrelationId"] = correlationId.ToString();
        context.Response.Headers[CorrelationIdHeader] = correlationId.ToString();

        // Tag the current OpenTelemetry activity so Application Insights traces
        // can be searched by correlation ID alongside the audit log.
        Activity.Current?.SetTag("app.correlationId", correlationId.ToString());

        await _next(context);
    }
}
