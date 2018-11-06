using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Web;
using HomeSeerAPI;
using Scheduler;
using Scheduler.Classes;

namespace HSPI_LIFX
{
	// ReSharper disable once InconsistentNaming
	public class HSPI : HspiBase
	{
		private LifxClient.Client lifxClient;
		private List<DeviceDescriptor> knownDeviceCache;
		
		private const uint TRANSITION_TIME = 1000; // TODO make this configurable
		
		public HSPI() {
			Name = "LIFX";
			PluginIsFree = true;
			PluginActionCount = 1;
			PluginSupportsConfigDevice = true;
		}

		public override string InitIO(string port) {
			Program.WriteLog("verbose", "InitIO");
			
			updateKnownDeviceCache();

			lifxClient = new LifxClient.Client {DiscoveryFrequency = 10000};
			lifxClient.StartDiscovery();
			lifxClient.DeviceDiscovered += (object source, LifxClient.DeviceEventArgs args) => {
				processDiscoveredDevice(args.Device);
			};
			
			return "";
		}

		public override void SetIOMulti(List<CAPI.CAPIControl> colSend) {
			// TODO inspect all the commands at once
			
			foreach (CAPI.CAPIControl ctrl in colSend) {
				int devRef = ctrl.Ref;
				int controlValue = (int) ctrl.ControlValue;
				string controlString = ctrl.ControlString;
				
				DeviceClass device = (DeviceClass) hs.GetDeviceByRef(devRef);
				string[] addressParts = device.get_Address(hs).Split('-');
				SubDeviceType subType = (SubDeviceType) int.Parse(addressParts[1]);
				Program.WriteLog("debug", "Device ref " + devRef + " (" + addressParts[0] + " " + subType + ") set to " +
				                          controlValue + " (" + controlString + ")");

				int rootRef = hs.DeviceExistsAddress(addressParts[0], false);
				if (rootRef == -1) {
					Program.WriteLog("error", "Root ref does not exist for " + devRef + " (" + addressParts[0] + ")");
					continue;
				}

				DeviceBundle bundle = new DeviceBundle(addressParts[0], this) {Root = rootRef};
				bundle.TryFindChildren();
				if (!bundle.IsComplete()) {
					Program.WriteLog("error",
						"No complete device bundle for " + devRef + " (" + addressParts[0] + ")");
					continue;
				}

				LifxClient.Device lifxDevice = lifxClient.GetDeviceByAddress(hs3AddressToLifxAddress(addressParts[0]));
				if (lifxDevice == null) {
					Program.WriteLog("error",
						"No known LIFX device for ref " + devRef + " (" + addressParts[0] + ")");
					continue;
				}

				if (subType == SubDeviceType.Brightness && controlValue == 0) {
					lifxDevice.SetPowered(false, TRANSITION_TIME);
					hs.SetDeviceValueByRef(devRef, controlValue, true);
				} else if (subType == SubDeviceType.Brightness && controlValue == 255) {
					lifxDevice.SetPowered(true, TRANSITION_TIME);
					Task.Run(async () => {
						LifxClient.LightStatus status = await lifxDevice.QueryLightStatus();
						hs.SetDeviceValueByRef(devRef, (int) ((double) status.Brightness / ushort.MaxValue * 100), true);
					});
				} else {
					Task.Run(async () => {
						LifxClient.LightStatus status = await lifxDevice.QueryLightStatus();
						HSV color = new HSV {Hue = status.Hue, Saturation = status.Saturation};
						ushort temperature = status.Kelvin;
						ushort brightness = status.Brightness;
						
						switch (subType) {
							case SubDeviceType.Brightness:
								temperature = (ushort) hs.DeviceValue(bundle.Temperature);
								color = ColorConvert.rgbToHsv(
									ColorConvert.stringToRgb(hs.DeviceString(bundle.Color))
								);
								controlValue = Math.Min(controlValue, 100);
								brightness = (ushort) ((double) controlValue / 100.0 * ushort.MaxValue);
								break;
							
							case SubDeviceType.Color:
								color = ColorConvert.rgbToHsv(ColorConvert.stringToRgb(controlString));
								temperature = (ushort) hs.DeviceValue(bundle.Temperature);
								brightness = (ushort) ((double) hs.DeviceValue(bundle.Brightness) / 100.0 * ushort.MaxValue);
								break;
							
							case SubDeviceType.Temperature:
								brightness = (ushort) ((double) hs.DeviceValue(bundle.Brightness) / 100.0 * ushort.MaxValue);
								color = ColorConvert.rgbToHsv(
									ColorConvert.stringToRgb(hs.DeviceString(bundle.Color))
								);
								temperature = (ushort) controlValue;
								break;
						}

						ushort hue = (ushort) (color.Hue * ushort.MaxValue);
						ushort saturation = (ushort) (color.Saturation * ushort.MaxValue);

						if (!status.Powered && brightness > 0) {
							await lifxDevice.SetColorWithAck(hue, saturation, brightness, temperature, 0);
							lifxDevice.SetPowered(true, TRANSITION_TIME);
						} else {
							lifxDevice.SetColor(hue, saturation, brightness, temperature, TRANSITION_TIME);
						}
						
						if (!String.IsNullOrEmpty(controlString)) {
							hs.SetDeviceString(devRef, controlString, true);
						} else {
							hs.SetDeviceValueByRef(devRef, controlValue, true);
						}
					});
				}
			}
		}

