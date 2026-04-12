using System.Net;
using System.Text.Json;

namespace Platform.Api.Infrastructure.Middleware;

public class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation: {Message}", ex.Message);
            await WriteErrorResponse(context, HttpStatusCode.BadRequest, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unauthorized: {Message}", ex.Message);
            await WriteErrorResponse(context, HttpStatusCode.Forbidden, "Access denied");
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "Not found: {Message}", ex.Message);
            await WriteErrorResponse(context, HttpStatusCode.NotFound, ex.Message);
        }
        catch (Exception ex)
        {
            var correlationId = context.Items["CorrelationId"]?.ToString();
            _logger.LogError(ex, "Unhandled exception. CorrelationId: {CorrelationId}", correlationId);
            await WriteErrorResponse(context, HttpStatusCode.InternalServerError,
                "An internal error occurred. Reference: " + correlationId);
        }
    }

    private static async Task WriteErrorResponse(HttpContext context, HttpStatusCode statusCode, string message)
    {
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";

        var response = new { error = message, correlationId = context.Items["CorrelationId"]?.ToString() };
        await context.Response.WriteAsJsonAsync(response);
    }
}
