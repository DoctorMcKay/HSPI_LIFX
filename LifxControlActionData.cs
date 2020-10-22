using System;
using System.IO;

namespace HSPI_LIFX
{
	public class LifxControlActionData
	{
		public const uint FLAG_OVERRIDE_TRANSITION_TIME = 1 << 0;
		
		public const int ACTION_UNSELECTED = 0;
		public const int ACTION_SET_COLOR = 1;
		public const int ACTION_SET_COLOR_AND_BRIGHTNESS = 2;
		public const int ACTION_RECALL_MZ_THEME = 3;
		public const int ACTION_SET_TRANSITION_TIME = 10;
		
		public uint Flags { get; set; }
		public int DevRef { get; set; }
		public string Color { get; set; }
		public byte BrightnessPercent { get; set; }
		public uint TransitionTimeMilliseconds { get; set; }

		public LifxControlActionData() {
			Flags = 0;
			DevRef = 0;
			Color = "";
			BrightnessPercent = 255; // 255 = unset
			TransitionTimeMilliseconds = 0;
		}

		public bool HasFlag(uint flag) {
			return (Flags & flag) == flag;
		}

		public byte[] Serialize() {
			var stream = new MemoryStream();
			var writer = new BinaryWriter(stream);

			writer.Write((byte) 1); // data version]
			writer.Write(Flags);
			writer.Write(DevRef);
			writer.Write(Color);
			writer.Write(BrightnessPercent);
			writer.Write(TransitionTimeMilliseconds);

			byte[] output = stream.ToArray();
			
			writer.Dispose();
			stream.Dispose();

			return output;
		}

		public static LifxControlActionData Unserialize(byte[] value) {
			LifxControlActionData output = new LifxControlActionData();
			if (value == null || value.Length == 0) {
				return output;
			}
			
			var stream = new MemoryStream(value);
			var reader = new BinaryReader(stream);

			byte dataVersion = reader.ReadByte();
			switch (dataVersion) {
				case 1:
					output.Flags = reader.ReadUInt32();
					output.DevRef = reader.ReadInt32();
					output.Color = reader.ReadString();
					output.BrightnessPercent = reader.ReadByte();
					output.TransitionTimeMilliseconds = reader.ReadUInt32();
					break;
				
				default:
					throw new Exception("Unknown data version " + dataVersion);
			}
			
			reader.Dispose();
			stream.Dispose();

			return output;
		}

		public static bool IsValidTimeSpan(string timeSpan) {
			return timeSpan.Replace('.', ':').Split(':').Length == 4;
		}

		public static uint DecodeTimeSpan(string timeSpan) {
			string[] parts = timeSpan.Replace('.', ':').Split(':');
			uint days = uint.Parse(parts[0]);
			uint hours = uint.Parse(parts[1]);
			uint minutes = uint.Parse(parts[2]);
			uint seconds = uint.Parse(parts[3]);

			seconds += minutes * 60;
			seconds += hours * 60 * 60;
			seconds += days * 60 * 60 * 24;
			return seconds * 1000;
		}
	}
}