		public override string get_ActionName(int actionNumber) {
			switch (actionNumber) {
				case 1:
					return Name + ": Control Device";
				
				default:
					return "UNKNOWN ACTION";
			}
		}

		public override string ActionBuildUI(string unique, IPlugInAPI.strTrigActInfo actInfo) {
			if (actInfo.TANumber != 1) {
				return "Bad action number " + actInfo.TANumber + "," + actInfo.SubTANumber;
			}
			
			LifxControlActionData data = LifxControlActionData.Unserialize(actInfo.DataIn);
			StringBuilder builder = new StringBuilder();

			// Action type dropdown
			clsJQuery.jqDropList actionSelector = new clsJQuery.jqDropList("ActionType" + unique, "events", true);
			actionSelector.AddItem("(Choose A LIFX Action)", LifxControlActionData.ACTION_UNSELECTED.ToString(),
				actInfo.SubTANumber == LifxControlActionData.ACTION_UNSELECTED);
			actionSelector.AddItem("Set color...", LifxControlActionData.ACTION_SET_COLOR.ToString(),
				actInfo.SubTANumber == LifxControlActionData.ACTION_SET_COLOR);
			actionSelector.AddItem("Set color and brightness...", LifxControlActionData.ACTION_SET_COLOR_AND_BRIGHTNESS.ToString(),
				actInfo.SubTANumber == LifxControlActionData.ACTION_SET_COLOR_AND_BRIGHTNESS);
			/*actionSelector.AddItem("Set transition time...", LifxControlActionData.ACTION_SET_TRANSITION_TIME.ToString(),
				actInfo.SubTANumber == LifxControlActionData.ACTION_SET_TRANSITION_TIME);*/
			builder.Append(actionSelector.Build());

			// Device selector dropdown
			if (actInfo.SubTANumber != LifxControlActionData.ACTION_UNSELECTED) {
				clsJQuery.jqDropList deviceSelector = new clsJQuery.jqDropList("Device" + unique, "events", true);
				deviceSelector.AddItem("(Choose A Device)", "0", data.DevRef == 0);
				foreach (DeviceDescriptor device in knownDeviceCache) {
					deviceSelector.AddItem(device.DevName, device.DevRef.ToString(), data.DevRef == device.DevRef);
				}

				builder.Append(deviceSelector.Build());
			}

			if (data.DevRef != 0) {
				switch (actInfo.SubTANumber) {
					case LifxControlActionData.ACTION_UNSELECTED:
						break;

					case LifxControlActionData.ACTION_SET_COLOR:
					case LifxControlActionData.ACTION_SET_COLOR_AND_BRIGHTNESS:
						string chosenColor = "ffffff";
						if (!string.IsNullOrEmpty(data.Color)) {
							chosenColor = data.Color;
						}

						clsJQuery.jqColorPicker colorPicker =
							new clsJQuery.jqColorPicker("Color" + unique, "events", 6, chosenColor);
						builder.Append(colorPicker.Build());

						if (actInfo.SubTANumber == LifxControlActionData.ACTION_SET_COLOR_AND_BRIGHTNESS) {
							clsJQuery.jqDropList brightnessPicker =
								new clsJQuery.jqDropList("BrightnessPercent" + unique, "events", true);
							brightnessPicker.AddItem("", "255", data.BrightnessPercent == 255);
							for (byte i = 0; i <= 100; i++) {
								brightnessPicker.AddItem(i + "%", i.ToString(), data.BrightnessPercent == i);
							}

							builder.Append(brightnessPicker.Build());
						}

						// This is necessary to work around a HS3 bug that doesn't submit the color picker properly
						clsJQuery.jqButton colorSaveBtn =
							new clsJQuery.jqButton("SaveBtn", "Save Color", "events", true);
						colorSaveBtn.submitForm = true;
						builder.Append(colorSaveBtn.Build());
						builder.Append("<br />Due to an HS3 bug, you may need to press Save Color to save this event.<br />");

						clsJQuery.jqCheckBox overrideTransitionBox =
							new clsJQuery.jqCheckBox("OverrideTransitionTime" + unique, "Override transition time",
								"Events", true, true);
						overrideTransitionBox.@checked =
							data.HasFlag(LifxControlActionData.FLAG_OVERRIDE_TRANSITION_TIME);
						builder.Append(overrideTransitionBox.Build());
						
						break;

					case LifxControlActionData.ACTION_SET_TRANSITION_TIME:
						// nothing
						break;

					default:
						builder.Append("Unknown action type " + actInfo.SubTANumber);
						break;
				}
			}

			if (actInfo.SubTANumber == LifxControlActionData.ACTION_SET_TRANSITION_TIME ||
			    data.HasFlag(LifxControlActionData.FLAG_OVERRIDE_TRANSITION_TIME)) {
				
				clsJQuery.jqTimeSpanPicker timePicker =
					new clsJQuery.jqTimeSpanPicker("TransitionTime" + unique, "Transition Time", "events", true);
				timePicker.showDays = false;
				timePicker.defaultTimeSpan = TimeSpan.FromMilliseconds(data.TransitionTimeMilliseconds == 0 ? TRANSITION_TIME : data.TransitionTimeMilliseconds);
				builder.Append(timePicker.Build());
			}

			return builder.ToString();
		}

