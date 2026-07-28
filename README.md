# SingleInstance

[![NuGet](https://img.shields.io/nuget/v/HMBSbige.SingleInstance.svg?logo=nuget)](https://www.nuget.org/packages/HMBSbige.SingleInstance/)

A .NET 10 library that elects one application instance and lets later instances notify it with simple integer commands over named pipes.

## Usage

```csharp
using SingleInstance;

await using SingleInstanceService instance = new("MyApplication");

if (!instance.IsFirstInstance)
{
	await instance.SendMessageAsync((int)AppCommand.Activate);
	return;
}

instance.StartListening(static command => HandleCommand((AppCommand)command));

// Continue into the application's normal run loop.
// Observe instance.ListenerCompletion to detect listener failures.

static void HandleCommand(AppCommand command)
{
	// Handle the command on the first instance.
}

enum AppCommand
{
	Activate = 1,
	ShowMainWindow = 2,
}
```

## Notes

- Use the same short, preferably ASCII, identifier in every instance; do not vary its casing or namespace prefix. Long identifiers can exceed Unix socket-path limits.
- The message handler must be synchronous. Do not pass an async lambda; synchronous handler exceptions are ignored, so catch and log them inside the handler.
- `StartListening` reports initial setup failures directly; observe `ListenerCompletion` for failures after startup.
- `SendMessageAsync` throws `TimeoutException` if it cannot connect within 10 seconds; use a `CancellationToken` for an earlier deadline.
- In UI applications, prefer `await using`. Synchronous `Dispose()` waits for the listener and active handler and can deadlock if the handler depends on the disposing thread.

## License

[MIT](LICENSE)
