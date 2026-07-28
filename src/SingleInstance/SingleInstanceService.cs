using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;

namespace SingleInstance;

/// <summary>Elects one application instance per identifier and lets later instances notify it with integer commands over named pipes.</summary>
public sealed class SingleInstanceService : IDisposable, IAsyncDisposable
{
	private const int MaxConsecutiveFailures = 20;

	private static readonly TimeSpan ListenRetryDelay = TimeSpan.FromMilliseconds(50);
	private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan CommandReadTimeout = TimeSpan.FromSeconds(5);

	private readonly CancellationTokenSource _cancellation = new();
	private readonly string _identifier;
	private readonly Lock _lifecycleGate = new();
	private readonly Mutex? _mutex;
	private Task? _disposeTask;
	private int _handlerThreadId;
	private Task? _listenerTask;

	/// <summary>Creates the service and immediately competes for ownership of <paramref name="identifier"/>.</summary>
	/// <param name="identifier">Names both the ownership mutex and the command pipe. Use one canonical value everywhere, preferably short ASCII: on Unix it becomes part of a length-limited socket path.</param>
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

	/// <summary>True when this instance won the election and may call <see cref="StartListening"/>; later instances send commands instead.</summary>
	public bool IsFirstInstance { get; }

	/// <summary>
	/// Completes when the listener stops. It faults if the listener cannot recover from a transport failure.
	/// Await or attach a continuation after <see cref="StartListening"/> to observe such failures.
	/// Completes successfully when disposal stops a healthy listener, or immediately if listening has not started;
	/// disposal does not overwrite an existing listener fault.
	/// </summary>
	public Task ListenerCompletion => _listenerTask ?? Task.CompletedTask;

	// Internal for tests: lets the connect-timeout failure path run without waiting out the ten-second default.
	internal TimeSpan ConnectTimeout { get; init; } = DefaultConnectTimeout;

	/// <summary>Sends an integer command to the first instance. Only valid on later instances.</summary>
	/// <param name="message">The command value delivered to the first instance's handler.</param>
	/// <param name="cancellationToken">Cancels the send; disposing this service also cancels it.</param>
	/// <remarks>Throws <see cref="TimeoutException"/> when the first instance cannot be reached within ten seconds.</remarks>
	public async Task SendMessageAsync(int message, CancellationToken cancellationToken = default)
	{
		CancellationToken lifetimeToken;

		lock (_lifecycleGate)
		{
			ThrowIfDisposed();

			if (IsFirstInstance)
			{
				throw new InvalidOperationException("This is the first instance.");
			}

			lifetimeToken = _cancellation.Token;
		}

		// Linking the service lifetime cancels pending sends when disposal begins.
		using CancellationTokenSource sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
		using NamedPipeClientStream client = new(@".", _identifier, PipeDirection.Out, PipeOptions.Asynchronous);
		await client.ConnectAsync(ConnectTimeout, sendCancellation.Token).ConfigureAwait(false);

		byte[] buffer = new byte[sizeof(int)];
		BinaryPrimitives.WriteInt32BigEndian(buffer, message);
		await client.WriteAsync(buffer, sendCancellation.Token).ConfigureAwait(false);
	}