		public override IPlugInAPI.strMultiReturn ActionProcessPostUI(NameValueCollection postData,
			IPlugInAPI.strTrigActInfo actInfo) {

			if (actInfo.TANumber != 1) {
				throw new Exception("Unknown action number " + actInfo.TANumber);
			}
			
			IPlugInAPI.strMultiReturn output = new IPlugInAPI.strMultiReturn();
			output.TrigActInfo.TANumber = actInfo.TANumber;
			output.TrigActInfo.SubTANumber = actInfo.SubTANumber;
			output.DataOut = actInfo.DataIn;

			foreach (string key in postData.AllKeys) {
				string[] parts = key.Split('_');
				if (parts.Length > 1) {
					postData.Add(parts[0], postData.Get(key));
				}
			}
			
			LifxControlActionData data = LifxControlActionData.Unserialize(actInfo.DataIn);
			
			// Are we changing the action type?
			string newActionType = postData.Get("ActionType");
			if (newActionType != null && int.Parse(newActionType) != actInfo.SubTANumber) {
				output.TrigActInfo.SubTANumber = int.Parse(newActionType);
			}
			
			// Every action has a device
			string newDevRef = postData.Get("Device");
			if (newDevRef != null && int.Parse(newDevRef) != data.DevRef) {
				data.DevRef = int.Parse(newDevRef);
			}

			string newColor = postData.Get("Color");
			if (newColor != null) {
				data.Color = newColor.Replace("#", "");
			}

			string newTransitionTime = postData.Get("TransitionTime");
			if (newTransitionTime != null && LifxControlActionData.IsValidTimeSpan(newTransitionTime)) {
				data.TransitionTimeMilliseconds = (uint) LifxControlActionData.DecodeTimeSpan(newTransitionTime);
			}

			string newBrightnessPct = postData.Get("BrightnessPercent");
			byte newBrightness;
			if (newBrightnessPct != null && byte.TryParse(newBrightnessPct, out newBrightness)) {
				data.BrightnessPercent = newBrightness;
			}

			string newOverrideTransitionTime = postData.Get("OverrideTransitionTime");
			if (newOverrideTransitionTime == "checked") {
				data.Flags |= LifxControlActionData.FLAG_OVERRIDE_TRANSITION_TIME;
			} else if (newOverrideTransitionTime == "unchecked") {
				data.Flags &= ~LifxControlActionData.FLAG_OVERRIDE_TRANSITION_TIME;
			}

			output.DataOut = data.Serialize();
			
			return output;
		}

