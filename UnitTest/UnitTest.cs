using Microsoft.VisualStudio.TestTools.UnitTesting;
using SingleInstance;
using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace UnitTest
{
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
			const string identifier = @"Global\SingleInstance.TestInstance";

			using var singleInstance = CreateNewInstance(identifier);
			Assert.IsFalse(singleInstance.IsFirstInstance);
			Assert.IsTrue(singleInstance.TryStartSingleInstance());
			Assert.IsTrue(singleInstance.IsFirstInstance);

			using var singleInstance2 = CreateNewInstance(identifier);
			Assert.IsFalse(singleInstance2.TryStartSingleInstance());
			Assert.IsFalse(singleInstance2.IsFirstInstance);
		}

		[TestMethod]
		public void TestDispose()
		{
			const string identifier = @"Global\SingleInstance.TestDispose";

			var singleInstance = CreateNewInstance(identifier);
			Assert.IsTrue(singleInstance.TryStartSingleInstance());

			var singleInstance2 = CreateNewInstance(identifier);
			Assert.IsFalse(singleInstance2.TryStartSingleInstance());

			singleInstance.Dispose();

			var singleInstance3 = CreateNewInstance(identifier);
			Assert.IsTrue(singleInstance3.TryStartSingleInstance());

			singleInstance2.Dispose();

			var singleInstance4 = CreateNewInstance(identifier);
			Assert.IsFalse(singleInstance4.TryStartSingleInstance());
		}

		[TestMethod]
		public async Task TestSendMessageToFirstInstanceAsync()
		{
			const string identifier = @"Global\SingleInstance.TestPassArgumentsAsync";
			const string clientSendStr = @"Hello!";
			const string serverResponseStr = @"你好！";

			var server = CreateNewInstance(identifier);
			Assert.IsTrue(server.TryStartSingleInstance());

			var client = CreateNewInstance(identifier);
			Assert.IsFalse(client.TryStartSingleInstance());

			server.StartListenServer();

			Assert.ThrowsException<InvalidOperationException>(() => server.StartListenServer());
			Assert.ThrowsException<InvalidOperationException>(() => client.StartListenServer());

			server.Received.SelectMany(ServerResponseAsync).Subscribe();

			var clientReceive = await client.SendMessageToFirstInstanceAsync(clientSendStr);

			Assert.AreEqual(serverResponseStr, clientReceive);

			static async Task<Unit> ServerResponseAsync((string, Action<string>) receive)
			{
				var (message, endFunc) = receive;
				Assert.AreEqual(clientSendStr, message);

				await Task.Delay(TimeSpan.FromSeconds(3));

				endFunc(serverResponseStr);

				return default;
			}
		}
	}
}
