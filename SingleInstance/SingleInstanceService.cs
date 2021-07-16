using Nerdbank.Streams;
using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PipeOptions = System.IO.Pipes.PipeOptions;

namespace SingleInstance
{
	public class SingleInstanceService : ISingleInstanceService
	{
		public string? Identifier { get; init; }

		public bool IsFirstInstance => _ownsMutex;

		private readonly Subject<(string, Action<string>)> _received = new();
		public IObservable<(string, Action<string>)> Received => _received;

		private Mutex? _mutex;
		private bool _ownsMutex;
		private volatile Task? _server;
		private readonly CancellationTokenSource _cts = new();
		private static ReadOnlySpan<byte> EndDelimiter => new[] { (byte)'\r', (byte)'\n' };

		public SingleInstanceService(string identifier)
		{
			Identifier = identifier;
		}

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

		public async ValueTask<string> SendMessageToFirstInstanceAsync(string message, CancellationToken token = default)
		{
			CheckDispose();

			if (IsFirstInstance)
			{
				throw new InvalidOperationException(@"This is the first instance.");
			}

			CheckIdentifierNotNull();

			await using var client = new NamedPipeClientStream(@".", Identifier, PipeDirection.InOut, PipeOptions.Asynchronous);
			await client.ConnectAsync(200, token);

			var pipe = client.UsePipe(cancellationToken: token);

			// Send message
			try
			{
				var length = Encoding.UTF8.GetBytes(message, pipe.Output.GetSpan(Encoding.UTF8.GetMaxByteCount(message.Length)));
				pipe.Output.Advance(length);

				EndDelimiter.CopyTo(pipe.Output.GetSpan(EndDelimiter.Length));
				pipe.Output.Advance(EndDelimiter.Length);
			}
			finally
			{
				await pipe.Output.CompleteAsync();
			}

			return await ReadAsync(pipe.Input, token);
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

						var receive = await ReadAsync(pipe.Input, token);

						if (string.IsNullOrEmpty(receive))
						{
							continue;
						}

						void SendResponseAsync(string message)
						{
							try
							{
								var length = Encoding.UTF8.GetBytes(message, pipe.Output.GetSpan(Encoding.UTF8.GetMaxByteCount(message.Length)));
								pipe.Output.Advance(length);

								EndDelimiter.CopyTo(pipe.Output.GetSpan(EndDelimiter.Length));
								pipe.Output.Advance(EndDelimiter.Length);
							}
							finally
							{

								pipe.Output.Complete();
							}
						}

						_received.OnNext((receive, SendResponseAsync));
					}
					catch
					{
						// ignored
					}
				}
			}
		}

		private static async ValueTask<string> ReadAsync(PipeReader reader, CancellationToken token)
		{
			try
			{
				while (true)
				{
					token.ThrowIfCancellationRequested();

					var result = await reader.ReadAsync(token);
					var buffer = result.Buffer;
					try
					{
						var response = HandleResponse(ref buffer);

						if (response is not null)
						{
							return response;
						}

						if (result.IsCompleted)
						{
							break;
						}
					}
					finally
					{
						reader.AdvanceTo(buffer.Start, buffer.End);
					}
				}
			}
			finally
			{
				await reader.CompleteAsync();
			}

			return string.Empty;
		}

		private static string? HandleResponse(ref ReadOnlySequence<byte> buffer)
		{
			var reader = new SequenceReader<byte>(buffer);

			if (!reader.TryReadTo(out ReadOnlySequence<byte> read, EndDelimiter))
			{
				return default;
			}

			buffer = reader.UnreadSequence;
			return Encoding.UTF8.GetString(read);
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

			_cts.Cancel();
			_mutex?.Dispose();
			_received.OnCompleted();

			_isDispose = true;
			GC.SuppressFinalize(this);
		}

		#endregion
	}
}
