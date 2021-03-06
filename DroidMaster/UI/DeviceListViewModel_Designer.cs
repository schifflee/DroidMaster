﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DroidMaster.Core;
using DroidMaster.Models;

namespace DroidMaster.UI {
	partial class DeviceListViewModel {
		[Obsolete("This constructor should only be called by the designer.", error: true)]
		public DeviceListViewModel() {
			ActiveDevices.Add(new DeviceViewModel(new PersistentDevice("A", new DesignerDevice(new AdbDeviceScanner(), "AC123DEADB33F"))));
			ActiveDevices.Add(new DeviceViewModel(new PersistentDevice("B", new DesignerDevice(new SshDeviceScanner(), "192.168.1.217"))));
			ActiveDevices.Add(new DeviceViewModel(new PersistentDevice("C", new DesignerDevice(new AdbDeviceScanner(), "4829AD254FE76"))));
			ActiveDevices.Add(new DeviceViewModel(new PersistentDevice("D", new DesignerDevice(new SshDeviceScanner(), "192.168.1.187"))));
			ActiveDevices.Add(new DeviceViewModel(new PersistentDevice("E", new DesignerDevice(new SshDeviceScanner(), "192.168.1.225"))));

			SetDesignerProperty(0, nameof(DeviceModel.BatteryLevel), 78);
			SetDesignerProperty(1, nameof(DeviceModel.BatteryLevel), 27);
			SetDesignerProperty(2, nameof(DeviceModel.BatteryLevel), 10);
			SetDesignerProperty(3, nameof(DeviceModel.BatteryLevel), 90);

			SetDesignerProperty(0, nameof(DeviceModel.PowerSources), "AC");
			SetDesignerProperty(2, nameof(DeviceModel.PowerSources), "USB");

			SetDesignerProperty(1, nameof(DeviceModel.IsScreenOn), true);
			SetDesignerProperty(2, nameof(DeviceModel.IsScreenOn), true);

			SetDesignerProperty(0, nameof(DeviceModel.IsWiFiEnabled), true);
			SetDesignerProperty(2, nameof(DeviceModel.IsWiFiEnabled), true);

			ActiveDevices[0].ScriptStatus = ScriptStatus.None;
			ActiveDevices[1].ScriptStatus = ScriptStatus.Running;
			ActiveDevices[2].ScriptStatus = ScriptStatus.Success;
			ActiveDevices[3].ScriptStatus = ScriptStatus.Failure;
			ActiveDevices[4].ScriptStatus = ScriptStatus.Cancelled;

			ActiveDevices.Last().Reboot();
		}

		void SetDesignerProperty(int index, string property, object value)
			=> typeof(DeviceModel).GetProperty(property).SetValue(ActiveDevices[index], value);

		class DesignerDevice : IDeviceConnection {
			public DesignerDevice(DeviceScanner type, string id) {
				Owner = type;
				ConnectionId = id;
			}
			public string ConnectionId { get; }

			public DeviceScanner Owner { get; }

			public void Dispose() { }
			public ICommandResult ExecuteShellCommand(string command) { throw new NotImplementedException(); }
			public Task RebootAsync() { throw new SocketException(); }	// Go offline
			public Task PullFileAsync(string devicePath, string localPath, CancellationToken token = default(CancellationToken), IProgress<double> progress = null) {
				throw new NotImplementedException();
			}
			public Task PushFileAsync(string localPath, string devicePath, CancellationToken token = default(CancellationToken), IProgress<double> progress = null) {
				throw new NotImplementedException();
			}
		}
	}
}
