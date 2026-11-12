using Microsoft.AspNetCore.SignalR;

namespace Etw.Backend.Hubs;

/// <summary>
/// Push channel to the desktop app. The server sends "runEvent" (a RunEventDto) and "runStatus" (a RunDto);
/// there are no client-to-server methods, so everything that changes state goes through the authenticated REST API.
/// </summary>
public sealed class RunsHub : Hub
{
}
