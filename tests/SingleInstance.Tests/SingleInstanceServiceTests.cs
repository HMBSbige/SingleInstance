using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;

namespace SingleInstance.Tests;

[Timeout(30_000)]
public class SingleInstanceServiceTests
{
	[Test]
	[SuppressMessage("ReSharper", "DisposeOnUsingVariable", Justification = "This test intentionally disposes services before the end of the scope to verify ownership transfer.")]
	public async Task Ownership_IsExclusiveAndTransfersAfterOwnerDisposes(CancellationToken cancellationToken)
	{
		string identifier = CreateIdentifier();

		using SingleInstanceService owner = CreateService(identifier);
		await Assert.That(owner.IsFirstInstance).IsTrue();

		using SingleInstanceService contender = CreateService(identifier);
		await Assert.That(contender.IsFirstInstance).IsFalse();
		contender.Dispose();

		using SingleInstanceService anotherContender = CreateService(identifier);
		await Assert.That(anotherContender.IsFirstInstance).IsFalse();

		owner.Dispose();
		using SingleInstanceService replacement = CreateService(identifier);
		await Assert.That(replacement.IsFirstInstance).IsTrue();
	}

	[Test]
	[SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "TUnit invokes and awaits these delegates before the services leave scope.")]
	public async Task SendMessageAsync_NotifiesFirstInstance(CancellationToken cancellationToken)
	{
		const int command = -123_456_789;
		string identifier = CreateIdentifier();
		TaskCompletionSource<int> commandReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

		using SingleInstanceService server = CreateService(identifier);
		await Assert.That(server.IsFirstInstance).IsTrue();
		server.StartListening(received => commandReceived.TrySetResult(received));

		using SingleInstanceService client = CreateService(identifier);
		await Assert.That(client.IsFirstInstance).IsFalse();

		await Assert.That(() => server.StartListening(static _ => { })).Throws<InvalidOperationException>();
		await Assert.That(() => client.StartListening(static _ => { })).Throws<InvalidOperationException>();
		await Assert.That(async () => await server.SendMessageAsync(command, cancellationToken)).Throws<InvalidOperationException>();

		await client.SendMessageAsync(command, cancellationToken);
		await Assert.That(await commandReceived.Task.WaitAsync(cancellationToken)).IsEqualTo(command);
	}

	[Test]
	public async Task SendMessageAsync_DoesNotWaitForHandlerCompletion(CancellationToken cancellationToken)
	{
		string identifier = CreateIdentifier();
		using ManualResetEventSlim handlerCanReturn = new(false);
		using SingleInstanceService server = CreateService(identifier);
		Task handlerStarted = StartBlockingHandler(server, handlerCanReturn, cancellationToken);
		using SingleInstanceService client = CreateService(identifier);

		Task sendTask = client.SendMessageAsync(1, cancellationToken);
		bool completedBeforeHandlerReturned;

		try
		{
			await handlerStarted.WaitAsync(cancellationToken);
			Task completed = await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
			completedBeforeHandlerReturned = ReferenceEquals(completed, sendTask);
		}
		finally
		{
			handlerCanReturn.Set();
		}

		await sendTask.WaitAsync(cancellationToken);
		await Assert.That(completedBeforeHandlerReturned).IsTrue();
	}

	[Test]
	public async Task StartListening_ContinuesAfterHandlerFailure(CancellationToken cancellationToken)
	{
		string identifier = CreateIdentifier();
		TaskCompletionSource firstCommandHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<int> secondCommandReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int commandCount = 0;

		using SingleInstanceService server = CreateService(identifier);
		server.StartListening
		(
			command =>
			{
				if (Interlocked.Increment(ref commandCount) == 1)
				{
					firstCommandHandled.TrySetResult();
					throw new InvalidOperationException("handler failure");
				}

				secondCommandReceived.TrySetResult(command);
			}
		);
		using SingleInstanceService client = CreateService(identifier);

		await client.SendMessageAsync(1, cancellationToken);
		await firstCommandHandled.Task.WaitAsync(cancellationToken);
		await client.SendMessageAsync(2, cancellationToken);

		await Assert.That(await secondCommandReceived.Task.WaitAsync(cancellationToken)).IsEqualTo(2);
		await Assert.That(commandCount).IsEqualTo(2);
	}

	[Test]
	public async Task SendMessageAsync_WritesExactlyOneBigEndianInt32(CancellationToken cancellationToken)
	{
		const int command = unchecked((int)0x89ABCDEF);
		string identifier = CreateIdentifier();

		using SingleInstanceService owner = CreateService(identifier);
		using SingleInstanceService client = CreateService(identifier);
		using NamedPipeServerStream rawServer = new(identifier, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		Task connection = rawServer.WaitForConnectionAsync(cancellationToken);

		Task sendTask = client.SendMessageAsync(command, cancellationToken);
		await connection;
		byte[] actualCommand = new byte[sizeof(int)];
		await rawServer.ReadExactlyAsync(actualCommand, cancellationToken);
		await sendTask;

		byte[] extra = new byte[1];
		int extraLength = await rawServer.ReadAsync(extra, cancellationToken);
		await Assert.That(actualCommand.SequenceEqual(new byte[] { 0x89, 0xAB, 0xCD, 0xEF })).IsTrue();
		await Assert.That(extraLength).IsEqualTo(0);
	}

	[Test]
	public async Task StartListening_ContinuesAfterTruncatedCommand(CancellationToken cancellationToken)
	{
		const int command = int.MinValue;
		string identifier = CreateIdentifier();
		TaskCompletionSource<int> commandReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int handlerCalls = 0;

		using SingleInstanceService server = CreateService(identifier);
		server.StartListening
		(
			received =>
			{
				Interlocked.Increment(ref handlerCalls);
				commandReceived.TrySetResult(received);
			}
		);

		await SendRawBytesAsync(identifier, new byte[] { 0x00, 0x01, 0x02 }, cancellationToken);

		using SingleInstanceService client = CreateService(identifier);
		await client.SendMessageAsync(command, cancellationToken);
		await Assert.That(await commandReceived.Task.WaitAsync(cancellationToken)).IsEqualTo(command);
		await Assert.That(handlerCalls).IsEqualTo(1);
	}

	[Test]
	public async Task SendMessageAsync_CancelsBlockedConnect(CancellationToken cancellationToken)
	{
		string identifier = CreateIdentifier();
		using SingleInstanceService owner = CreateService(identifier);
		using SingleInstanceService client = CreateService(identifier);
		using CancellationTokenSource connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		Task sendTask = client.SendMessageAsync(1, connectCancellation.Token);
		connectCancellation.Cancel();

		await Assert.That
		(
			async () => await sendTask
		).Throws<OperationCanceledException>();
	}

	[Test]
	[SuppressMessage("ReSharper", "DisposeOnUsingVariable", Justification = "This test intentionally calls Dispose repeatedly to verify idempotency.")]
	[SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "This test intentionally invokes operations on a disposed service to verify ObjectDisposedException.")]
	public async Task Dispose_IsIdempotentAndRejectsFurtherOperations(CancellationToken cancellationToken)
	{
		using SingleInstanceService service = CreateService(CreateIdentifier());
		service.Dispose();
		service.Dispose();

		await Assert.That(() => service.StartListening(static _ => { })).Throws<ObjectDisposedException>();
		await Assert.That(async () => await service.SendMessageAsync(1, cancellationToken)).Throws<ObjectDisposedException>();
	}

	[Test]
	public async Task Dispose_CancelsIncompleteCommand(CancellationToken cancellationToken)
	{
		string identifier = CreateIdentifier();
		int handlerCalls = 0;
		using SingleInstanceService server = CreateService(identifier);
		server.StartListening(_ => Interlocked.Increment(ref handlerCalls));

		using (NamedPipeClientStream rawClient = new(@".", identifier, PipeDirection.Out, PipeOptions.Asynchronous))
		{
			await rawClient.ConnectAsync(cancellationToken);
			await rawClient.WriteAsync(new byte[] { 0x00, 0x01 }, cancellationToken);
			await rawClient.FlushAsync(cancellationToken);

			Task disposeTask = Task.Run(server.Dispose, CancellationToken.None);
			await disposeTask.WaitAsync(cancellationToken);
		}

		await Assert.That(handlerCalls).IsEqualTo(0);
		using SingleInstanceService replacement = CreateService(identifier);
		await Assert.That(replacement.IsFirstInstance).IsTrue();
	}

	[Test]
	public async Task Dispose_WaitsForActiveHandlerBeforeReleasingInstanceMarker(CancellationToken cancellationToken)
	{
		string identifier = CreateIdentifier();
		using ManualResetEventSlim handlerCanReturn = new(false);
		using SingleInstanceService server = CreateService(identifier);
		Task handlerStarted = StartBlockingHandler(server, handlerCanReturn, cancellationToken);
		using SingleInstanceService client = CreateService(identifier);

		Task sendTask = client.SendMessageAsync(1, cancellationToken);
		await handlerStarted.WaitAsync(cancellationToken);
		Task disposeTask = Task.Run(server.Dispose, CancellationToken.None);
		await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
		bool disposeCompletedEarly = disposeTask.IsCompleted;

		using SingleInstanceService contender = CreateService(identifier);
		bool contenderBecameFirst = contender.IsFirstInstance;

		handlerCanReturn.Set();
		await sendTask;
		await disposeTask;

		using SingleInstanceService replacement = CreateService(identifier);
		await Assert.That(disposeCompletedEarly).IsFalse();
		await Assert.That(contenderBecameFirst).IsFalse();
		await Assert.That(replacement.IsFirstInstance).IsTrue();
	}

	[Test]
	[SuppressMessage("ReSharper", "DisposeOnUsingVariable", Justification = "This test intentionally disposes the server from its handler and again afterward to verify idempotent shutdown.")]
	[SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "This test waits for server shutdown before the captured handler can outlive its scope.")]
	public async Task Dispose_FromMessageHandler_CompletesWithoutDeadlock(CancellationToken cancellationToken)
	{
		string identifier = CreateIdentifier();
		TaskCompletionSource disposeReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using SingleInstanceService server = CreateService(identifier);
		server.StartListening
		(
			_ =>
			{
				server.Dispose();
				disposeReturned.TrySetResult();
			}
		);
		using SingleInstanceService client = CreateService(identifier);

		await client.SendMessageAsync(1, cancellationToken);
		await disposeReturned.Task.WaitAsync(cancellationToken);
		server.Dispose();

		using SingleInstanceService replacement = CreateService(identifier);
		await Assert.That(replacement.IsFirstInstance).IsTrue();
	}

	[Test]
	[SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "The server is disposed before the captured synchronization primitive leaves scope.")]
	public async Task StartListening_ProcessesMessagesSequentially(CancellationToken cancellationToken)
	{
		string identifier = CreateIdentifier();
		using ManualResetEventSlim firstCanReturn = new(false);
		TaskCompletionSource<int> firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<int> secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int commandCount = 0;
		using SingleInstanceService server = CreateService(identifier);
		server.StartListening
		(
			command =>
			{
				if (Interlocked.Increment(ref commandCount) == 1)
				{
					firstStarted.TrySetResult(command);
					firstCanReturn.Wait(cancellationToken);
				}
				else
				{
					secondStarted.TrySetResult(command);
				}
			}
		);
		using SingleInstanceService firstClient = CreateService(identifier);
		using SingleInstanceService secondClient = CreateService(identifier);

		Task firstSend = firstClient.SendMessageAsync(1, cancellationToken);
		int firstCommand = await firstStarted.Task.WaitAsync(cancellationToken);
		Task secondSend = secondClient.SendMessageAsync(2, cancellationToken);
		await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
		bool secondStartedEarly = secondStarted.Task.IsCompleted;

		firstCanReturn.Set();
		await Task.WhenAll(firstSend, secondSend);
		int secondCommand = await secondStarted.Task.WaitAsync(cancellationToken);

		await Assert.That(secondStartedEarly).IsFalse();
		await Assert.That(firstCommand).IsEqualTo(1);
		await Assert.That(secondCommand).IsEqualTo(2);
	}

	[Test]
	public async Task SingleInstanceService_CoordinatesAcrossProcesses(CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeout = CreateTimeout(cancellationToken);
		string identifier = CreateIdentifier();
		using Process helper = StartTestApp(identifier);

		try
		{
			await Assert.That(await ReadHelperLineAsync(helper, timeout.Token)).IsEqualTo("READY");

			using SingleInstanceService contender = CreateService(identifier);
			await Assert.That(contender.IsFirstInstance).IsFalse();
			await contender.SendMessageAsync(42, timeout.Token);
			await Assert.That(await ReadHelperLineAsync(helper, timeout.Token)).IsEqualTo("COMMAND:42");

			await helper.StandardInput.WriteLineAsync("stop");
			await helper.StandardInput.FlushAsync();
			await helper.WaitForExitAsync(timeout.Token);
			await Assert.That(helper.ExitCode).IsEqualTo(0);
			await Assert.That(contender.IsFirstInstance).IsFalse();

			using SingleInstanceService replacement = CreateService(identifier);
			await Assert.That(replacement.IsFirstInstance).IsTrue();
		}
		finally
		{
			await EnsureProcessStoppedAsync(helper);
		}
	}

	[Test]
	public async Task GlobalIdentifier_CoordinatesOwnershipAndMessaging(CancellationToken cancellationToken)
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		const int command = int.MaxValue;
		string identifier = $@"Global\SingleInstance.Tests.{Guid.NewGuid():N}";
		TaskCompletionSource<int> commandReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using SingleInstanceService first = CreateService(identifier);
		await Assert.That(first.IsFirstInstance).IsTrue();
		using SingleInstanceService second = CreateService(identifier);
		await Assert.That(second.IsFirstInstance).IsFalse();

		first.StartListening(received => commandReceived.TrySetResult(received));
		await second.SendMessageAsync(command, cancellationToken);
		await Assert.That(await commandReceived.Task.WaitAsync(cancellationToken)).IsEqualTo(command);
	}

	// The trailing CancellationToken parameters are mandated by the class-level TimeoutAttribute (TUnit0015) even when unused.
	[Test]
	[SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "The awaited TUnit Throws assertion invokes the delegate before the service leaves its using scope.")]
	public async Task StartListening_RejectsNullHandler(CancellationToken cancellationToken)
	{
		using SingleInstanceService service = CreateService(CreateIdentifier());
		await Assert.That(() => service.StartListening(null!)).Throws<ArgumentNullException>();
	}

	[Test]
	[SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "The awaited TUnit Throws assertion invokes the delegate before the service leaves its using scope.")]
	public async Task StartListening_SurfacesUnusableIdentifier(CancellationToken cancellationToken)
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		// "." passes client-side pipe name validation but cannot be used as a server pipe name on Windows.
		using SingleInstanceService service = CreateService(@".");
		await Assert.That(() => service.StartListening(static _ => { })).Throws<UnauthorizedAccessException>();
	}

	[Test]
	public async Task StartListening_ContinuesAfterStalledClient(CancellationToken cancellationToken)
	{
		const int command = 7;
		string identifier = CreateIdentifier();
		TaskCompletionSource<int> commandReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

		using SingleInstanceService server = CreateService(identifier);
		server.StartListening(received => commandReceived.TrySetResult(received));

		// Connect and send a truncated command without ever disconnecting, then verify a real client still gets through.
		using NamedPipeClientStream stalledClient = new(@".", identifier, PipeDirection.Out, PipeOptions.Asynchronous);
		await stalledClient.ConnectAsync(cancellationToken);
		await stalledClient.WriteAsync(new byte[] { 0x00 }, cancellationToken);
		await stalledClient.FlushAsync(cancellationToken);

		using SingleInstanceService client = CreateService(identifier);
		await client.SendMessageAsync(command, cancellationToken);
		await Assert.That(await commandReceived.Task.WaitAsync(cancellationToken)).IsEqualTo(command);
	}

	[Test]
	[SuppressMessage("ReSharper", "DisposeOnUsingVariable", Justification = "This test intentionally disposes the service before the end of the scope to verify idempotency and ownership release.")]
	[SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "This test intentionally invokes operations on a disposed service to verify ObjectDisposedException.")]
	public async Task DisposeAsync_ReleasesOwnershipAndRejectsFurtherOperations(CancellationToken cancellationToken)
	{
		string identifier = CreateIdentifier();
		await using SingleInstanceService service = CreateService(identifier);
		service.StartListening(static _ => { });

		await service.DisposeAsync();
		await service.DisposeAsync();

		await Assert.That(() => service.StartListening(static _ => { })).Throws<ObjectDisposedException>();
		await Assert.That(async () => await service.SendMessageAsync(1, cancellationToken)).Throws<ObjectDisposedException>();
		using SingleInstanceService replacement = CreateService(identifier);
		await Assert.That(replacement.IsFirstInstance).IsTrue();
	}

	[Test]
	[SuppressMessage("ReSharper", "DisposeOnUsingVariable", Justification = "This test intentionally disposes the server before the end of the scope to verify non-blocking disposal.")]
	public async Task DisposeAsync_DoesNotBlockButWaitsForActiveHandler(CancellationToken cancellationToken)
	{
		string identifier = CreateIdentifier();
		using ManualResetEventSlim handlerCanReturn = new(false);
		await using SingleInstanceService server = CreateService(identifier);
		Task handlerStarted = StartBlockingHandler(server, handlerCanReturn, cancellationToken);
		using SingleInstanceService client = CreateService(identifier);

		Task sendTask;
		Task disposeTask;
		bool disposeCompletedEarly;

		try
		{
			sendTask = client.SendMessageAsync(1, cancellationToken);
			await handlerStarted.WaitAsync(cancellationToken);

			// DisposeAsync must return its ValueTask without blocking the calling thread even while a handler is running.
			disposeTask = server.DisposeAsync().AsTask();
			await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
			disposeCompletedEarly = disposeTask.IsCompleted;
		}
		finally
		{
			handlerCanReturn.Set();
		}

		await sendTask;
		await disposeTask.WaitAsync(cancellationToken);

		using SingleInstanceService replacement = CreateService(identifier);
		await Assert.That(disposeCompletedEarly).IsFalse();
		await Assert.That(replacement.IsFirstInstance).IsTrue();
	}

	[Test]
	[SuppressMessage("ReSharper", "DisposeOnUsingVariable", Justification = "This test intentionally disposes the server before the end of the scope to assert ListenerCompletion; the using declaration provides failure-path cleanup and repeated disposal is idempotent.")]
	public async Task ListenerCompletion_TracksListenerLifecycle(CancellationToken cancellationToken)
	{
		string identifier = CreateIdentifier();
		using SingleInstanceService server = CreateService(identifier);
		await Assert.That(server.ListenerCompletion.IsCompleted).IsTrue();

		server.StartListening(static _ => { });
		await Assert.That(server.ListenerCompletion.IsCompleted).IsFalse();

		server.Dispose();
		await Assert.That(server.ListenerCompletion.IsCompletedSuccessfully).IsTrue();
	}

	[Test]
	[SuppressMessage("ReSharper", "DisposeOnUsingVariable", Justification = "Each iteration intentionally disposes the server before asserting ListenerCompletion; the using declaration provides failure-path cleanup and repeated disposal is idempotent.")]
	public async Task Dispose_RacingClientDisconnect_CompletesListenerSuccessfully(CancellationToken cancellationToken)
	{
		// Regression: a truncated client disconnect racing with disposal used to fault ListenerCompletion
		// with EndOfStreamException instead of completing it successfully.
		for (int iteration = 0; iteration < 10; ++iteration)
		{
			string identifier = CreateIdentifier();
			using SingleInstanceService server = CreateService(identifier);
			server.StartListening(static _ => { });

			await SendRawBytesAsync(identifier, new byte[] { 0x00 }, cancellationToken);

			server.Dispose();
			await Assert.That(server.ListenerCompletion.IsCompletedSuccessfully).IsTrue();
		}
	}

	[Test]
	[SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "The awaited TUnit Throws assertion invokes and awaits the delegate before the client leaves its using scope.")]
	public async Task SendMessageAsync_ThrowsTimeoutWhenFirstInstanceNeverListens(CancellationToken cancellationToken)
	{
		string identifier = CreateIdentifier();
		using SingleInstanceService owner = CreateService(identifier);
		using SingleInstanceService client = new(identifier) { ConnectTimeout = TimeSpan.FromMilliseconds(250) };

		await Assert.That(async () => await client.SendMessageAsync(1, cancellationToken)).Throws<TimeoutException>();

		// The ten-second default is a documented contract (README); pin the value so changing it forces a doc update.
		using SingleInstanceService defaultClient = CreateService(identifier);
		await Assert.That(defaultClient.ConnectTimeout).IsEqualTo(TimeSpan.FromSeconds(10));
	}

	[Test]
	[SuppressMessage("ReSharper", "DisposeOnUsingVariable", Justification = "This test intentionally disposes the client to cancel the pending send; the using declaration provides failure-path cleanup and repeated disposal is idempotent.")]
	public async Task Dispose_CancelsPendingSend(CancellationToken cancellationToken)
	{
		string identifier = CreateIdentifier();
		using SingleInstanceService owner = CreateService(identifier);
		using SingleInstanceService client = CreateService(identifier);

		// The owner never listens, so the send waits in ConnectAsync (bounded by the default connect timeout) until the client is disposed.
		Task sendTask = client.SendMessageAsync(1, cancellationToken);
		client.Dispose();

		await Assert.That(async () => await sendTask.WaitAsync(cancellationToken)).Throws<OperationCanceledException>();
	}

	private static SingleInstanceService CreateService(string identifier)
	{
		return new SingleInstanceService(identifier);
	}

	private static Task StartBlockingHandler
	(
		SingleInstanceService server,
		ManualResetEventSlim handlerCanReturn,
		CancellationToken cancellationToken
	)
	{
		TaskCompletionSource handlerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		server.StartListening
		(
			_ =>
			{
				handlerStarted.TrySetResult();
				handlerCanReturn.Wait(cancellationToken);
			}
		);

		return handlerStarted.Task;
	}

	private static async Task SendRawBytesAsync
	(
		string identifier,
		ReadOnlyMemory<byte> message,
		CancellationToken cancellationToken
	)
	{
		using NamedPipeClientStream client = new(@".", identifier, PipeDirection.Out, PipeOptions.Asynchronous);
		await client.ConnectAsync(cancellationToken);
		await client.WriteAsync(message, cancellationToken);
		await client.FlushAsync(cancellationToken);
	}

	private static string CreateIdentifier()
	{
		// Keep identifiers short: on macOS the pipe path (TMPDIR + "CoreFxPipe_" + name + NUL) must fit sun_path's 104 bytes,
		// and 16 hex characters leave headroom for long custom TMPDIR values while staying collision-safe for tests.
		return Guid.NewGuid().ToString(@"N")[..16];
	}

	private static CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
	{
		CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(15));
		return timeout;
	}

	private static Process StartTestApp(string identifier)
	{
		string application = Path.Combine(AppContext.BaseDirectory, "SingleInstance.TestApp.dll");
		if (!File.Exists(application))
		{
			throw new FileNotFoundException("The cross-process test application was not built.", application);
		}

		ProcessStartInfo startInfo = new("dotnet")
		{
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			UseShellExecute = false,
			WorkingDirectory = AppContext.BaseDirectory,
		};
		startInfo.ArgumentList.Add(application);
		startInfo.ArgumentList.Add("server");
		startInfo.ArgumentList.Add(identifier);

		return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the cross-process test application.");
	}

	private static async Task<string> ReadHelperLineAsync(Process process, CancellationToken cancellationToken)
	{
		string? line = await process.StandardOutput.ReadLineAsync(cancellationToken);
		if (line is not null)
		{
			return line;
		}

		await process.WaitForExitAsync(cancellationToken);
		string error = await process.StandardError.ReadToEndAsync(cancellationToken);
		throw new InvalidOperationException($"The cross-process test application exited with code {process.ExitCode}: {error}");
	}

	private static async Task EnsureProcessStoppedAsync(Process process)
	{
		if (process.HasExited)
		{
			return;
		}

		try
		{
			await process.StandardInput.WriteLineAsync("stop");
			await process.StandardInput.FlushAsync();
		}
		catch (IOException)
		{
		}

		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
		try
		{
			await process.WaitForExitAsync(timeout.Token);
		}
		catch (OperationCanceledException)
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}

			await process.WaitForExitAsync();
		}
	}
}
