using System.Buffers.Binary;
using System.IO.Pipes;

namespace SingleInstance;

public sealed class SingleInstanceService : IDisposable
{
	private static readonly TimeSpan ListenRetryDelay = TimeSpan.FromMilliseconds(50);

	private readonly CancellationTokenSource _cancellation = new();
	private readonly string _identifier;
	private readonly Lock _lifecycleGate = new();
	private readonly Mutex? _mutex;
	private Task? _disposeTask;
	private int _handlerThreadId;
	private Task? _listenerTask;

	public SingleInstanceService(string identifier)
	{
		ArgumentNullException.ThrowIfNull(identifier);

		// Constructing a client stream validates the pipe name (e.g. rejects "" and "anonymous") without creating any OS handle.
		new NamedPipeClientStream(@".", identifier, PipeDirection.Out, PipeOptions.Asynchronous).Dispose();

		_identifier = identifier;

		Mutex mutex = new(false, identifier, out bool createdNew);
		IsFirstInstance = createdNew;

		if (createdNew)
		{
			_mutex = mutex;
		}
		else
		{
			mutex.Dispose();
		}
	}

	public bool IsFirstInstance { get; }

	public async Task SendMessageAsync(int message, CancellationToken cancellationToken = default)
	{
		lock (_lifecycleGate)
		{
			ThrowIfDisposed();

			if (IsFirstInstance)
			{
				throw new InvalidOperationException("This is the first instance.");
			}
		}

		await using NamedPipeClientStream client = new(@".", _identifier, PipeDirection.Out, PipeOptions.Asynchronous);
		await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

		byte[] buffer = new byte[sizeof(int)];
		BinaryPrimitives.WriteInt32BigEndian(buffer, message);
		await client.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
	}

	public void StartListening(Action<int> messageHandler)
	{
		lock (_lifecycleGate)
		{
			ThrowIfDisposed();

			if (!IsFirstInstance)
			{
				throw new InvalidOperationException("This is not the first instance.");
			}

			if (_listenerTask is not null)
			{
				throw new InvalidOperationException("Server already started!");
			}

			CancellationToken cancellationToken = _cancellation.Token;
			_listenerTask = Task.Run(() => ListenAsync(messageHandler, cancellationToken), cancellationToken);
		}
	}

	private async Task ListenAsync(Action<int> messageHandler, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				try
				{
					await using NamedPipeServerStream server = new
					(
						_identifier,
						PipeDirection.In,
						1,
						PipeTransmissionMode.Byte,
						PipeOptions.Asynchronous
					);
					await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

					byte[] buffer = new byte[sizeof(int)];
					await server.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

					InvokeHandler(messageHandler, BinaryPrimitives.ReadInt32BigEndian(buffer));
				}
				catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is IOException or UnauthorizedAccessException)
				{
					// The pipe can be temporarily unavailable (e.g. a previous owner is still shutting down) or a client can disconnect mid-command.
					await Task.Delay(ListenRetryDelay, cancellationToken).ConfigureAwait(false);
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	private void InvokeHandler(Action<int> messageHandler, int message)
	{
		_handlerThreadId = Environment.CurrentManagedThreadId;

		try
		{
			messageHandler(message);
		}
		catch
		{
			// A failing handler must not stop the listener.
		}
		finally
		{
			_handlerThreadId = 0;
		}
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
	}

	public void Dispose()
	{
		Task disposeTask;

		lock (_lifecycleGate)
		{
			if (_disposeTask is null)
			{
				_cancellation.Cancel();
				_disposeTask = DisposeCoreAsync(_listenerTask);
			}

			disposeTask = _disposeTask;
		}

		// Blocking inside the message handler would deadlock: the listener task cannot end until the handler returns.
		if (_handlerThreadId != Environment.CurrentManagedThreadId)
		{
			disposeTask.GetAwaiter().GetResult();
		}
	}

	private async Task DisposeCoreAsync(Task? listenerTask)
	{
		if (listenerTask is not null)
		{
			try
			{
				await listenerTask.ConfigureAwait(false);
			}
			catch
			{
				// A dead listener must not prevent releasing the single-instance ownership.
			}
		}

		_mutex?.Dispose();
		_cancellation.Dispose();
	}
}
