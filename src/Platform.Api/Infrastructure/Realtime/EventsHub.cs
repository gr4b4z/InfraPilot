using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Platform.Api.Infrastructure.Realtime;

/// <summary>
/// The realtime channel to the web app. Server-to-client only — the API pushes "entityChanged"
/// signals (see <see cref="EntityChangedEvent"/>) and "notification" messages; clients invoke
/// nothing. Authorization mirrors the REST endpoints: any authenticated user may connect, and
/// since browsers cannot set an Authorization header on a WebSocket handshake the JWT arrives
/// as ?access_token=… (accepted for hub paths only, see Program.cs).
/// </summary>
[Authorize]
public class EventsHub : Hub
{
}
