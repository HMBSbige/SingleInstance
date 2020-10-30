using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace SingleInstance
{
	/// <summary>
	/// Enforces single instance for an application.
	/// </summary>
	public sealed class SingleInstance : IDisposable
	{
		private readonly Mutex _mutex;
		private readonly bool _ownsMutex;
		private readonly string _identifier;
		private volatile Task _server;
		private readonly CancellationTokenSource _cts;

		/// <summary>
		/// Enforces single instance for an application.
		/// </summary>
		/// <param name="identifier">An identifier unique to this application.</param>
		public SingleInstance(string identifier)
		{
			_identifier = identifier;

			_mutex = new Mutex(true, identifier, out _ownsMutex);
			if (!_ownsMutex)
			{
				Dispose();
			}

			_cts = new CancellationTokenSource();
		}

		/// <summary>
		/// Indicates whether this is the first instance of this application.
		/// </summary>
		public bool IsFirstInstance => _ownsMutex;

		/// <summary>
		/// Passes the given arguments to the first running instance of the application.
		/// </summary>
		/// <param name="arguments">The arguments to pass.</param>
		/// <returns>Return true if the operation succeeded, false otherwise.</returns>
		public bool PassArgumentsToFirstInstance(IEnumerable<string> arguments)
		{
			if (IsFirstInstance)
			{
				throw new InvalidOperationException(@"This is the first instance.");
			}

			try
			{
				using var client = new NamedPipeClientStream(@".", _identifier, PipeDirection.Out);
				using var writer = new StreamWriter(client);
				client.Connect(200);

				foreach (var argument in arguments)
				{
					writer.WriteLine(argument);
				}

				return true;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Listens for arguments being passed from successive instances of the application.
		/// </summary>
		public void ListenForArgumentsFromSuccessiveInstances()
		{
			if (!IsFirstInstance)
			{
				throw new InvalidOperationException(@"This is not the first instance.");
			}

			if (_server == null)
			{
				_server = ListenForArguments(_cts.Token);
			}
		}

		/// <summary>
		/// Listens for arguments on a named pipe.
		/// </summary>
		private async Task ListenForArguments(CancellationToken token)
		{
			while (!token.IsCancellationRequested)
			{
				try
				{
					using var server = new NamedPipeServerStream(_identifier, PipeDirection.In);
					using var reader = new StreamReader(server);
					await server.WaitForConnectionAsync(token);

					var arguments = new List<string>();
					while (server.IsConnected)
					{
						var str = await reader.ReadLineAsync();
						if (str != null)
						{
							arguments.Add(str);
						}
						else
						{
							break;
						}
					}

					_argumentsReceived.OnNext(arguments.ToArray());
				}
				catch
				{
					// ignored
				}
			}
		}

		private readonly Subject<string[]> _argumentsReceived = new Subject<string[]>();
		public IObservable<string[]> ArgumentsReceived => _argumentsReceived;

		#region IDisposable

		private volatile bool _disposedValue;

		private void Dispose(bool disposing)
		{
			if (_disposedValue)
			{
				return;
			}

			if (disposing)
			{
				// 释放托管状态(托管对象)

				_cts?.Cancel();
				_mutex?.Dispose();
				_argumentsReceived.OnCompleted();
			}

			// 释放未托管的资源(未托管的对象)并替代终结器
			// 将大型字段设置为 null
			_disposedValue = true;
		}

		~SingleInstance()
		{
			Dispose(false);
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		#endregion
	}
}