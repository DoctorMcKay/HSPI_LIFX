using System;
using System.IO;

namespace HSPI_LIFX
{
	public class LifxControlActionData
	{
		public const int ACTION_UNSELECTED = 0;
		public const int ACTION_SET_COLOR = 1;
		public const int ACTION_SET_TRANSITION_TIME = 2;
		
		public int DevRef { get; set; }
		public string StringValue { get; set; }

		public LifxControlActionData() {
			DevRef = 0;
			StringValue = "";
		}

		public byte[] Serialize() {
			var stream = new MemoryStream();
			var writer = new BinaryWriter(stream);

			writer.Write((byte) 1); // data version
			writer.Write(DevRef);
			writer.Write(StringValue);

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
					output.DevRef = reader.ReadInt32();
					output.StringValue = reader.ReadString();
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

		public static int DecodeTimeSpan(string timeSpan) {
			string[] parts = timeSpan.Replace('.', ':').Split(':');
			int days = int.Parse(parts[0]);
			int hours = int.Parse(parts[1]);
			int minutes = int.Parse(parts[2]);
			int seconds = int.Parse(parts[3]);

			seconds += minutes * 60;
			seconds += hours * 60 * 60;
			seconds += days * 60 * 60 * 24;
			return seconds;
		}
	}
}
