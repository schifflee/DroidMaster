﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Managed.Adb.Exceptions;
using Nito.AsyncEx;
using Renci.SshNet.Common;

namespace DroidMaster.Core {
	///<summary>
	/// Wraps an ephemeral connection to a device, automatically retrying 
	/// commands against a new connection from <see cref="PersistentDeviceManager"/> 
	/// if the connection fails.
	///</summary>
	///<remarks>
	/// A <see cref="PersistentDevice"/> is initially created with a connection.  It will hold
	/// this connection and run commands against it until a connection error is thrown.  After
	/// that happens, the connection is disposed and cleared, and subsequent commands will not
	/// run until a new connection arrives via <see cref="SetDevice(IDeviceConnection)"/>.  If
	/// <see cref="SetDevice(IDeviceConnection)"/> is called while connection is active, it'll
	/// wait for all commands against the previous connection to complete, and replace the old
	/// connection (and dispose it) with the new one.
	/// 
	/// This class maintains the following invariants:
	///  1. A new connection will not be installed until all operations running against the current connection finish.
	///  2. A connection will not be disposed until all operations running against the current connection finish.
	///  3. Any instance ever set to <see cref="volatileDeviceSource"/> will not dangle in the presence of a connection.
	///     (if an unresolved task is set, it will not be replaced.
	///  4. Any connection that is passed to <see cref="SetDevice(IDeviceConnection)"/> will be eventually be disposed,
	///     when it errors, is replaced with a new connection or when the entire class is disposed.
	///</remarks>
	class PersistentDevice : NotifyPropertyChanged {
		// ADB chokes on too much parallel activity.
		// I apply the lock here, so that it can use
		// our cancellation token, and to prevent it
		// from blocking persistent ID discovery.
		readonly SemaphoreSlim semaphore = new SemaphoreSlim(4);

		///<summary>Controls reads and writes of <see cref="volatileDeviceSource"/>.</summary>
		readonly AsyncReaderWriterLock sourceLock = new AsyncReaderWriterLock();
		///<summary>The current device, if any, or a promise that will resolve to the current device once it arrives.</summary>
		///<remarks>
		/// It is always safe to wait for this promise, but you must enter
		/// a read lock and double-check the promise again once it arrives
		///</remarks>
		volatile TaskCompletionSource<IDeviceConnection> volatileDeviceSource;

		///<summary>Gets the persistent identifier for this device.</summary>
		public string ConnectionId { get; }


		public PersistentDevice(string id, IDeviceConnection initialConnection) {
			ConnectionId = id;
			volatileDeviceSource = CompletedSource(initialConnection);
			LatestConnectionId = initialConnection.ConnectionId;
		}

		public ICommandResult ExecuteShellCommand(string command) {
			var result = new ForwardingCommandResult { CommandText = command };
			PropertyChangedEventHandler onPropertyChanged = (s, e) => result.OnPropertyChanged(e);
			result.Complete = Execute(c => {
				// Each time we re-execute the command, change our outer
				// result to reflect the result of the current execution
				if (result.Inner != null)
					result.Inner.PropertyChanged -= onPropertyChanged;
				result.Inner = c.ExecuteShellCommand(command);
				result.Inner.PropertyChanged += onPropertyChanged;
				return result.Inner.Complete;
			});
			result.Complete.ContinueWith(_ => result.OnPropertyChanged(new PropertyChangedEventArgs(nameof(result.Output))), TaskContinuationOptions.OnlyOnCanceled);
			return result;
		}

		class ForwardingCommandResult : ICommandResult {
			public ICommandResult Inner { get; set; }
			public Task<string> Complete { get; set; }

			public string Output => Complete.IsCanceled ? "(Cancelled)" : Inner?.Output;
			public string CommandText { get; set; }

			public event PropertyChangedEventHandler PropertyChanged;
			public void OnPropertyChanged(PropertyChangedEventArgs e) { PropertyChanged?.Invoke(this, e); }
		}

		public Task PullFileAsync(string devicePath, string localPath, IProgress<double> progress = null) {
			return Execute(d => d.PullFileAsync(devicePath, localPath, CurrentToken, progress));
		}

		public Task PushFileAsync(string localPath, string devicePath, IProgress<double> progress = null) {
			return Execute(d => d.PushFileAsync(localPath, devicePath, CurrentToken, progress));
		}

		public Task RebootAsync() {
			return Execute(d => d.RebootAsync());
		}

		///<summary>Gets or sets an optional <see cref="CancellationTokenSource"/> that will cancel all active or pending operations when set.</summary>
		///<remarks>Setting this property will not affect any operations that have already started.</remarks>
		public CancellationTokenSource CancellationToken { get; set; }
		///<summary>Gets the current token from the <see cref="CancellationToken"/>, if any.</summary>
		CancellationToken CurrentToken => CancellationToken?.Token ?? new CancellationToken();