	/// <summary>Starts listening for commands sent by later instances.</summary>
	/// <remarks>
	/// <paramref name="messageHandler"/> must complete synchronously: an async lambda degrades to async void, so neither the
	/// listener nor <see cref="DisposeAsync"/> waits for it and its exceptions bypass the built-in isolation. Exceptions thrown
	/// by the handler are swallowed to keep the listener alive — catch and log inside the handler to observe failures.
	/// This method throws synchronously when the very first server cannot be created; later unrecoverable listener failures
	/// fault <see cref="ListenerCompletion"/> instead.
	/// </remarks>
	public void StartListening(Action<int> messageHandler)
	{
		ArgumentNullException.ThrowIfNull(messageHandler);

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

			// Creating the first server synchronously surfaces every initial creation failure (occupied name, invalid identifier, access denied) instead of retrying in the background.
			NamedPipeServerStream server = CreateServer();
			CancellationToken cancellationToken = _cancellation.Token;

			// No token on Task.Run: ListenAsync must always run so its finally block disposes the pre-created server.
			_listenerTask = Task.Run(() => ListenAsync(server, messageHandler, cancellationToken));
		}
	}

	private NamedPipeServerStream CreateServer()
	{
		return new NamedPipeServerStream
		(
			_identifier,
			PipeDirection.In,
			1,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous
		);
	}

	private async Task ListenAsync(NamedPipeServerStream? currentServer, Action<int> messageHandler, CancellationToken cancellationToken)
	{
		// The server instance is reused across connections: recreating it per message would close the
		// underlying Unix domain socket and drop clients already queued in its backlog.
		try
		{
			// Creation and accept failures share one budget across server rebuilds. Only a successful accept resets it.
			int consecutiveInfrastructureFailures = 0;

			while (!cancellationToken.IsCancellationRequested)
			{
				if (currentServer is null)
				{
					try
					{
						currentServer = CreateServer();
					}
					catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsRecoverableCreateError(exception))
					{
						if (++consecutiveInfrastructureFailures >= MaxConsecutiveFailures)
						{
							throw;
						}

						await Task.Delay(ListenRetryDelay, cancellationToken).ConfigureAwait(false);
						continue;
					}
				}

				try
				{
					await currentServer.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
					consecutiveInfrastructureFailures = 0;
				}
				catch (SocketException exception) when (!cancellationToken.IsCancellationRequested && IsClientAcceptError(exception.SocketErrorCode))
				{
					// A client aborting between connect and accept only costs its own connection; the listener is healthy, so accept again immediately without spending the budget.
					continue;
				}
				catch (IOException exception) when (!cancellationToken.IsCancellationRequested && IsClientAcceptError(exception))
				{
					// Windows surfaces the same client abort as ERROR_NO_DATA; reset this pipe instance and keep accepting without spending the budget.
					currentServer = DisconnectOrDispose(currentServer);
					continue;
				}
				catch (SocketException exception) when (!cancellationToken.IsCancellationRequested && IsTransientResourceError(exception.SocketErrorCode))
				{
					// EMFILE/ENOBUFS style pressure leaves the listening socket usable; keep it so queued Unix clients are not dropped.
					if (++consecutiveInfrastructureFailures >= MaxConsecutiveFailures)
					{
						throw;
					}

					await Task.Delay(ListenRetryDelay, cancellationToken).ConfigureAwait(false);
					continue;
				}
				catch (IOException) when (!cancellationToken.IsCancellationRequested)
				{
					// The listening socket itself is broken; nothing queued can be saved, so rebuild.
					if (++consecutiveInfrastructureFailures >= MaxConsecutiveFailures)
					{
						throw;
					}

					currentServer.Dispose();
					currentServer = null;
					await Task.Delay(ListenRetryDelay, cancellationToken).ConfigureAwait(false);
					continue;
				}

				int message;

				try
				{
					byte[] buffer = new byte[sizeof(int)];

					// A client that connects but never completes a command must not block the listener forever.
					using CancellationTokenSource readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
					readTimeout.CancelAfter(CommandReadTimeout);
					await currentServer.ReadExactlyAsync(buffer, readTimeout.Token).ConfigureAwait(false);

					message = BinaryPrimitives.ReadInt32BigEndian(buffer);
				}
				catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is IOException or OperationCanceledException)
				{
					// A single failed client (truncated write, stall, disconnect) must only cost its own connection, never the listening socket.
					currentServer = DisconnectOrDispose(currentServer);
					continue;
				}

				currentServer = DisconnectOrDispose(currentServer);
				InvokeHandler(messageHandler, message);
			}
		}
		catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is OperationCanceledException or IOException or SocketException)
		{
			// Disposal races with in-flight pipe operations (e.g. a client disconnecting mid-command); these are expected shutdown outcomes, not listener failures.
		}
		finally
		{
			currentServer?.Dispose();
		}
	}

	internal static bool IsRecoverableCreateError(Exception exception)
	{
		// On Unix the server creates/binds/listens on a domain socket directly, so transient resource errors surface as SocketException instead of IOException.
		return exception switch
		{
			SocketException socketException => socketException.SocketErrorCode is SocketError.AddressAlreadyInUse || IsTransientResourceError(socketException.SocketErrorCode),
			IOException => true,
			_ => false,
		};
	}

	// Win32 ERROR_NO_DATA (232) as an HRESULT: how Windows reports a client that connected and vanished before the accept completed.
	private const int ErrorNoDataHResult = unchecked((int)0x800700E8);

	// A client aborting between connect and accept must only cost its own connection, never the listener's failure budget.
	internal static bool IsClientAcceptError(SocketError error)
	{
		return error is SocketError.ConnectionAborted or SocketError.ConnectionReset;
	}

	internal static bool IsClientAcceptError(IOException exception)
	{
		return exception.HResult is ErrorNoDataHResult;
	}

	internal static bool IsTransientResourceError(SocketError error)
	{
		return error is SocketError.TooManyOpenSockets or SocketError.NoBufferSpaceAvailable;
	}

	private static NamedPipeServerStream? DisconnectOrDispose(NamedPipeServerStream server)
	{
		try
		{
			server.Disconnect();
			return server;
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException)
		{
			server.Dispose();
			return null;
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

	/// <summary>Releases the single-instance ownership after the listener and any running handler finish.</summary>
	/// <remarks>
	/// When called from inside the message handler this returns early without waiting, so ownership is released only after the
	/// handler returns. It can also deadlock when a running handler synchronously waits on the disposing thread (e.g. a UI
	/// thread); prefer <see cref="DisposeAsync"/> in such environments.
	/// </remarks>
	public void Dispose()
	{
		Task disposeTask = BeginDispose();

		// Blocking inside the message handler would deadlock: the listener task cannot end until the handler returns.
		if (_handlerThreadId != Environment.CurrentManagedThreadId)
		{
			disposeTask.GetAwaiter().GetResult();
		}
	}

	/// <summary>Begins releasing the single-instance ownership; unlike <see cref="Dispose"/>, it does not synchronously wait for the listener or a running handler to finish. The returned task completes once both have finished.</summary>
	public async ValueTask DisposeAsync()
	{
		await BeginDispose().ConfigureAwait(false);
	}

	private Task BeginDispose()
	{
		lock (_lifecycleGate)
		{
			if (_disposeTask is null)
			{
				_cancellation.Cancel();
				_disposeTask = DisposeCoreAsync(_listenerTask);
			}

			return _disposeTask;
		}
	}

	private async Task DisposeCoreAsync(Task? listenerTask)
	{
		if (listenerTask is not null)
		{
			await listenerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
		}

		_mutex?.Dispose();
		_cancellation.Dispose();
	}
}
