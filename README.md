# SingleInstance

[![NuGet](https://img.shields.io/nuget/v/HMBSbige.SingleInstance.svg?logo=nuget)](https://www.nuget.org/packages/HMBSbige.SingleInstance/)

A .NET 10 library that elects one application instance and lets later instances notify it with simple integer commands over named pipes.

## Usage

```csharp
using SingleInstance;

using SingleInstanceService instance = new("MyApplication");

if (!instance.IsFirstInstance)
{
	await instance.SendMessageAsync((int)AppCommand.Activate);
	return;
}

instance.StartListening(static command => HandleCommand((AppCommand)command));

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

## License

[MIT](LICENSE)
