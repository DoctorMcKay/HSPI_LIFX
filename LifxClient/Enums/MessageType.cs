using System.Diagnostics.CodeAnalysis;

namespace LifxClient.Enums
{
	[SuppressMessage("ReSharper", "InconsistentNaming")]
	public enum MessageType
	{
		GetService = 2,
		StateService = 3,
		GetHostInfo = 12,
		StateHostInfo = 13,
		GetHostFirmware = 14,
		StateHostFirmware = 15,
		GetWifiInfo = 16,
		StateWifiInfo = 17,
		GetWifiFirmware = 18,
		StateWifiFirmware = 19,
		GetPower = 20,
		SetPower = 21,
		StatePower = 22,
		GetLabel = 23,
		SetLabel = 24,
		StateLabel = 25,
		GetVersion = 32,
		StateVersion = 33,
		GetInfo = 34,
		StateInfo = 35,
		Acknowledgement = 45,
		GetLocation = 48,
		SetLocation = 49,
		StateLocation = 50,
		GetGroup = 51,
		SetGroup = 52,
		StateGroup = 53,
		EchoRequest = 58,
		EchoResponse = 59,
		
		Light_Get = 101,
		Light_SetColor = 102,
		Light_SetWaveform = 103,
		Light_SetWaveformOptional = 119,
		Light_State = 107,
		Light_GetPower = 116,
		Light_SetPower = 117,
		Light_StatePower = 118,
		Light_GetInfrared = 120,
		Light_StateInfrared = 121,
		Light_SetInfrared = 122,
		
		SetExtendedColorZones = 510,
		GetExtendedColorZones = 511,
		StateExtendedColorZones = 512
	}
}
