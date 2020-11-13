using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace UnitTest
{
	[TestClass]
	public class UnitTest
	{
		[TestMethod]
		public void TestInstance()
		{
			const string identifier = @"Global\SingleInstance.TestInstance";

			using var singleInstance = new SingleInstance.SingleInstance(identifier);
			Assert.IsTrue(singleInstance.IsFirstInstance);

			using var singleInstance2 = new SingleInstance.SingleInstance(identifier);
			Assert.IsFalse(singleInstance2.IsFirstInstance);
		}

		[TestMethod]
		public void TestDispose()
		{
			const string identifier = @"Global\SingleInstance.TestDispose";

			var singleInstance = new SingleInstance.SingleInstance(identifier);
			Assert.IsTrue(singleInstance.IsFirstInstance);

			var singleInstance2 = new SingleInstance.SingleInstance(identifier);
			Assert.IsFalse(singleInstance2.IsFirstInstance);

			singleInstance.Dispose();

			singleInstance = new SingleInstance.SingleInstance(identifier);
			Assert.IsTrue(singleInstance.IsFirstInstance);

			singleInstance2.Dispose();

			singleInstance2 = new SingleInstance.SingleInstance(identifier);
			Assert.IsFalse(singleInstance2.IsFirstInstance);
		}

		[TestMethod]
		public async Task TestPassArgumentsAsync()
		{
			const string identifier = @"Global\SingleInstance.TestPassArgumentsAsync";
			const string success = @"success";
			const string fail = @"fail";
			var tcs = new TaskCompletionSource<bool>();

			using var singleInstance = new SingleInstance.SingleInstance(identifier);

			singleInstance.ArgumentsReceived.Subscribe(args =>
			{
				if (args.First() == success && args.Last() == fail)
				{
					tcs.SetResult(true);
				}
				else
				{
					tcs.SetResult(false);
				}
			});
			singleInstance.ListenForArgumentsFromSuccessiveInstances();

			using var singleInstance2 = new SingleInstance.SingleInstance(identifier);

			Assert.IsTrue(singleInstance2.PassArgumentsToFirstInstance(new[] { success, fail }));

			Assert.IsTrue(await tcs.Task);
		}
	}
}
