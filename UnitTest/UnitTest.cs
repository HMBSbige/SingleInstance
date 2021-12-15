using Microsoft.VisualStudio.TestTools.UnitTesting;
using SingleInstance;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;

namespace UnitTest;

[TestClass]
public class UnitTest
{
	private static ISingleInstanceService CreateNewInstance(string identifier)
	{
		return new SingleInstanceService(identifier);
	}

	[TestMethod]
	public void TestSingleInstance()
	{
		const string identifier = @"Global\SingleInstance.Test1";

		using ISingleInstanceService singleInstance = CreateNewInstance(identifier);
		Assert.IsFalse(singleInstance.IsFirstInstance);
		Assert.IsTrue(singleInstance.TryStartSingleInstance());
		Assert.IsTrue(singleInstance.IsFirstInstance);

		using ISingleInstanceService singleInstance2 = CreateNewInstance(identifier);
		Assert.IsFalse(singleInstance2.TryStartSingleInstance());
		Assert.IsFalse(singleInstance2.IsFirstInstance);
	}

	[TestMethod]
	public void TestDispose()
	{
		const string identifier = @"Global\SingleInstance.Test";

		ISingleInstanceService singleInstance = CreateNewInstance(identifier);
		Assert.IsTrue(singleInstance.TryStartSingleInstance());

		ISingleInstanceService singleInstance2 = CreateNewInstance(identifier);
		Assert.IsFalse(singleInstance2.TryStartSingleInstance());

		singleInstance.Dispose();

		ISingleInstanceService singleInstance3 = CreateNewInstance(identifier);
		Assert.IsTrue(singleInstance3.TryStartSingleInstance());

		singleInstance2.Dispose();

		ISingleInstanceService singleInstance4 = CreateNewInstance(identifier);
		Assert.IsFalse(singleInstance4.TryStartSingleInstance());
	}

	[TestMethod]
	public async Task TestSendMessageToFirstInstanceAsync()
	{
		const string identifier = @"Global\SingleInstance.Test2";
		const string clientSendStr = @"Hello!";
		const string serverResponseStr = @"你好！";

		ISingleInstanceService server = CreateNewInstance(identifier);
		Assert.IsTrue(server.TryStartSingleInstance());

		ISingleInstanceService client = CreateNewInstance(identifier);
		Assert.IsFalse(client.TryStartSingleInstance());

		server.StartListenServer();

		Assert.ThrowsException<InvalidOperationException>(() => server.StartListenServer());
		Assert.ThrowsException<InvalidOperationException>(() => client.StartListenServer());

		server.Received.ObserveOn(Scheduler.Default).SelectMany(ServerResponseAsync).Subscribe();

		string clientReceive = await client.SendMessageToFirstInstanceAsync(clientSendStr);

		Assert.AreEqual(serverResponseStr, clientReceive);

		static async Task<Unit> ServerResponseAsync((string, Action<string>) receive)
		{
			(string message, Action<string> endFunc) = receive;
			Assert.AreEqual(clientSendStr, message);

			await Task.Delay(TimeSpan.FromSeconds(3));

			endFunc(serverResponseStr);

			return default;
		}
	}

	[TestMethod]
	public void TestCheckIdentifier()
	{
		const string identifier = @"Global\SingleInstance.Test3";

		Assert.ThrowsException<ArgumentNullException>(() => CreateNewInstance(null!));
		Assert.ThrowsException<ArgumentException>(() => CreateNewInstance(@""));
		Assert.ThrowsException<ArgumentOutOfRangeException>(() => CreateNewInstance(@"anonymous"));

		if (OperatingSystem.IsWindows())
		{
			ISingleInstanceService s1 = CreateNewInstance(identifier);
			Assert.IsTrue(s1.TryStartSingleInstance());
			s1.StartListenServer();
			CreateNewInstance(identifier);
		}
		else if (OperatingSystem.IsLinux())
		{
			CreateNewInstance(identifier);
		}
		else if (OperatingSystem.IsMacOS())
		{
			Assert.ThrowsException<ArgumentOutOfRangeException>(() => CreateNewInstance(@"Global\SingleInstance.TestPassArgumentsAsync"));
		}
	}
}