		public override bool ActionConfigured(IPlugInAPI.strTrigActInfo actInfo) {
			LifxControlActionData data = LifxControlActionData.Unserialize(actInfo.DataIn);
			if (actInfo.SubTANumber == LifxControlActionData.ACTION_UNSELECTED || data.DevRef == 0) {
				return false;
			}

			if (actInfo.SubTANumber == LifxControlActionData.ACTION_SET_COLOR && data.Color.Length != 6) {
				// TODO eventually check for hex
				return false;
			}

			if (actInfo.SubTANumber == LifxControlActionData.ACTION_SET_COLOR_AND_BRIGHTNESS &&
			    (data.Color.Length != 6 || data.BrightnessPercent == 255)) {
				return false;
			}
			
			if (actInfo.SubTANumber == LifxControlActionData.ACTION_SET_TRANSITION_TIME && data.TransitionTimeMilliseconds == 0) {
				return false;
			}

			if (data.HasFlag(LifxControlActionData.FLAG_OVERRIDE_TRANSITION_TIME) && data.TransitionTimeMilliseconds == 0) {
				return false;
			}
			
			return true;
		}

		public override string ActionFormatUI(IPlugInAPI.strTrigActInfo actInfo) {
			if (actInfo.TANumber != 1) {
				return "Unknown action number " + actInfo.TANumber;
			}

			LifxControlActionData data = LifxControlActionData.Unserialize(actInfo.DataIn);

			StringBuilder builder = new StringBuilder();
			builder.Append("Set LIFX ");
			switch (actInfo.SubTANumber) {
				case LifxControlActionData.ACTION_SET_COLOR:
				case LifxControlActionData.ACTION_SET_COLOR_AND_BRIGHTNESS:
					builder.Append("<span class=\"event_Txt_Selection\">Color</span>");
					break;
				
				case LifxControlActionData.ACTION_SET_TRANSITION_TIME:
					builder.Append("<span class=\"event_Txt_Selection\">Transition Time</span>");
					break;
				
				default:
					builder.Append("UNKNOWN");
					break;
			}

			builder.Append(" for <span class=\"event_Txt_Option\">");
			builder.Append(((DeviceClass) hs.GetDeviceByRef(data.DevRef)).get_Name(hs));
			builder.Append("</span> to ");

			switch (actInfo.SubTANumber) {
				case LifxControlActionData.ACTION_SET_COLOR:
				case LifxControlActionData.ACTION_SET_COLOR_AND_BRIGHTNESS:
					builder.Append(
						"<span style=\"display: inline-block; width: 10px; height: 10px; background-color: #" +
						data.Color + "\"></span> ");
					
					builder.Append("<span class=\"event_Txt_Selection\">#" + data.Color.ToUpper() + "</span>");
					if (actInfo.SubTANumber == LifxControlActionData.ACTION_SET_COLOR_AND_BRIGHTNESS) {
						builder.Append(" with brightness <span class=\"event_Txt_Selection\">" +
						               data.BrightnessPercent + "%</span>");
					}
					break;
				
				case LifxControlActionData.ACTION_SET_TRANSITION_TIME:
					builder.Append("<span class=\"event_Txt_Selection\">" + (data.TransitionTimeMilliseconds / 1000) + " seconds</span>");
					break;
				
				default:
					builder.Append("UNKNOWN");
					break;
			}

			if (data.HasFlag(LifxControlActionData.FLAG_OVERRIDE_TRANSITION_TIME)) {
				builder.Append(" over <span class=\"event_Txt_Selection\">");
				builder.Append(data.TransitionTimeMilliseconds / 1000);
				builder.Append(" seconds</span>");
			}

			return builder.ToString();
		}

