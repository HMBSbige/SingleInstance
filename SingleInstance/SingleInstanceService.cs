using Microsoft;
using Pipelines.Extensions;
using System.Buffers;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Reactive.Subjects;
using System.Text;
using PipeOptions = System.IO.Pipes.PipeOptions;

namespace SingleInstance;

public class SingleInstanceService : ISingleInstanceService
{
	public string Identifier { get; }

	public bool IsFirstInstance => _ownsMutex;

	private readonly Subject<(string, Action<string>)> _received = new();
	public IObservable<(string, Action<string>)> Received => _received;

	private Mutex? _mutex;
	private bool _ownsMutex;
	private volatile Task? _server;
	private readonly CancellationTokenSource _cts = new();
	protected static ReadOnlySpan<byte> EndDelimiter => new[] { (byte)'\r', (byte)'\n' };

	private static readonly StreamPipeReaderOptions LeaveOpenReaderOptions = new(leaveOpen: true);
	private static readonly StreamPipeWriterOptions LeaveOpenWriterOptions = new(leaveOpen: true);

	public SingleInstanceService(string identifier)
	{
		CheckIdentifierValid(identifier);
		Identifier = identifier;
	}

	public bool TryStartSingleInstance()
	{
		CheckDispose();

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

		Verify.Operation(!IsFirstInstance, @"This is the first instance.");

		await using NamedPipeClientStream client = new(@".", Identifier, PipeDirection.InOut, PipeOptions.Asynchronous);
		await client.ConnectAsync(200, token);

		IDuplexPipe pipe = client.AsDuplexPipe(writerOptions: LeaveOpenWriterOptions);

		// Send message
		try
		{
			int length = Encoding.UTF8.GetBytes(message, pipe.Output.GetSpan(Encoding.UTF8.GetMaxByteCount(message.Length)));
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

		Verify.Operation(IsFirstInstance, @"This is not the first instance.");
		Verify.Operation(_server is null, @"Server already started!");

		_server = ListenServerInternalAsync(Identifier, _cts.Token);

		async Task ListenServerInternalAsync(string identifier, CancellationToken token)
		{
			while (!token.IsCancellationRequested)
			{
				try
				{
					NamedPipeServerStream server = new(identifier, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
					await server.WaitForConnectionAsync(token);

					IDuplexPipe pipe = server.AsDuplexPipe(LeaveOpenReaderOptions);

					string receive = await ReadAsync(pipe.Input, token);

					if (string.IsNullOrEmpty(receive))
					{
						continue;
					}

					void SendResponseAsync(string message)
					{
						try
						{
							int length = Encoding.UTF8.GetBytes(message, pipe.Output.GetSpan(Encoding.UTF8.GetMaxByteCount(message.Length)));
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

				ReadResult result = await reader.ReadAsync(token);
				ReadOnlySequence<byte> buffer = result.Buffer;
				try
				{
					string? response = HandleResponse(ref buffer);

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
		SequenceReader<byte> reader = new(buffer);

		if (!reader.TryReadTo(out ReadOnlySequence<byte> read, EndDelimiter))
		{
			return default;
		}

		buffer = reader.UnreadSequence;
		return Encoding.UTF8.GetString(read);
	}

	private static void CheckIdentifierValid(string identifier)
	{
		Requires.NotNull(identifier, nameof(identifier));

		using (new Mutex(false, identifier))
		{

		}

		using (new NamedPipeClientStream(@".", identifier, PipeDirection.InOut, PipeOptions.Asynchronous))
		{

		}

		try
		{
			using (new NamedPipeServerStream(identifier, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
			{

			}
		}
		catch (IOException)
		{

		}
	}

	#region Dispose

	private void CheckDispose()
	{
		Verify.NotDisposed(this, @"This instance has been disposed!");
	}

	public bool IsDisposed { get; private set; }

	public void Dispose()
	{
		if (IsDisposed)
		{
			return;
		}

		_cts.Cancel();
		_mutex?.Dispose();
		_received.OnCompleted();

		IsDisposed = true;
		GC.SuppressFinalize(this);
	}

	#endregion
}
