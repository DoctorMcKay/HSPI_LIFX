using LifxClient.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace LifxClient
{
	public class Client
	{
		public event EventHandler<DeviceEventArgs> DeviceDiscovered;
		public event EventHandler<DeviceEventArgs> DeviceLost;
		
		private UdpClient sock;
		private Dictionary<uint, Frame> responses;
		private uint sourceId = 0;
		private byte seq = 0;
		private Dictionary<ulong, Device> devices;
		private Dictionary<ulong, byte> countDevicesNotSeen;
		
		private const ushort BROADCAST_PORT = 56700;

		public Client() {
			sock = new UdpClient(0) {
				EnableBroadcast = true
			};
			Debug.WriteLine("LifxClient UDP socket listening on port " + ((IPEndPoint) sock.Client.LocalEndPoint).Port);

			responses = new Dictionary<uint, Frame>();
			devices = new Dictionary<ulong, Device>();
			countDevicesNotSeen = new Dictionary<ulong, byte>();
			
			receiveUdpPacket();
		}

		public void DiscoverDevices() {
			// TODO go adapter by adapter and send a broadcast to each
			// TODO make this automatic, and handle missing devices
			sendPacket(new IPEndPoint(new IPAddress(new byte[] { 192, 168, 1, 255 }), BROADCAST_PORT), new Frame {
				Type = MessageType.GetService,
				Payload = new byte[] {}
			});
		}

		internal async Task<Frame> sendPacketWithResponse(IPEndPoint address, Frame frame) {
			sendPacket(address, frame);
			var source = frame.Source;

			var attempts = 0;
			while (!responses.ContainsKey(source) && ++attempts < 100) {
				await Task.Delay(100);				
			}

			Frame respFrame;
			if (!responses.TryGetValue(source, out respFrame)) {
				throw new Exception("Timed out waiting for response");
			}

			responses.Remove(source); // clean up after ourselves
			return respFrame;
		}

		internal void sendPacket(IPEndPoint address, Frame frame) {
			Debug.WriteLine("Sending packet of " + frame.Size + " bytes to " + address.Address + ":" + address.Port);
			frame.Source = ++sourceId;
			frame.Sequence = ++seq;
			Debug.WriteLine(Helpers.ByteArrayToHexString(frame.Serialize()));
			sock.SendAsync(frame.Serialize(), frame.Size, address);
		}

		private async void receiveUdpPacket() {
			var data = await sock.ReceiveAsync();
			Debug.WriteLine("Received UDP packet of length " + data.Buffer.Length + " from " + data.RemoteEndPoint.Address + ":" + data.RemoteEndPoint.Port);

			var hex = new StringBuilder(data.Buffer.Length * 2);
			foreach (byte b in data.Buffer) {
				hex.AppendFormat("{0:x2}", b);
			}
			
			Debug.WriteLine(hex.ToString());
			try {
				var frame = Frame.Unserialize(data.Buffer);
				Debug.WriteLine("Got packet type " + frame.Type);

				if (!responses.ContainsKey(frame.Source)) {
					responses.Add(frame.Source, frame);
					Task.Run(async () => {
						await Task.Delay(10000);
						if (responses.ContainsKey(frame.Source)) {
							responses.Remove(frame.Source);
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
					Debug.WriteLine("Got service " + service + " on port " + port + " from " + remote + " address " + frame.Target.ToString("X"));
					if (service == 1) {
						// UDP
						Device device;
						if (devices.TryGetValue(frame.Target, out device)) {
							Debug.WriteLine("Address " + frame.Target + " is already known; not querying");
							// Make sure the IP address hasn't changed
							if (!device.IPAddress.Equals(remote)) {
								device.IPAddress = remote;
							}
							
							break;
						}
						
						device = new Device(this) {
							Address = frame.Target,
							IPAddress = remote,
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
					Debug.WriteLine("Unhandled frame " + frame.Type);
					break;
			}
			
			reader.Dispose();
			stream.Dispose();
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
