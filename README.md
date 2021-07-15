# SingleInstance

Channel | Status
-|-
CI | [![CI](https://github.com/HMBSbige/SingleInstance/workflows/CI/badge.svg)](https://github.com/HMBSbige/SingleInstance/actions)
NuGet.org | [![NuGet.org](https://img.shields.io/nuget/v/HMBSbige.SingleInstance.svg?logo=nuget)](https://www.nuget.org/packages/HMBSbige.SingleInstance/)

# Usage
## Create
```csharp
const string identifier = @"Global\SingleInstance" // Change to your Guid or something unique
ISingleInstanceService singleInstance = new SingleInstanceService
{
    Identifier = identifier
};
// You should create service as singleton!
```

## Try register as single instance
```csharp
if(singleInstance.TryStartSingleInstance())
{
    // Success
}
else
{
    // There is another program register as same 'identifier'
}

if(singleInstance.IsFirstInstance)
{
    // This is the first instance.
}
else
{
    // This is not the first instance.
}
```

## Transfer data between instances
### 1. The first instance start listen server
```csharp
if(singleInstance.IsFirstInstance)
{
    singleInstance.StartListenServer();
}
```

### 2. Subscribe actions to handle incoming data
```csharp
singleInstance.ConnectionsReceived.SelectMany(ServerResponseAsync).Subscribe(); //Async
async Task<Unit> ServerResponseAsync(IDuplexPipe pipe)
{
    ReadResult result = await pipe.Input.ReadAsync();
    // Handle result
    await pipe.Input.CompleteAsync(); // No longer receiving data

    await Task.Delay(TimeSpan.FromSeconds(3)); // You may do some jobs

    await pipe.Output.WriteAsync(Encoding.UTF8.GetBytes("success")); // Send response
    await pipe.Output.CompleteAsync(); // No longer sending data
    return default;
}
```

### 3. The second instance send data to the first instance
```csharp
ISingleInstanceService singleInstance2 = new SingleInstanceService
{
    Identifier = identifier
};

ReadOnlyMemory<byte> data; // Your data
IDuplexPipe pipe = await singleInstance2.SendMessageToFirstInstanceAsync(data);
// Use Pipelines to handle data
```

## Release single instance before exiting
```csharp
singleInstance.Dispose();
```