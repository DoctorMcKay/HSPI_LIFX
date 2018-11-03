using LifxClient.Enums;
using System;
using System.IO;

namespace LifxClient
{
	public class Frame
	{
		public ushort Size {
			get { return getSize(); }
		}
		
		public bool Tagged { get; set; }
		public uint Source { get; set; }
		public ulong Target { get; set; }
		public bool AckRequired { get; set; }
		public bool ResponseRequired { get; set; }
		public byte Sequence { get; set; }
		public MessageType Type { get; set; }
		public byte[] Payload { get; set; }

		private const ushort HEADER_LENGTH_BYTES = 8 + 16 + 12;

		private ushort getSize() {
			return (ushort) (HEADER_LENGTH_BYTES + Payload.Length);
		}

		public byte[] Serialize() {
			var stream = new MemoryStream();
			var writer = new BinaryWriter(stream);

			// Frame section
			writer.Write(Size);

			writer.Write((ushort) (((Tagged ? 1 : 0) << 13)
			            | (1 << 12) // addressable
			            | 1024)); // protocol
			writer.Write(Source);
			
			// Frame Address section
			writer.Write(Target);
			for (byte i = 0; i < 6; i++) {
				writer.Write((byte) 0); // reserved
			}
			
			writer.Write((byte) (((AckRequired ? 1 : 0) << 1)
			           | (ResponseRequired ? 1 : 0)));
			writer.Write(Sequence);
			
			// Protocol header section
			writer.Write((ulong) 0); // reserved
			writer.Write((ushort) Type);
			writer.Write((ushort) 0); // reserved

			writer.Write(Payload);

			var packet = stream.ToArray();
			writer.Dispose();
			stream.Dispose();

			return packet;
		}

		public static Frame Unserialize(byte[] serialized) {
			var stream = new MemoryStream(serialized);
			var reader = new BinaryReader(stream);

			ushort tempShort = 0;

			// Frame section
			var size = reader.ReadUInt16();
			if (size < HEADER_LENGTH_BYTES) {
				stream.Dispose();
				reader.Dispose();
				throw new Exception("Expected data packet to be at least " + HEADER_LENGTH_BYTES + " bytes, but it's actually " + size + " bytes");
			}

			if (size != serialized.Length) {
				stream.Dispose();
				reader.Dispose();
				throw new Exception("Packet reported size of " + size + "but it's actually " + serialized.Length);
			}
			
			var frame = new Frame();

			tempShort = reader.ReadUInt16();
			frame.Tagged = (tempShort & (1 << 13)) == (1 << 13);
			var protocol = tempShort & 0xfff;
			if (protocol != 1024) {
				stream.Dispose();
				reader.Dispose();
				throw new Exception("Expected protocol to be 1024, but got " + protocol);
			}

			frame.Source = reader.ReadUInt32();
			
			// Frame address section
			frame.Target = reader.ReadUInt64();
			for (byte i = 0; i < 6; i++) {
				reader.ReadByte(); // reserved
			}

			var flags = reader.ReadByte();
			frame.AckRequired = (flags & (1 << 1)) == (1 << 1);
			frame.ResponseRequired = (flags & 1) == 1;
			frame.Sequence = reader.ReadByte();
			
			// Protocol header section
			reader.ReadUInt64(); // reserved
			frame.Type = (MessageType) reader.ReadUInt16();
			reader.ReadUInt16(); // reserved
			
			frame.Payload = reader.ReadBytes(size - HEADER_LENGTH_BYTES);
			
			reader.Dispose();
			stream.Dispose();
			return frame;
		}
	}
}