		public override bool HandleAction(IPlugInAPI.strTrigActInfo actInfo) {
			if (actInfo.TANumber != 1) {
				Program.WriteLog("error", "Bad action number " + actInfo.TANumber + " for event " + actInfo.evRef);
				return false;
			}
			
			LifxControlActionData data = LifxControlActionData.Unserialize(actInfo.DataIn);

			switch (actInfo.SubTANumber) {
				case LifxControlActionData.ACTION_SET_COLOR:
				case LifxControlActionData.ACTION_SET_COLOR_AND_BRIGHTNESS:
					int rootRef = data.DevRef;
					DeviceClass device = (DeviceClass) hs.GetDeviceByRef(rootRef);
					DeviceBundle bundle = new DeviceBundle(device.get_Address(hs).Split('-')[0], this);
					bundle.Root = rootRef;
					bundle.TryFindChildren();
					if (!bundle.IsComplete()) {
						Program.WriteLog("error", "Didn't find a complete device bundle for root " + rootRef + " address " + bundle.Address + " for event " + actInfo.evRef);
						Program.WriteLog("error", bundle.Root + "," + bundle.Color + "," + bundle.Temperature + "," + bundle.Brightness);
						return false;
					}

					LifxClient.Device lifxDevice = lifxClient.GetDeviceByAddress(hs3AddressToLifxAddress(bundle.Address));
					if (lifxDevice == null) {
						Program.WriteLog("error",
							"No LIFX device found on the network for address " + bundle.Address + "for event " +
							actInfo.evRef);
						return false;
					}

					HSV color = ColorConvert.rgbToHsv(ColorConvert.stringToRgb(data.Color));
					ushort hue = (ushort) (color.Hue * ushort.MaxValue);
					ushort sat = (ushort) (color.Saturation * ushort.MaxValue);
					uint transitionTime = data.HasFlag(LifxControlActionData.FLAG_OVERRIDE_TRANSITION_TIME)
						? data.TransitionTimeMilliseconds
						: TRANSITION_TIME;
					ushort temperature = (ushort) ((DeviceClass) hs.GetDeviceByRef(bundle.Temperature)).get_devValue(hs);
					byte brightPct;
					if (actInfo.SubTANumber == LifxControlActionData.ACTION_SET_COLOR_AND_BRIGHTNESS) {
						brightPct = data.BrightnessPercent;
					} else {
						brightPct = (byte) ((DeviceClass) hs.GetDeviceByRef(bundle.Brightness)).get_devValue(hs);
					}

					ushort brightness = (ushort) (((double) brightPct / 100.0) * ushort.MaxValue);
					Task.Run(async () => {
						if (brightness == 0) {
							await lifxDevice.SetPoweredWithAck(false, transitionTime);
							await Task.Delay((int) transitionTime);
							lifxDevice.SetColor(hue, sat, brightness, temperature, 0);

							hs.SetDeviceString(bundle.Color, data.Color, true);
							if (actInfo.SubTANumber == LifxControlActionData.ACTION_SET_COLOR_AND_BRIGHTNESS) {
								hs.SetDeviceValueByRef(bundle.Brightness, data.BrightnessPercent, true);
							}
						} else {
							// Brightness is > 0, so turn it on if necessary
							LifxClient.LightStatus status = await lifxDevice.QueryLightStatus();
							if (!status.Powered) {
								// Set color first
								await lifxDevice.SetColorWithAck(hue, sat, brightness, temperature, 0);
								lifxDevice.SetPowered(true, transitionTime);
							} else {
								// It's already on, so just set color
								lifxDevice.SetColor(hue, sat, brightness, temperature, transitionTime);
							}

							hs.SetDeviceString(bundle.Color, data.Color, true);
							if (actInfo.SubTANumber == LifxControlActionData.ACTION_SET_COLOR_AND_BRIGHTNESS) {
								hs.SetDeviceValueByRef(bundle.Brightness, data.BrightnessPercent, true);
							}
						}
					});
					
					return true;
				
				default:
					return false;
			}
		}
		
