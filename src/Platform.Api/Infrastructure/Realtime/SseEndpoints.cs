using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Infrastructure.Realtime;

public static class SseEndpoints
{
    public static void MapSseEndpoints(this WebApplication app)
    {
        app.MapGet("/api/events/stream", async (HttpContext context, SseConnectionManager manager, ICurrentUser currentUser) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var userId = currentUser.Id;
            var writer = new StreamWriter(context.Response.Body) { AutoFlush = false };
            var connection = new SseConnection { Writer = writer };
            manager.AddConnection(userId, connection);

            // Send initial heartbeat
            await writer.WriteAsync(": connected\n\n");
            await writer.FlushAsync();

            try
            {
                // Keep connection alive with periodic heartbeats
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    await Task.Delay(30_000, context.RequestAborted);
                    await writer.WriteAsync(": heartbeat\n\n");
                    await writer.FlushAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected
            }
            finally
            {
                manager.RemoveConnection(userId, connection);
            }
        }).AllowAnonymous();
    }
}
