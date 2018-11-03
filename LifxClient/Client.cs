using LifxClient.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace LifxClient
{
	public class Client
	{
		public event EventHandler<DeviceEventArgs> DeviceDiscovered;
		public event EventHandler<DeviceEventArgs> DeviceLost;
		
		private readonly UdpClient sock;
		private readonly Dictionary<RequestId, Frame> responses;
		private readonly Dictionary<ulong, Device> devices;
		private readonly Dictionary<ulong, byte> countDevicesNotSeen;
		private uint sourceId = 0;
		private byte seq = 0;

		private const ushort BROADCAST_PORT = 56700;

		public Client() {
			sock = new UdpClient(0) {
				EnableBroadcast = true
			};
			Debug.WriteLine("LifxClient UDP socket listening on port " + ((IPEndPoint) sock.Client.LocalEndPoint).Port);

			responses = new Dictionary<RequestId, Frame>();
			devices = new Dictionary<ulong, Device>();
			countDevicesNotSeen = new Dictionary<ulong, byte>();
			
			// Initialize with a random source ID
			sourceId = (uint) new Random().Next(0, int.MaxValue);
			
			receiveUdpPacket();
		}

		public void DiscoverDevices() {
			foreach (IPAddress broadcastAddress in Helpers.GetBroadcastAddresses()) {
				// TODO make this automatic, and handle missing devices
				sendPacket(new IPEndPoint(broadcastAddress, BROADCAST_PORT), new Frame {
					Type = MessageType.GetService,
					Payload = new byte[] { }
				});
			}
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
			Debug.WriteLine("Sending packet of " + frame.Size + " bytes to " + address.Address + ":" + address.Port);
			frame.Source = ++sourceId;
			frame.Sequence = ++seq;
			sock.SendAsync(frame.Serialize(), frame.Size, address);
		}

		private async void receiveUdpPacket() {
			var data = await sock.ReceiveAsync();
			Debug.WriteLine("Received UDP packet of length " + data.Buffer.Length + " from " + data.RemoteEndPoint.Address + ":" + data.RemoteEndPoint.Port);
			
			try {
				var frame = Frame.Unserialize(data.Buffer);
				Debug.WriteLine("Got packet with Type = " + frame.Type + "; Source = " + frame.Source + "; Sequence = " + frame.Sequence);

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
					//Debug.WriteLine("Unhandled frame " + frame.Type);
					break;
			}
			
			reader.Dispose();
			stream.Dispose();
		}
	}

	internal class RequestId : IEquatable<RequestId>
	{
		public uint SourceID { get; set; }
		public byte Sequence { get; set; }

		public override int GetHashCode() {
			byte hi = (byte) ((SourceID >> 24) & 0xff);
			byte medHi = (byte) ((SourceID >> 16) & 0xff);
			byte medLo = (byte) ((SourceID >> 8) & 0xff);
			byte lo = (byte) (SourceID & 0xff);

			hi ^= Sequence;
			medHi ^= Sequence;
			medLo ^= Sequence;
			lo ^= Sequence;

			return (hi << 24) |
			       (medHi << 16) |
			       (medLo << 8) |
			       lo;
		}

		public override bool Equals(object other) {
			return Equals(other as RequestId);
		}

		public bool Equals(RequestId other) {
			return SourceID == other.SourceID && Sequence == other.Sequence;
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
