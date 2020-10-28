using LifxClient.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Timers;

namespace LifxClient
{
	public class Client
	{
		/// <summary> How frequently should we try to discover devices? In milliseconds. </summary>
		public ushort DiscoveryFrequency {
			get { return _discoveryFrequency; }
			set {
				if (value < 2500) {
					throw new Exception("Cannot set DiscoveryFrequency less than 2500 ms");
				}
				
				_discoveryFrequency = value;
				_discoveryTimer.Interval = value;
			}
		}
		
		/// <summary> Fired when a new device is discovered. </summary>
		public event EventHandler<DeviceEventArgs> DeviceDiscovered;
		/// <summary> Fired when a device is lost. </summary>
		public event EventHandler<DeviceEventArgs> DeviceLost;
		
		private readonly UdpClient _sock;
		private readonly Dictionary<RequestId, Frame> _responses;
		private readonly Dictionary<ulong, Device> _devices;
		private readonly Timer _discoveryTimer;
		private ushort _discoveryFrequency = 10000;
		private uint _sourceId = 0;
		private byte _seq = 0;

		private const ushort BroadcastPort = 56700;

		public Client() {
			_sock = new UdpClient(0) {
				EnableBroadcast = true
			};
			Debug.WriteLine("Client UDP socket listening on port " + ((IPEndPoint) _sock.Client.LocalEndPoint).Port);

			_responses = new Dictionary<RequestId, Frame>();
			_devices = new Dictionary<ulong, Device>();
			
			_discoveryTimer = new Timer(DiscoveryFrequency);
			_discoveryTimer.Elapsed += (object source, ElapsedEventArgs e) => { _discoverDevices(); };
			_discoveryTimer.AutoReset = true;
			_discoveryTimer.Enabled = false;
			
			// Initialize with a random source ID
			_sourceId = (uint) new Random().Next(0, int.MaxValue);
			
			_receiveUdpPacket();
		}

		/// <summary> Start auto-discovery of LIFX devices. </summary>
		public void StartDiscovery() {
			_discoveryTimer.Enabled = true;
			_discoverDevices();
		}

		/// <summary> Stop auto-discovery of LIFX devices. </summary>
		public void StopDiscovery() {
			_discoveryTimer.Enabled = false;
		}

		/// <summary> Get all known devices. </summary>
		/// <returns>List<Device></returns>
		public List<Device> GetKnownDevices() {
			return _devices.Values.ToList();
		}

		/// <summary> Get a device by its address. </summary>
		/// <param name="address">The device's address</param>
		/// <returns>Device, or null if not known</returns>
		public Device GetDeviceByAddress(ulong address) {
			Device output;
			if (!_devices.TryGetValue(address, out output)) {
				return null;
			}

			return output;
		}

		private void _discoverDevices() {
			Debug.WriteLine("Resetting device checkins");
			var allDevices = _devices.Values;
			foreach (Device dev in allDevices) {
				dev.CheckedIn = false;
			}
			
			foreach (IPAddress broadcastAddress in Helpers.GetBroadcastAddresses()) {
				_sendPacket(new IPEndPoint(broadcastAddress, BroadcastPort), new Frame {
					Type = MessageType.GetService,
					Payload = new byte[] { }
				});
			}

			Task.Run(async () => {
				await Task.Delay(2000);
				foreach (Device dev in allDevices) {
					Debug.WriteLine($"Device {dev.Address:X} checked in: {dev.CheckedIn}; missed checkins: {dev.MissedCheckins}");
					if (!dev.CheckedIn && ++dev.MissedCheckins >= 5) {
						_removeFailedDevice(dev);
					}
				}
			});
		}

		internal async Task<Frame> _sendPacketWithRetry(IPEndPoint address, Frame frame, byte retryCount = 0) {
			if (!frame.AckRequired && !frame.ResponseRequired) {
				frame.AckRequired = true;
			}

			try {
				return await _sendPacketWithResponse(address, frame);
			} catch (Exception ex) {
				if (retryCount >= 4) {
					throw ex;
				}

				return await _sendPacketWithRetry(address, frame, (byte) (retryCount + 1));
			}
		}

		internal async Task<Frame> _sendPacketWithResponse(IPEndPoint address, Frame frame) {
			_sendPacket(address, frame);
			RequestId reqId = new RequestId {
				SourceId = frame.Source,
				Sequence = frame.Sequence,
			};

			var attempts = 0;
			while (!_responses.ContainsKey(reqId) && ++attempts < 50) {
				await Task.Delay(100);				
			}
			
			if (!_responses.TryGetValue(reqId, out Frame respFrame)) {
				throw new Exception("Timed out waiting for response");
			}

			_responses.Remove(reqId); // clean up after ourselves
			return respFrame;
		}

