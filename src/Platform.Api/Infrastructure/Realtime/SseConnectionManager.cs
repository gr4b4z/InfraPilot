using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Api.Infrastructure.Realtime;

/// <summary>
/// Manages active SSE connections and broadcasts events to connected clients.
/// Registered as a singleton.
/// </summary>
public class SseConnectionManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // userId -> list of response streams
    private readonly ConcurrentDictionary<string, ConcurrentBag<SseConnection>> _connections = new();

    public void AddConnection(string userId, SseConnection connection)
    {
        var bag = _connections.GetOrAdd(userId, _ => new ConcurrentBag<SseConnection>());
        bag.Add(connection);
    }

    public void RemoveConnection(string userId, SseConnection connection)
    {
        if (_connections.TryGetValue(userId, out var bag))
        {
            // ConcurrentBag doesn't support removal, rebuild without the connection
            var remaining = new ConcurrentBag<SseConnection>(bag.Where(c => c.Id != connection.Id));
            _connections.TryUpdate(userId, remaining, bag);
        }
    }

    public async Task SendEvent(string userId, PlatformEvent evt)
    {
        if (_connections.TryGetValue(userId, out var bag))
        {
            var data = JsonSerializer.Serialize(evt, JsonOptions);
            var message = $"event: {evt.Type}\ndata: {data}\n\n";

            foreach (var conn in bag)
            {
                try
                {
                    await conn.Writer.WriteAsync(message);
                    await conn.Writer.FlushAsync();
                }
                catch
                {
                    // Connection dead, will be cleaned up
                }
            }
        }
    }

    public async Task BroadcastEvent(PlatformEvent evt)
    {
        var data = JsonSerializer.Serialize(evt, JsonOptions);
        var message = $"event: {evt.Type}\ndata: {data}\n\n";

        foreach (var (_, bag) in _connections)
        {
            foreach (var conn in bag)
            {
                try
                {
                    await conn.Writer.WriteAsync(message);
                    await conn.Writer.FlushAsync();
                }
                catch
                {
                    // Connection dead
                }
            }
        }
    }
}

public class SseConnection
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public StreamWriter Writer { get; init; } = null!;
}

public class PlatformEvent
{
    public string Type { get; set; } = "";
    public string? RequestId { get; set; }
    public string? ServiceName { get; set; }
    public string? OldStatus { get; set; }
    public string? NewStatus { get; set; }
    public string? ActorName { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