		public override string ConfigDevice(int devRef, string user, int userRights, bool newDevice) {
			Program.WriteLog("debug", "ConfigDevice called for device " + devRef + " by user " + user + " with rights " + userRights);

			DeviceClass device = (DeviceClass) hs.GetDeviceByRef(devRef);
			PlugExtraData.clsPlugExtraData extraData = device.get_PlugExtraData_Get(hs);
			object tempObj;

			StringBuilder builder = new StringBuilder();
			builder.Append("<table width=\"100%\" cellspacing=\"0\">");
			builder.Append("<tr><td class=\"tableheader\" colspan=\"8\">LIFX Device Settings</td></tr>");
			
			// Transition rate
			tempObj = extraData.GetNamed("TransitionRateMs");
			uint transitionTimeMs = TRANSITION_TIME;
			if (tempObj != null) {
				transitionTimeMs = (uint) tempObj;
			}
			
			clsJQuery.jqTimeSpanPicker timeSpan =
				new clsJQuery.jqTimeSpanPicker("LifxTransitionRate", "Transition Time", "DeviceUtility", true);
			timeSpan.showDays = false;
			timeSpan.defaultTimeSpan = TimeSpan.FromMilliseconds(transitionTimeMs);
			builder.Append("<tr><td class=\"tablecell\" colspan=\"1\" align=\"left\">Transition Time:</td>");
			builder.Append("<td class=\"tablecell\" colspan=\"7\" align=\"left\">");
			builder.Append(timeSpan.Build());
			builder.Append("</td></tr>");
			
			// Sync label
			tempObj = extraData.GetNamed("SyncLabel");
			bool syncLabel = false;
			if (tempObj != null) {
				syncLabel = (bool) tempObj;
			}

			clsJQuery.jqCheckBox checkBox =
				new clsJQuery.jqCheckBox("LifxSyncLabel", "Sync device name", "DeviceUtility", true, true);
			checkBox.@checked = syncLabel;
			builder.Append("<tr><td class=\"tablecell\" colspan=\"1\" align=\"left\">Sync Device Name:</td>");
			builder.Append("<td class=\"tablecell\" colspan=\"7\" align=\"left\">");
			builder.Append(checkBox.Build());
			builder.Append("<br /><hr />Updates the device's name in HS3 if it changes in the LIFX app, and vice versa.<br />Enabling this option results in slightly more LAN traffic.");
			builder.Append("</td></tr>");
			
			// Sync state
			tempObj = extraData.GetNamed("SyncState");
			bool syncState = false;
			if (tempObj != null) {
				syncState = (bool) tempObj;
			}

			checkBox = new clsJQuery.jqCheckBox("LifxSyncState", "Sync light state", "DeviceUtility", true, true);
			checkBox.@checked = syncState;
			builder.Append("<tr><td class=\"tablecell\" colspan=\"1\" align=\"left\">Sync Light State:</td>");
			builder.Append("<td class=\"tablecell\" colspan=\"7\" align=\"left\">");
			builder.Append(checkBox.Build());
			builder.Append("<br /><hr />Updates the light's state in HS3 if it changes in the LIFX app. If enabled, events will be triggered based on light state changes via the LIFX app.<br />Enabling this option results in slightly more LAN traffic.");
			builder.Append("</td></tr>");

			builder.Append("</table>");

			clsJQuery.jqButton button = new clsJQuery.jqButton("LifxDone", "Done", "DeviceUtility", true);
			builder.Append("<br /><br />");
			builder.Append(button.Build());
			return builder.ToString();
		}

