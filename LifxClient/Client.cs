using LifxClient.Enums;
using System;
using System.Collections.Generic;
using System.IO;
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
				discoveryTimer.Interval = value;
			}
		}
		
		/// <summary> Fired when a new device is discovered. </summary>
		public event EventHandler<DeviceEventArgs> DeviceDiscovered;
		/// <summary> Fired when a device is lost. </summary>
		public event EventHandler<DeviceEventArgs> DeviceLost;
		
		private readonly UdpClient sock;
		private readonly Dictionary<RequestId, Frame> responses;
		private readonly Dictionary<ulong, Device> devices;
		private readonly Timer discoveryTimer;
		private ushort _discoveryFrequency = 10000;
		private uint sourceId = 0;
		private byte seq = 0;

		private const ushort BROADCAST_PORT = 56700;

		public Client() {
			sock = new UdpClient(0) {
				EnableBroadcast = true
			};
			Debug.WriteLine("Client UDP socket listening on port " + ((IPEndPoint) sock.Client.LocalEndPoint).Port);

			responses = new Dictionary<RequestId, Frame>();
			devices = new Dictionary<ulong, Device>();
			
			discoveryTimer = new Timer(DiscoveryFrequency);
			discoveryTimer.Elapsed += (object source, ElapsedEventArgs e) => { discoverDevices(); };
			discoveryTimer.AutoReset = true;
			discoveryTimer.Enabled = false;
			
			// Initialize with a random source ID
			sourceId = (uint) new Random().Next(0, int.MaxValue);
			
			receiveUdpPacket();
		}

		/// <summary> Start auto-discovery of LIFX devices. </summary>
		public void StartDiscovery() {
			discoveryTimer.Enabled = true;
			discoverDevices();
		}

		/// <summary> Stop auto-discovery of LIFX devices. </summary>
		public void StopDiscovery() {
			discoveryTimer.Enabled = false;
		}

		/// <summary> Get all known devices. </summary>
		/// <returns>List<Device></returns>
		public List<Device> GetKnownDevices() {
			var output = new List<Device>();
			foreach (Device device in devices.Values) {
				output.Add(device);
			}

			return output;
		}

		/// <summary> Get a device by its address. </summary>
		/// <param name="address">The device's address</param>
		/// <returns>Device, or null if not known</returns>
		public Device GetDeviceByAddress(ulong address) {
			Device output;
			if (!devices.TryGetValue(address, out output)) {
				return null;
			}

			return output;
		}

		private void discoverDevices() {
			var allDevices = devices.Values;
			foreach (Device dev in allDevices) {
				dev.CheckedIn = false;
			}
			
			foreach (IPAddress broadcastAddress in Helpers.GetBroadcastAddresses()) {
				sendPacket(new IPEndPoint(broadcastAddress, BROADCAST_PORT), new Frame {
					Type = MessageType.GetService,
					Payload = new byte[] { }
				});
			}

			Task.Run(async () => {
				await Task.Delay(2000);
				foreach (Device dev in allDevices) {
					if (!dev.CheckedIn && ++dev.MissedCheckins >= 5) {
						removeFailedDevice(dev);
					}
				}
			});
		}

		internal async Task<Frame> sendPacketWithResponse(IPEndPoint address, Frame frame) {
			sendPacket(address, frame);
			RequestId reqId = new RequestId {
				SourceID = frame.Source,
				Sequence = frame.Sequence,
			};

			var attempts = 0;
			while (!responses.ContainsKey(reqId) && ++attempts < 100) {
				await Task.Delay(100);				
			}

			Frame respFrame;
			if (!responses.TryGetValue(reqId, out respFrame)) {
				throw new Exception("Timed out waiting for response");
			}

			responses.Remove(reqId); // clean up after ourselves
			return respFrame;
		}

		internal void sendPacket(IPEndPoint address, Frame frame) {
			//Debug.WriteLine("Sending packet of " + frame.Size + " bytes to " + address.Address + ":" + address.Port);
			frame.Source = sourceId++;
			frame.Sequence = seq++;
			sock.SendAsync(frame.Serialize(), frame.Size, address);
		}

		private async void receiveUdpPacket() {
			var data = await sock.ReceiveAsync();
			//Debug.WriteLine("Received UDP packet of length " + data.Buffer.Length + " from " + data.RemoteEndPoint.Address + ":" + data.RemoteEndPoint.Port);
			
			try {
				var frame = Frame.Unserialize(data.Buffer);
				Debug.WriteLine("Got packet from " + data.RemoteEndPoint.Address + " with Type = " + frame.Type + "; Source = " + frame.Source + "; Sequence = " + frame.Sequence);

				RequestId reqId = new RequestId {
					SourceID = frame.Source,
					Sequence = frame.Sequence,
				};

				if (!responses.ContainsKey(reqId)) {
					responses.Add(reqId, frame);
#pragma warning disable 4014
					Task.Run(async () => {
#pragma warning restore 4014
						await Task.Delay(10000);
						if (responses.ContainsKey(reqId)) {
							responses.Remove(reqId);
						}
					});
				}

				handleFrame(frame, data.RemoteEndPoint);
			}
			catch (Exception ex) {
				Debug.WriteLine("Malformed packet: " + ex.Message);
			}
			
			receiveUdpPacket();
		}

		private void handleFrame(Frame frame, IPEndPoint remote) {
			var stream = new MemoryStream(frame.Payload);
			var reader = new BinaryReader(stream);
			
			switch (frame.Type) {
				case MessageType.StateService:
					var service = reader.ReadByte();
					var port = reader.ReadUInt32();
					//Debug.WriteLine("Got service " + service + " on port " + port + " from " + remote + " address " + frame.Target.ToString("X"));
					if (service == 1) {
						// UDP
						Device device;
						if (devices.TryGetValue(frame.Target, out device)) {
							//Debug.WriteLine("Address " + frame.Target.ToString("X") + " is already known; not querying");
							device.CheckedIn = true;
							
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
							
							devices.Add(frame.Target, device);
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

		private void removeFailedDevice(Device dev) {
			if (devices.ContainsKey(dev.Address)) {
				devices.Remove(dev.Address);
			}

			var handler = DeviceLost;
			if (handler != null) {
				handler(this, new DeviceEventArgs(dev));
			}
		}
	}

	internal class RequestId : IEquatable<RequestId>
	{
		public uint SourceID { get; set; }
		public byte Sequence { get; set; }

		public bool Equals(RequestId other) {
			if (ReferenceEquals(null, other)) return false;
			if (ReferenceEquals(this, other)) return true;
			return SourceID == other.SourceID && Sequence == other.Sequence;
		}

		public override bool Equals(object obj) {
			if (ReferenceEquals(null, obj)) return false;
			if (ReferenceEquals(this, obj)) return true;
			if (obj.GetType() != this.GetType()) return false;
			return Equals((RequestId) obj);
		}

		public override int GetHashCode() {
			unchecked {
				return ((int) SourceID * 397) ^ Sequence.GetHashCode();
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
