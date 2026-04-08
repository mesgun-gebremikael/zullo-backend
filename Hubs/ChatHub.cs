using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Zullo.Api.Hubs;

[Authorize]
public class ChatHub : Hub
{
}