		public override Enums.ConfigDevicePostReturn ConfigDevicePost(int devRef, string data, string user,
			int userRights) {

			Program.WriteLog("debug",
				"ConfigDevicePost called by " + user + " with rights " + userRights + " for device " + devRef +
				" with data " + data);

			DeviceClass device = (DeviceClass) hs.GetDeviceByRef(devRef);
			PlugExtraData.clsPlugExtraData extraData = device.get_PlugExtraData_Get(hs);
			
			NameValueCollection postData = HttpUtility.ParseQueryString(data);
			string val;

			if ((val = postData.Get("LifxTransitionRate")) != null && LifxControlActionData.IsValidTimeSpan(val)) {
				extraData.RemoveNamed("TransitionRateMs");
				extraData.AddNamed("TransitionRateMs", LifxControlActionData.DecodeTimeSpan(val));
			}

			if ((val = postData.Get("LifxSyncLabel")) != null) {
				extraData.RemoveNamed("SyncLabel");
				extraData.AddNamed("SyncLabel", val == "checked");
			}

			if ((val = postData.Get("LifxSyncState")) != null) {
				extraData.RemoveNamed("SyncState");
				extraData.AddNamed("SyncState", val == "checked");
			}

			device.set_PlugExtraData_Set(hs, extraData);

			return postData.Get("LifxDone") != null
				? Enums.ConfigDevicePostReturn.DoneAndSave
				: Enums.ConfigDevicePostReturn.DoneAndCancelAndStay;
		}

		private void processDiscoveredDevice(LifxClient.Device lifxDevice) {
			string hs3Addr = lifxAddressToHs3Address(lifxDevice.Address);
			Program.WriteLog("debug", "Discovered LIFX device " + hs3Addr + " at " + lifxDevice.IPAddress);
			
			// Do we already have an HS3 device for this?
			DeviceBundle bundle = new DeviceBundle(hs3Addr, this);

			int devRef = hs.DeviceExistsAddress(bundle.GetSubDeviceAddress(SubDeviceType.Root), false);
			bundle.Root = devRef == -1 ? 0 : devRef;
			bundle.TryFindChildren();

			if (bundle.IsComplete()) {
				Program.WriteLog("info", "Complete device bundle found in HS3 for LIFX device " + hs3Addr);
			} else {
				Program.WriteLog("info", "Creating HS3 devices for LIFX device " + hs3Addr + " (" + lifxDevice.LastKnownStatus.Label + ")");
				bundle.CreateDevices(lifxDevice.LastKnownStatus.Label);
				updateKnownDeviceCache();
			}
		}
		
		private string lifxAddressToHs3Address(ulong lifxAddress) {
			string addressHex = lifxAddress.ToString("X12");
			StringBuilder builder = new StringBuilder();

			for (int i = 5; i >= 0; i--) {
				builder.Append(addressHex.Substring(i * 2, 2));
			}

			return builder.ToString().ToUpper();
		}

		private ulong hs3AddressToLifxAddress(string hs3Address) {
			StringBuilder builder = new StringBuilder();
			for (int i = 5; i >= 0; i--) {
				builder.Append(hs3Address.Substring(i * 2, 2));
			}

			return ulong.Parse(builder.ToString(), NumberStyles.HexNumber);
		}

		private void updateKnownDeviceCache() {
			List<DeviceDescriptor> devices = new List<DeviceDescriptor>();

			clsDeviceEnumeration enumerator = (clsDeviceEnumeration) hs.GetDeviceEnumerator();
			do {
				DeviceClass enumeratedDevice = enumerator.GetNext();
				if (enumeratedDevice == null) {
					continue;
				}

				if (
					enumeratedDevice.get_Interface(hs) == Name &&
				    enumeratedDevice.get_Relationship(hs) == Enums.eRelationship.Parent_Root
				) {
					devices.Add(new DeviceDescriptor {
						DevName = enumeratedDevice.get_Name(hs),
						DevRef = enumeratedDevice.get_Ref(hs)
					});
				}
			} while (!enumerator.Finished);

			knownDeviceCache = devices.OrderBy(d => d.DevName).ToList();
		}
	}

	public class DeviceDescriptor
	{
		public int DevRef { get; set; }
		public string DevName { get; set; }
	}
}
