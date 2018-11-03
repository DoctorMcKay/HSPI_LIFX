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
		public event EventHandler<DeviceDiscoveredEventArgs> DeviceDiscovered;
		
		private UdpClient sock;
		private Dictionary<uint, Frame> responses;
		private uint sourceId = 0;
		private byte seq = 0;

		public Client() {
			sock = new UdpClient(0) {
				EnableBroadcast = true
			};
			Debug.WriteLine("LifxClient UDP socket listening on port " + ((IPEndPoint) sock.Client.LocalEndPoint).Port);

			responses = new Dictionary<uint, Frame>();
			
			receiveUdpPacket();
		}

		public void DiscoverDevices() {
			// TODO go adapter by adapter and send a broadcast to each
			sendPacket(new IPEndPoint(new IPAddress(new byte[] { 192, 168, 1, 255 }), 56700), new Frame {
				Tagged = true,
				Type = MessageType.GetService,
				Payload = new byte[] {}
			});
		}

		private async Task<Frame> sendPacketWithResponse(IPEndPoint address, Frame frame) {
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

		private void sendPacket(IPEndPoint address, Frame frame) {
			Debug.WriteLine("Sending packet of " + frame.Size + " bytes to " + address.Address.ToString() + ":" + address.Port);
			frame.Source = ++sourceId;
			frame.Sequence = ++seq;
			sock.SendAsync(frame.Serialize(), frame.Size, address);
		}

		private async void receiveUdpPacket() {
			var data = await sock.ReceiveAsync();
			Debug.WriteLine("Received UDP packet of length " + data.Buffer.Length + " from " + data.RemoteEndPoint.Address.ToString() + ":" + data.RemoteEndPoint.Port);

			/*var hex = new StringBuilder(data.Buffer.Length * 2);
			foreach (byte b in data.Buffer) {
				hex.AppendFormat("{0:x2}", b);
			}
			
			Debug.WriteLine(hex.ToString());*/
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
					Debug.WriteLine("Got service " + service + " on port " + port + " from " + remote + " address " + frame.Target);
					if (service == 1) {
						// UDP
						queryLight(remote, frame.Target);
					}
					break;
				
				default:
					Debug.WriteLine("Unhandled frame " + frame.Type);
					break;
			}
			
			reader.Dispose();
			stream.Dispose();
		}

		private async void queryLight(IPEndPoint remote, ulong target) {
			var resp = await sendPacketWithResponse(remote, new Frame {
				Payload = new byte[] { },
				Target = target,
				Type = MessageType.Light_Get
			});

			if (resp.Type != MessageType.Light_State) {
				Debug.WriteLine("Unexpected response type " + resp.Type + " to Light_Get");
				return;
			}
			
			var stream = new MemoryStream(resp.Payload);
			var reader = new BinaryReader(stream);

			var hue = reader.ReadUInt16();
			var sat = reader.ReadUInt16();
			var brightness = reader.ReadUInt16();
			var kelvin = reader.ReadUInt16();
			reader.ReadUInt16(); // reserved
			var power = reader.ReadUInt16();
			var label = System.Text.Encoding.Default.GetString(reader.ReadBytes(32));
			
			Debug.WriteLine("Got light data hue = " + hue + ", sat = " + sat + ", bright = " + brightness + ", kel = " + kelvin + ", power = " + power + ", label = " + label);
		}
	}
	
	public class DeviceDiscoveredEventArgs : EventArgs
	{
		public Device Device { get; private set; }

		public DeviceDiscoveredEventArgs(Device dev) {
			Device = dev;
		}
	}
}
