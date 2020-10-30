# SingleInstance

Channel | Status
-|-
Build | [![GitHub CI](https://github.com/HMBSbige/SingleInstance/workflows/GitHub%20CI/badge.svg)](https://github.com/HMBSbige/SingleInstance/actions)
Unit testing | [![Unit testing](https://github.com/HMBSbige/SingleInstance/workflows/Unit%20testing/badge.svg)](https://github.com/HMBSbige/SingleInstance/actions)
NuGet.org | [![NuGet.org](https://img.shields.io/nuget/v/HMBSbige.SingleInstance.svg)](https://www.nuget.org/packages/HMBSbige.SingleInstance/)

# Example
```csharp
var singleInstance = new SingleInstance(@"Global\SingleInstance");

if (!singleInstance.IsFirstInstance)
{
    singleInstance.PassArgumentsToFirstInstance(e.Args);
    return;
}

singleInstance.ArgumentsReceived.ObserveOnDispatcher().Subscribe(args => { });
singleInstance.ListenForArgumentsFromSuccessiveInstances();

singleInstance.Dispose();
```
