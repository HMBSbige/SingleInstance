using System.Net.Sockets;

namespace SingleInstance.Tests;

public class ListenerFailurePolicyTests
{
	[Test]
	public async Task ClientAbortErrors_AreSeparatedFromInfrastructureErrors()
	{
		await Assert.That(SingleInstanceService.IsClientAcceptError(SocketError.ConnectionAborted)).IsTrue();
		await Assert.That(SingleInstanceService.IsClientAcceptError(SocketError.ConnectionReset)).IsTrue();
		await Assert.That(SingleInstanceService.IsClientAcceptError(SocketError.TooManyOpenSockets)).IsFalse();
		await Assert.That(SingleInstanceService.IsClientAcceptError(SocketError.NoBufferSpaceAvailable)).IsFalse();
	}

	[Test]
	public async Task WindowsClientAbortHResult_IsClassifiedAsClientError()
	{
		// Windows reports a client that connected and vanished before the accept as IOException with ERROR_NO_DATA.
		IOException errorNoData = new(@"The pipe is being closed.", unchecked((int)0x800700E8));
		await Assert.That(SingleInstanceService.IsClientAcceptError(errorNoData)).IsTrue();
		await Assert.That(SingleInstanceService.IsClientAcceptError(new IOException())).IsFalse();
	}

	[Test]
	public async Task TransientResourceErrors_AreRetriableUnderPressure()
	{
		await Assert.That(SingleInstanceService.IsTransientResourceError(SocketError.TooManyOpenSockets)).IsTrue();
		await Assert.That(SingleInstanceService.IsTransientResourceError(SocketError.NoBufferSpaceAvailable)).IsTrue();
		await Assert.That(SingleInstanceService.IsTransientResourceError(SocketError.AccessDenied)).IsFalse();
		await Assert.That(SingleInstanceService.IsTransientResourceError(SocketError.ConnectionAborted)).IsFalse();
	}

	[Test]
	public async Task CreateErrors_AreClassifiedByRecoverability()
	{
		await Assert.That(SingleInstanceService.IsRecoverableCreateError(new IOException())).IsTrue();
		await Assert.That(SingleInstanceService.IsRecoverableCreateError(new SocketException((int)SocketError.AddressAlreadyInUse))).IsTrue();
		await Assert.That(SingleInstanceService.IsRecoverableCreateError(new SocketException((int)SocketError.TooManyOpenSockets))).IsTrue();
		await Assert.That(SingleInstanceService.IsRecoverableCreateError(new SocketException((int)SocketError.NoBufferSpaceAvailable))).IsTrue();
		await Assert.That(SingleInstanceService.IsRecoverableCreateError(new SocketException((int)SocketError.AccessDenied))).IsFalse();
		await Assert.That(SingleInstanceService.IsRecoverableCreateError(new UnauthorizedAccessException())).IsFalse();
	}
}
