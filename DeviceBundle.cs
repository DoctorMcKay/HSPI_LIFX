using HomeSeerAPI;
using Scheduler.Classes;

namespace HSPI_LIFX
{
	public class DeviceBundle
	{
		public string Address { get; private set; }
		
		public int Root { get; set; }
		public int Brightness { get; set; }
		public int Color { get; set; }
		public int Temperature { get; set; }

		private readonly HSPI plugin;

		public DeviceBundle(string address, HSPI plugin) {
			this.Address = address;
			this.plugin = plugin;
		}

		public void TryFindChildren() {
			if (Root == 0) {
				return;
			}
			
			IHSApplication hs = plugin.hs;

			DeviceClass root = (DeviceClass) hs.GetDeviceByRef(Root);
			foreach (int childRef in root.get_AssociatedDevices(hs)) {
				DeviceClass child = (DeviceClass) hs.GetDeviceByRef(childRef);
				SubDeviceType subType = (SubDeviceType) int.Parse(child.get_Address(hs).Split('-')[1]);
				switch (subType) {
					case SubDeviceType.Brightness:
						Brightness = childRef;
						break;
					
					case SubDeviceType.Color:
						Color = childRef;
						break;
					
					case SubDeviceType.Temperature:
						Temperature = childRef;
						break;
				}
			}
		}

		public bool IsComplete() {
			return Root != 0 && Brightness != 0 && Color != 0 && Temperature != 0;
		}

		public string GetSubDeviceAddress(SubDeviceType type) {
			if (type == SubDeviceType.Root) {
				return Address;
			}
			
			return Address + '-' + ((int) type).ToString("D2");
		}

		public void CreateDevices(string label) {
			if (IsComplete()) {
				return;
			}
			
			createRoot(label);
			createBrightness(label);
			createColor(label);
			createTemperature(label);
			associateDevices();
		}

		private void createRoot(string label) {
			if (Root != 0) {
				return;
			}

			IHSApplication hs = plugin.hs;

			int hsRef = hs.NewDeviceRef(label);
			DeviceClass device = (DeviceClass) hs.GetDeviceByRef(hsRef);
			device.set_Address(hs, GetSubDeviceAddress(SubDeviceType.Root));
			device.set_Interface(hs, plugin.Name);
			device.set_InterfaceInstance(hs, plugin.InstanceFriendlyName());
			device.set_Device_Type_String(hs, "LIFX Root Device");
			device.set_DeviceType_Set(hs, new DeviceTypeInfo_m.DeviceTypeInfo {
				Device_Type = DeviceTypeInfo_m.DeviceTypeInfo.eDeviceType_GenericRoot
			});
			
			hs.SetDeviceString(hsRef, "No Status", false);

			Root = hsRef;
		}

		private void createBrightness(string label) {
			if (Brightness != 0) {
				return;
			}
			
			IHSApplication hs = plugin.hs;

			int hsRef = hs.NewDeviceRef(label + " Brightness");
			DeviceClass device = (DeviceClass) hs.GetDeviceByRef(hsRef);
			device.set_Address(hs, GetSubDeviceAddress(SubDeviceType.Brightness));
			device.set_Interface(hs, plugin.Name);
			device.set_InterfaceInstance(hs, plugin.InstanceFriendlyName());
			device.set_Device_Type_String(hs, "LIFX Device Brightness");
			device.set_DeviceType_Set(hs, new DeviceTypeInfo_m.DeviceTypeInfo {
				Device_API = DeviceTypeInfo_m.DeviceTypeInfo.eDeviceAPI.Plug_In
			});

			// Create the buttons and slider
			VSVGPairs.VSPair offBtn = new VSVGPairs.VSPair(ePairStatusControl.Both);
			offBtn.PairType = VSVGPairs.VSVGPairType.SingleValue;
			offBtn.Render = Enums.CAPIControlType.Button;
			offBtn.Status = "Off";
			offBtn.ControlUse = ePairControlUse._Off;
			offBtn.Value = 0;
			offBtn.Render_Location = new Enums.CAPIControlLocation {
				Column = 1,
				Row = 1,
			};

			VSVGPairs.VSPair onBtn = new VSVGPairs.VSPair(ePairStatusControl.Both);
			onBtn.PairType = VSVGPairs.VSVGPairType.SingleValue;
			onBtn.Render = Enums.CAPIControlType.Button;
			onBtn.Status = "On";
			onBtn.ControlUse = ePairControlUse._On;
			onBtn.Value = 99;
			onBtn.Render_Location = new Enums.CAPIControlLocation {
				Column = 2,
				Row = 1,
			};
			
			VSVGPairs.VSPair lastBtn = new VSVGPairs.VSPair(ePairStatusControl.Control);
			lastBtn.PairType = VSVGPairs.VSVGPairType.SingleValue;
			lastBtn.Render = Enums.CAPIControlType.Button;
			lastBtn.Status = "Last";
			lastBtn.ControlUse = ePairControlUse._On_Alternate;
			lastBtn.Value = 255;
			lastBtn.Render_Location = new Enums.CAPIControlLocation {
				Column = 4,
				Row = 1,
			};
			
			VSVGPairs.VSPair dim = new VSVGPairs.VSPair(ePairStatusControl.Both);
			dim.PairType = VSVGPairs.VSVGPairType.Range;
			dim.Render = Enums.CAPIControlType.ValuesRangeSlider;
			dim.RangeStart = 1;
			dim.RangeEnd = 98;
			dim.RangeStatusPrefix = "Dim ";
			dim.RangeStatusSuffix = "%";
			dim.ControlUse = ePairControlUse._Dim;
			dim.Render_Location = new Enums.CAPIControlLocation {
				Column = 1,
				Row = 2,
				ColumnSpan = 3,
			};

			hs.DeviceVSP_AddPair(hsRef, offBtn);
			hs.DeviceVSP_AddPair(hsRef, onBtn);
			hs.DeviceVSP_AddPair(hsRef, lastBtn);
			hs.DeviceVSP_AddPair(hsRef, dim);
			
			device.MISC_Set(hs, Enums.dvMISC.SHOW_VALUES);
			device.MISC_Set(hs, Enums.dvMISC.IS_LIGHT);

			hs.SetDeviceValueByRef(hsRef, 0, false);
			
			Brightness = hsRef;
		}