		Task Execute(Func<IDeviceConnection, Task> operation) => Execute(async c => { await operation(c); return true; });
		///<summary>Keeps running an operation against the current connection until no errors occur.</summary>
		async Task<T> Execute<T>(Func<IDeviceConnection, Task<T>> operation) {
			while (true) {
				var currentSource = volatileDeviceSource;
				var token = CurrentToken;	// Only read this field once
				await Task.WhenAny(token.AsTask(), currentSource.Task).ConfigureAwait(false);
				token.ThrowIfCancellationRequested();

				using (await sourceLock.ReaderLockAsync(CurrentToken).ConfigureAwait(false)) {
					// If a different device was installed between waiting for the source and
					// acquiring the lock, start over.
					if (currentSource != volatileDeviceSource)
						continue;
					try {
						// We already awaited currentSource.Task, so .Result is safe
						using (await semaphore.DisposableWaitAsync(token).ConfigureAwait(false))
							return await operation(currentSource.Task.Result).ConfigureAwait(false);
						// If a connection-level error occurs, clear the device, then wait for the next connection.
					} catch (SocketException ex) {
						HandleConnectionError(currentSource, ex);
					} catch (IOException ex) when (!(ex is FileNotFoundException)) {
						HandleConnectionError(currentSource, ex);
					} catch (DeviceNotFoundException ex) {
						HandleConnectionError(currentSource, ex);
					} catch (ShellCommandUnresponsiveException ex) {
						HandleConnectionError(currentSource, ex);
					} catch (SshConnectionException ex) {
						HandleConnectionError(currentSource, ex);
					} catch (ObjectDisposedException ex) {	// SshClients seem to occasionally get stuck and dispose themselves.
						HandleConnectionError(currentSource, ex);
					} catch (ProxyException ex) {
						HandleConnectionError(currentSource, ex);
					}
				}
			}
		}

		///<summary>Releases the current connection, causing all future commands to wait for a new connection.  This is called inside the read lock.</summary>
		/// <param name="currentSource">The source instance holding the device that failed.  This must already be resolved.</param>
		/// <param name="ex">The exception from the device.</param>
		private void HandleConnectionError(TaskCompletionSource<IDeviceConnection> currentSource, Exception ex) {
			var newSource = new TaskCompletionSource<IDeviceConnection>();
			// Immediately clear the stored source to opportunistically
			// make new tasks wait for the next device. If a task slips
			// through, it will get a connection error and nothing will
			// happen.
			if (Interlocked.CompareExchange(ref volatileDeviceSource, newSource, currentSource) != currentSource)
				return;
			// Escape the read lock, then dispose the old connection in
			// a write lock to make sure that all tasks have finished.
			Task.Run(async () => {
				OnPropertyChanged(nameof(CurrentConnectionMethod));
				// Don't raise events inside a lock.
				OnConnectionError(new ConnectionErrorEventArgs(currentSource.Task.Result, ex));
				using (await sourceLock.WriterLockAsync().ConfigureAwait(false))
					currentSource.Task.Result.Dispose();
			});
		}

		///<summary>Provides a new connection, causing any commands that are waiting for a connection to run immediately.</summary>
		public async Task SetDevice(IDeviceConnection newDevice) {
			if (newDevice == null) throw new ArgumentNullException(nameof(newDevice));

			// Wait for all commands to finish before replacing the connection.
			using (await sourceLock.WriterLockAsync().ConfigureAwait(false)) {
				var oldDevice = volatileDeviceSource;

				// If we're waiting for a connection, simply resolve the promise.
				if (!oldDevice.Task.IsCompleted)
					oldDevice.SetResult(newDevice);
				else {
					// If we already have a device, replace it and dispose the old one.
					volatileDeviceSource = CompletedSource(newDevice);
					oldDevice.Task.Result.Dispose();
				}
				LatestConnectionId = newDevice.ConnectionId;
			}
			OnConnectionEstablished();
			OnPropertyChanged(nameof(LatestConnectionId));
			OnPropertyChanged(nameof(CurrentConnectionMethod));
		}

		///<summary>Gets an ID for the most recent connection.</summary>
		public string LatestConnectionId { get; private set; }
		///<summary>Gets the display name of the current connection type, or "Offline".</summary>
		public string CurrentConnectionMethod => volatileDeviceSource.Task.IsCompleted ? volatileDeviceSource.Task.Result.Owner.DisplayName : "Offline";

		#region Events
		///<summary>Occurs when a connection-level error is thrown.</summary>
		public event EventHandler<ConnectionErrorEventArgs> ConnectionError;
		///<summary>Raises the ConnectionError event.</summary>
		///<param name="e">A ConnectionErrorEventArgs object that provides the event data.</param>
		protected internal virtual void OnConnectionError(ConnectionErrorEventArgs e) => ConnectionError?.Invoke(this, e);

		///<summary>Occurs when a new connection is provided.</summary>
		public event EventHandler ConnectionEstablished;
		///<summary>Raises the ConnectionEstablished event.</summary>
		internal protected virtual void OnConnectionEstablished() => OnConnectionEstablished(EventArgs.Empty);
		///<summary>Raises the ConnectionEstablished event.</summary>
		///<param name="e">An EventArgs object that provides the event data.</param>
		protected internal virtual void OnConnectionEstablished(EventArgs e) => ConnectionEstablished?.Invoke(this, e);
		#endregion

		static TaskCompletionSource<T> CompletedSource<T>(T value) {
			var source = new TaskCompletionSource<T>();
			source.SetResult(value);
			return source;
		}

		protected virtual void Dispose(bool disposing) {
			if (disposing) {
				volatileDeviceSource.Task.ContinueWith(
					td => td.Result.Dispose(),
					TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously
				);
			}
		}
		public void Dispose() { Dispose(true); }
	}
	class ConnectionErrorEventArgs : EventArgs {
		public ConnectionErrorEventArgs(IDeviceConnection connection, Exception error) {
			DisposedConnection = connection;
			Error = error;
		}
		public Exception Error { get; }
		///<summary>Gets the connection that caused the error.</summary>
		public IDeviceConnection DisposedConnection { get; }
	}
}
