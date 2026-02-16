# SlackNet 0.17.9 Socket Mode Implementation Notes

## API Information for SlackNet 0.17.9

### Required Namespaces
```csharp
using SlackNet;
using SlackNet.Events;
using SlackNet.SocketMode;
```

### Event Types (in SlackNet.Events namespace)
- `MessageEvent` - Message events
- `AppMention` - App mention events  
- NOT `AppMentionEvent` or `MessageChan` - these don't exist

### Event Handler Interface
```csharp
public interface IEventHandler<T>
{
    Task Handle(T slackEvent);
}
```

### Socket Mode Client Constructor
The `SlackSocketModeClient` constructor in 0.17.9 requires:
```csharp
SlackSocketModeClient(
    ICoreSocketModeClient coreClient,
    SlackJsonSettings jsonSettings,
    IEnumerable<ISlackRequestListener> requestListeners,
    ISlackHandlerFactory handlerFactory,
    SlackNet.ILogger logger) // Note: SlackNet's ILogger, not Microsoft's
```

This makes direct instantiation very complex.

### Key Issues with 0.17.9
1. No `AddSlackNet()` extension method for ASP.NET Core DI
2. Complex constructor dependencies for `SlackSocketModeClient`
3. No simple factory methods like `SlackSocketModeClient.ConnectAsync()`
4. `ILogger` namespace collision with Microsoft.Extensions.Logging

### Recommendations
1. **Socket Mode only** - Webhook handling has been removed from the codebase
2. **Upgrade SlackNet** - Newer versions may have better Socket Mode support
3. **Alternative Library** - Consider Slack's official .NET SDK if available

### Current Working Approach
Inbound events are handled exclusively via Socket Mode in `SlackSocketModeService`.

Socket Mode is primarily useful for:
- Development/testing without public URLs
- Apps that can't expose webhooks
