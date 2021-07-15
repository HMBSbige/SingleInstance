using Microsoft.VisualStudio.TestTools.UnitTesting;
using SingleInstance;
using System;
using System.IO.Pipelines;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UnitTest
{
	[TestClass]
	public class UnitTest
	{
		private static ISingleInstanceService CreateNewInstance(string identifier)
		{
			return new SingleInstanceService
			{
				Identifier = identifier
			};
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

			server.ConnectionsReceived.SelectMany(ServerResponseAsync).Subscribe();

			var clientPipe = await client.SendMessageToFirstInstanceAsync(Encoding.UTF8.GetBytes(clientSendStr));
			await clientPipe.Output.CompleteAsync();

			var clientResult = await clientPipe.Input.ReadAsync();
			var response = Encoding.UTF8.GetString(clientResult.Buffer);
			Assert.AreEqual(serverResponseStr, response);
			await clientPipe.Input.CompleteAsync();

			static async Task<Unit> ServerResponseAsync(IDuplexPipe pipe)
			{
				var result = await pipe.Input.ReadAsync();
				var str = Encoding.UTF8.GetString(result.Buffer);
				Assert.AreEqual(clientSendStr, str);
				await pipe.Input.CompleteAsync();

				await Task.Delay(TimeSpan.FromSeconds(3));

				await pipe.Output.WriteAsync(Encoding.UTF8.GetBytes(serverResponseStr));
				await pipe.Output.CompleteAsync();
				return default;
			}
		}
	}
}