		private void createColor(string label) {
			if (Color != 0) {
				return;
			}
			
			IHSApplication hs = plugin.hs;

			int hsRef = hs.NewDeviceRef(label + " Color");
			DeviceClass device = (DeviceClass) hs.GetDeviceByRef(hsRef);
			device.set_Address(hs, GetSubDeviceAddress(SubDeviceType.Color));
			device.set_Interface(hs, plugin.Name);
			device.set_InterfaceInstance(hs, plugin.InstanceFriendlyName());
			device.set_Device_Type_String(hs, "LIFX Device Color");
			device.set_DeviceType_Set(hs, new DeviceTypeInfo_m.DeviceTypeInfo {
				Device_API = DeviceTypeInfo_m.DeviceTypeInfo.eDeviceAPI.Plug_In
			});

			// Create the buttons and slider
			VSVGPairs.VSPair colorPicker = new VSVGPairs.VSPair(ePairStatusControl.Both);
			colorPicker.PairType = VSVGPairs.VSVGPairType.SingleValue;
			colorPicker.Render = Enums.CAPIControlType.Color_Picker;
			colorPicker.ControlUse = ePairControlUse._ColorControl;

			hs.DeviceVSP_AddPair(hsRef, colorPicker);
			
			device.MISC_Set(hs, Enums.dvMISC.SHOW_VALUES);

			hs.SetDeviceString(hsRef, "ffffff", false);
			
			Color = hsRef;
		}
		
		private void createTemperature(string label) {
			if (Temperature != 0) {
				return;
			}
			
			IHSApplication hs = plugin.hs;

			int hsRef = hs.NewDeviceRef(label + " Color Temperature");
			DeviceClass device = (DeviceClass) hs.GetDeviceByRef(hsRef);
			device.set_Address(hs, GetSubDeviceAddress(SubDeviceType.Temperature));
			device.set_Interface(hs, plugin.Name);
			device.set_InterfaceInstance(hs, plugin.InstanceFriendlyName());
			device.set_Device_Type_String(hs, "LIFX Device Color Temperature");
			device.set_DeviceType_Set(hs, new DeviceTypeInfo_m.DeviceTypeInfo {
				Device_API = DeviceTypeInfo_m.DeviceTypeInfo.eDeviceAPI.Plug_In
			});
			
			VSVGPairs.VSPair temp = new VSVGPairs.VSPair(ePairStatusControl.Both);
			temp.PairType = VSVGPairs.VSVGPairType.Range;
			temp.Render = Enums.CAPIControlType.ValuesRangeSlider;
			temp.RangeStart = 2500;
			temp.RangeEnd = 9000;
			temp.RangeStatusSuffix = " K";

			hs.DeviceVSP_AddPair(hsRef, temp);
			
			device.MISC_Set(hs, Enums.dvMISC.SHOW_VALUES);

			hs.SetDeviceValueByRef(hsRef, 3200, false);
			
			Temperature = hsRef;
		}

		private void associateDevices() {
			IHSApplication hs = plugin.hs;

			DeviceClass root = (DeviceClass) hs.GetDeviceByRef(Root);
			DeviceClass brightness = (DeviceClass) hs.GetDeviceByRef(Brightness);
			DeviceClass color = (DeviceClass) hs.GetDeviceByRef(Color);
			DeviceClass temp = (DeviceClass) hs.GetDeviceByRef(Temperature);
			
			root.set_Relationship(hs, Enums.eRelationship.Parent_Root);
			
			brightness.set_Relationship(hs, Enums.eRelationship.Child);
			root.AssociatedDevice_Add(hs, Brightness);
			brightness.AssociatedDevice_Add(hs, Root);
			
			color.set_Relationship(hs, Enums.eRelationship.Child);
			root.AssociatedDevice_Add(hs, Color);
			color.AssociatedDevice_Add(hs, Root);
			
			temp.set_Relationship(hs, Enums.eRelationship.Child);
			root.AssociatedDevice_Add(hs, Temperature);
			temp.AssociatedDevice_Add(hs, Root);
		}
	}
}
