using Nerdbank.Streams;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using PipeOptions = System.IO.Pipes.PipeOptions;

namespace SingleInstance
{
	public class SingleInstanceService : ISingleInstanceService
	{
		public string? Identifier { get; set; }

		public bool IsFirstInstance => _ownsMutex;

		private readonly Subject<IDuplexPipe> _connectionsReceived = new();
		public IObservable<IDuplexPipe> ConnectionsReceived => _connectionsReceived;

		private Mutex? _mutex;
		private bool _ownsMutex;
		private volatile Task? _server;
		private readonly CancellationTokenSource _cts = new();

		public bool TryStartSingleInstance()
		{
			CheckDispose();

			CheckIdentifierNotNull();

			_mutex ??= new Mutex(true, Identifier, out _ownsMutex);
			if (!_ownsMutex)
			{
				_mutex.Dispose();
			}
			return _ownsMutex;
		}

		public async ValueTask<IDuplexPipe> SendMessageToFirstInstanceAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
		{
			CheckDispose();

			if (IsFirstInstance)
			{
				throw new InvalidOperationException(@"This is the first instance.");
			}

			CheckIdentifierNotNull();

			var client = new NamedPipeClientStream(@".", Identifier, PipeDirection.InOut, PipeOptions.Asynchronous);
			await client.ConnectAsync(200, token);

			var pipe = client.UsePipe(cancellationToken: token);

			await pipe.Output.WriteAsync(buffer, token);

			return pipe;
		}

		public void StartListenServer()
		{
			CheckDispose();

			if (!IsFirstInstance)
			{
				throw new InvalidOperationException(@"This is not the first instance.");
			}

			if (_server is not null)
			{
				throw new InvalidOperationException(@"Server already started!");
			}

			CheckIdentifierNotNull();
			_server = ListenServerInternalAsync(Identifier, _cts.Token);

			async Task ListenServerInternalAsync(string identifier, CancellationToken token)
			{
				while (!token.IsCancellationRequested)
				{
					try
					{
						var server = new NamedPipeServerStream(identifier, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
						await server.WaitForConnectionAsync(token);

						var pipe = server.UsePipe(cancellationToken: token);

						_connectionsReceived.OnNext(pipe);
					}
					catch
					{
						// ignored
					}
				}
			}
		}

		[MemberNotNull(nameof(Identifier))]
		private void CheckIdentifierNotNull()
		{
			if (Identifier is null)
			{
				throw new ArgumentNullException(nameof(Identifier));
			}
		}

		#region Dispose

		private void CheckDispose()
		{
			if (_isDispose)
			{
				throw new ObjectDisposedException(@"This instance has been disposed!");
			}
		}

		private volatile bool _isDispose;
		public void Dispose()
		{
			if (_isDispose)
			{
				return;
			}
			_isDispose = true;

			_cts.Cancel();
			_mutex?.Dispose();
			_connectionsReceived.OnCompleted();
		}

		#endregion
	}
}
