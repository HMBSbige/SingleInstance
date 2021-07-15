using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace SingleInstance
{
	public interface ISingleInstanceService : IDisposable
	{
		string? Identifier { get; set; }

		bool IsFirstInstance { get; }

		IObservable<IDuplexPipe> ConnectionsReceived { get; }

		bool TryStartSingleInstance();

		ValueTask<IDuplexPipe> SendMessageToFirstInstanceAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default);

		void StartListenServer();
	}
}
