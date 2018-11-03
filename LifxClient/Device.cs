using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using LifxClient.Enums;

namespace LifxClient
{
	public class Device
	{
		public ulong Address { get; set; }
		public IPEndPoint IPAddress { get; set; }
		public LightStatus LastKnownStatus { get; private set; }

		private readonly Client client;

		internal Device(Client client) {
			this.client = client;
		}

		public async Task<DeviceVersion> GetVersion() {
			var resp = await client.sendPacketWithResponse(IPAddress, new Frame {
				Payload = new byte[] { },
				Target = Address,
				Type = MessageType.GetVersion
			});

			if (resp.Type != MessageType.StateVersion) {
				Debug.WriteLine("Unexpected response type " + resp.Type + " to GetVersion");
				return null;
			}
			
			var stream = new MemoryStream(resp.Payload);
			var reader = new BinaryReader(stream);

			var version = new DeviceVersion {
				Vendor = reader.ReadUInt32(),
				Product = reader.ReadUInt32(),
				Version = reader.ReadUInt32()
			};
			
			reader.Dispose();
			stream.Dispose();
			return version;
		}

		/// <summary> Query this device's light status. </summary>
		/// <returns>LightStatus</returns>
		public async Task<LightStatus> QueryLightStatus() {
			var resp = await client.sendPacketWithResponse(IPAddress, new Frame {
				Payload = new byte[] { },
				Target = Address,
				Type = MessageType.Light_Get
			});

			if (resp.Type != MessageType.Light_State) {
				Debug.WriteLine("Unexpected response type " + resp.Type + " to Light_Get");
				return null;
			}
			
			return LastKnownStatus = decodeStateFrame(resp);
		}
		
#region Power control

		private Frame buildSetPoweredFrame(bool powered, uint duration) {
			var stream = new MemoryStream();
			var writer = new BinaryWriter(stream);
			
			writer.Write((ushort) (powered ? ushort.MaxValue : 0));
			writer.Write(duration);

			Frame frame = new Frame {
				Target = Address,
				Type = MessageType.Light_SetPower,
				ResponseRequired = true,
				Payload = stream.ToArray()
			};
			
			writer.Dispose();
			stream.Dispose();

			LastKnownStatus.Powered = powered;

			return frame;
		}

		/// <summary> Turn this device on or off. </summary>
		/// <param name="powered">True to turn on, false to turn off</param>
		/// <param name="duration">Time in ms for the transition</param>
		public void SetPowered(bool powered, uint duration) {
			client.sendPacket(IPAddress, buildSetPoweredFrame(powered, duration));
		}

		/// <summary> Turn this device on or off. The async method returns when the LIFX device acknowledges the request. </summary>
		/// <param name="powered">True to turn on, false to turn off</param>
		/// <param name="duration">Time in ms for the transition</param>
		public async Task SetPoweredWithAck(bool powered, uint duration) {
			Frame frame = buildSetPoweredFrame(powered, duration);
			frame.AckRequired = true;
			await client.sendPacketWithResponse(IPAddress, frame);
		}
		
#endregion
		
#region Color control

		private Frame buildSetColorFrame(ushort hue, ushort saturation, ushort brightness, ushort kelvin,
			uint duration) {
			var stream = new MemoryStream();
			var writer = new BinaryWriter(stream);

			writer.Write((byte) 0); // reserved
			writer.Write(hue);
			writer.Write(saturation);
			writer.Write(brightness);
			writer.Write(kelvin);
			writer.Write(duration);

			Frame frame = new Frame {
				Target = Address,
				Type = MessageType.Light_SetColor,
				Payload = stream.ToArray()
			};

			writer.Dispose();
			stream.Dispose();

			LastKnownStatus.Hue = hue;
			LastKnownStatus.Saturation = saturation;
			LastKnownStatus.Brightness = brightness;
			LastKnownStatus.Kelvin = kelvin;

			return frame;
		}

		/// <summary> Set this device's color and brightness. </summary>
		/// <param name="hue">The device's desired new hue, 0-65535</param>
		/// <param name="saturation">The device's desired new saturation, 0-65535</param>
		/// <param name="brightness">The device's desired new brightness, 0-65535</param>
		/// <param name="kelvin">Warmness, 2500 (warm) - 9000 (cool)</param>
		/// <param name="duration">Time in ms for the transition</param>
		public void SetColor(ushort hue, ushort saturation, ushort brightness, ushort kelvin, uint duration) {
			client.sendPacket(IPAddress, buildSetColorFrame(hue, saturation, brightness, kelvin, duration));
		}

		/// <summary> Set this device's color and brightness. The async method returns when the LIFX device acknowledges the request. </summary>
		/// <param name="hue">The device's desired new hue, 0-65535</param>
		/// <param name="saturation">The device's desired new saturation, 0-65535</param>
		/// <param name="brightness">The device's desired new brightness, 0-65535</param>
		/// <param name="kelvin">Warmness, 2500 (warm) - 9000 (cool)</param>
		/// <param name="duration">Time in ms for the transition</param>
		public async Task SetColorWithAck(ushort hue, ushort saturation, ushort brightness, ushort kelvin, uint duration) {
			Frame frame = buildSetColorFrame(hue, saturation, brightness, kelvin, duration);
			frame.AckRequired = true;
			await client.sendPacketWithResponse(IPAddress, frame);
		}
		
#endregion

		private LightStatus decodeStateFrame(Frame frame) {
			if (frame.Type != MessageType.Light_State) {
				throw new Exception("Got bad frame type " + frame.Type + " to decodeStateFrame");
			}
			
			var stream = new MemoryStream(frame.Payload);
			var reader = new BinaryReader(stream);

			var hue = reader.ReadUInt16();
			var sat = reader.ReadUInt16();
			var brightness = reader.ReadUInt16();
			var kelvin = reader.ReadUInt16();
			reader.ReadUInt16(); // reserved
			var power = reader.ReadUInt16();
			var label = System.Text.Encoding.Default.GetString(reader.ReadBytes(32)).TrimEnd(new char[] { (char) 0 });

			reader.Dispose();
			stream.Dispose();
			
			return new LightStatus {
				Hue = hue, 
				Saturation = sat,
				Brightness = brightness,
				Kelvin = kelvin,
				Powered = power > 0,
				Label = label,
			};
		}
	}

	public class DeviceVersion
	{
		public uint Vendor { get; internal set; }
		public uint Product { get; internal set; }
		public uint Version { get; internal set; }

		public override string ToString() {
			return "Vendor = " + Vendor + "; Product = " + Product + "; Version = " + Version;
		}
	}

	public class LightStatus
	{
		public ushort Hue { get; internal set; }
		public ushort Saturation { get; internal set; }
		public ushort Brightness { get; internal set; }
		public ushort Kelvin { get; internal set; }
		public bool Powered { get; internal set; }
		public string Label { get; internal set; }

		public override string ToString() {
			return "Hue = " + Hue + "; Saturation = " + Saturation + "; Brightness = " + Brightness + "; Kelvin = " +
			       Kelvin + "; Powered = " + Powered + "; Label = " + Label;
		}
	}
}