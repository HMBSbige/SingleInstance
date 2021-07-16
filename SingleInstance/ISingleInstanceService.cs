using System;
using System.Threading;
using System.Threading.Tasks;

namespace SingleInstance
{
	public interface ISingleInstanceService : IDisposable
	{
		string? Identifier { get; }

		bool IsFirstInstance { get; }

		IObservable<(string, Action<string>)> Received { get; }

		bool TryStartSingleInstance();

		ValueTask<string> SendMessageToFirstInstanceAsync(string message, CancellationToken token = default);

		void StartListenServer();
	}
}