		internal void _sendPacket(IPEndPoint address, Frame frame) {
			//Debug.WriteLine("Sending packet of " + frame.Size + " bytes to " + address.Address + ":" + address.Port);
			frame.Source = _sourceId++;
			frame.Sequence = _seq++;
			_sock.SendAsync(frame.Serialize(), frame.Size, address);
		}

		private async void _receiveUdpPacket() {
			var data = await _sock.ReceiveAsync();
			//Debug.WriteLine("Received UDP packet of length " + data.Buffer.Length + " from " + data.RemoteEndPoint.Address + ":" + data.RemoteEndPoint.Port);
			
			try {
				var frame = Frame.Unserialize(data.Buffer);
				Debug.WriteLine("Got packet from " + data.RemoteEndPoint.Address + " with Type = " + frame.Type + "; Source = " + frame.Source + "; Sequence = " + frame.Sequence);

				RequestId reqId = new RequestId {
					SourceId = frame.Source,
					Sequence = frame.Sequence,
				};

				if (!_responses.ContainsKey(reqId)) {
					_responses.Add(reqId, frame);
#pragma warning disable 4014
					Task.Run(async () => {
#pragma warning restore 4014
						await Task.Delay(10000);
						if (_responses.ContainsKey(reqId)) {
							_responses.Remove(reqId);
						}
					});
				}

				_handleFrame(frame, data.RemoteEndPoint);
			}
			catch (Exception ex) {
				Debug.WriteLine("Malformed packet from " + data.RemoteEndPoint.Address + ":" + data.RemoteEndPoint.Port + ": " + ex.Message + "\n" + ex.StackTrace);
			}
			
			_receiveUdpPacket();
		}

		private void _handleFrame(Frame frame, IPEndPoint remote) {
			var stream = new MemoryStream(frame.Payload);
			var reader = new BinaryReader(stream);
			
			switch (frame.Type) {
				case MessageType.StateService:
					var service = reader.ReadByte();
					var port = reader.ReadUInt32();
					Debug.WriteLine($"Got service {service} on port {port} from {remote} address {frame.Target:X}");
					if (service == 1) {
						// UDP
						if (_devices.TryGetValue(frame.Target, out Device device)) {
							//Debug.WriteLine("Address " + frame.Target.ToString("X") + " is already known; not querying");
							device.CheckedIn = true;
							device.MissedCheckins = 0;
							
							// Make sure the IP address hasn't changed
							if (!device.IPAddress.Equals(remote)) {
								device.IPAddress = remote;
							}
							
							break;
						}
						
						device = new Device(this) {
							Address = frame.Target,
							IPAddress = remote,
							CheckedIn = true,
						};
						
						device.QueryLightStatus().ContinueWith((Task<LightStatus> task) => {
							if (task.Result == null) {
								Debug.WriteLine("Unable to get light status for service " + service + " on port " + port + " from " + remote);
								return;
							}
							
							Debug.WriteLine("Got light status for " + device.Address.ToString("X"));
							
							_devices.Add(frame.Target, device);
							var handler = DeviceDiscovered;
							if (handler != null) {
								handler(this, new DeviceEventArgs(device));
							}
						});
					}
					break;
				
				default:
					//Debug.WriteLine("Unhandled frame " + frame.Type);
					break;
			}
			
			reader.Dispose();
			stream.Dispose();
		}

		private void _removeFailedDevice(Device dev) {
			if (_devices.ContainsKey(dev.Address)) {
				_devices.Remove(dev.Address);
			}

			var handler = DeviceLost;
			handler?.Invoke(this, new DeviceEventArgs(dev));
		}
	}

	internal class RequestId : IEquatable<RequestId>
	{
		public uint SourceId { get; set; }
		public byte Sequence { get; set; }

		public bool Equals(RequestId other) {
			if (ReferenceEquals(null, other)) return false;
			if (ReferenceEquals(this, other)) return true;
			return SourceId == other.SourceId && Sequence == other.Sequence;
		}

		public override bool Equals(object obj) {
			if (ReferenceEquals(null, obj)) return false;
			if (ReferenceEquals(this, obj)) return true;
			if (obj.GetType() != this.GetType()) return false;
			return Equals((RequestId) obj);
		}

		public override int GetHashCode() {
			unchecked {
				return ((int) SourceId * 397) ^ Sequence.GetHashCode();
			}
		}

		public static bool operator ==(RequestId left, RequestId right) {
			return Equals(left, right);
		}

		public static bool operator !=(RequestId left, RequestId right) {
			return !Equals(left, right);
		}
	}
	
	public class DeviceEventArgs : EventArgs
	{
		public Device Device { get; private set; }

		internal DeviceEventArgs(Device dev) {
			Device = dev;
		}
	}
}
