using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Web;
using HomeSeerAPI;
using LifxClient;
using Scheduler;
using Scheduler.Classes;

namespace HSPI_LIFX
{
	// ReSharper disable once InconsistentNaming
	public class HSPI : HspiBase
	{
		private Client lifxClient;
		private List<DeviceDescriptor> knownDeviceCache;
		private readonly List<DeviceDescriptor> devicesToPoll;
		private readonly Dictionary<int, Timer> ignoreControls;
		private Timer pollTimer;
		
		private const uint TRANSITION_TIME = 1000;
		private const ushort DISCOVERY_FREQUENCY = 30000;
		private const ushort DEVICE_STATUS_POLL_FREQUENCY = 10000;
		
		public HSPI() {
			Name = "HS3LIFX";
			PluginIsFree = true;
			PluginActionCount = 1;
			PluginSupportsConfigDevice = true;
			
			devicesToPoll = new List<DeviceDescriptor>();
			ignoreControls = new Dictionary<int, Timer>();
		}

		public override string InitIO(string port) {
			Program.WriteLog("verbose", "InitIO");
			
			updateKnownDeviceCache();

			Program.WriteLog("console", "Known device cache updated");
			
			foreach (DeviceDescriptor device in knownDeviceCache) {
				if (checkShouldPollDevice(device)) {
					devicesToPoll.Add(device);
				}
			}

			Program.WriteLog("console", "Devices to poll list built");

			lifxClient = new Client {DiscoveryFrequency = DISCOVERY_FREQUENCY};
			lifxClient.StartDiscovery();
			lifxClient.DeviceDiscovered += (object source, DeviceEventArgs args) => {
				processDiscoveredDevice(args.Device);
			};
			lifxClient.DeviceLost += (src, arg) => {
				Program.WriteLog("warn", $"LIFX device lost: {arg.Device.Address} (IP {arg.Device.IPAddress})");
			};
			
			Program.WriteLog("console", "LIFX client started discovery");
			
			pollTimer = new Timer(DEVICE_STATUS_POLL_FREQUENCY);
			pollTimer.AutoReset = true;
			pollTimer.Elapsed += (object src, ElapsedEventArgs args) => { pollDevices(); };
			pollTimer.Start();

			Program.WriteLog("verbose", "InitIO returning");
			return "";
		}

		public override void SetIOMulti(List<CAPI.CAPIControl> colSend) {
			// TODO inspect all the commands at once

			// Reset the poll timer so we don't run the risk of race conditions
			pollTimer.Stop();
			pollTimer.Start();
			
			foreach (CAPI.CAPIControl ctrl in colSend) {
				if (ctrl == null) {
					continue;	
				}
				
				int devRef = ctrl.Ref;
				Timer ignoreTimer;
				if (ignoreControls.TryGetValue(devRef, out ignoreTimer)) {
					ignoreTimer.Dispose();
					ignoreControls.Remove(devRef);
					continue;
				}
				
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

				Device lifxDevice = lifxClient.GetDeviceByAddress(hs3AddressToLifxAddress(addressParts[0]));
				if (lifxDevice == null) {
					Program.WriteLog("error",
						"No known LIFX device for ref " + devRef + " (" + addressParts[0] + ")");
					continue;
				}

				uint transitionTime = TRANSITION_TIME;
				PlugExtraData.clsPlugExtraData extraData = device.get_PlugExtraData_Get(hs);
				try {
					object tempObj;
					tempObj = extraData.GetNamed("TransitionRateMs");
					if (tempObj != null) {
						transitionTime = (uint) tempObj;
					}
				}
				catch (Exception) {}

				if (subType == SubDeviceType.Brightness && controlValue == 0) {
					lifxDevice.SetPowered(false, transitionTime);
					IgnoreNextDeviceControl(devRef);
					hs.SetDeviceValueByRef(devRef, controlValue, true);
				} else if (subType == SubDeviceType.Brightness && controlValue == 255) {
					lifxDevice.SetPowered(true, transitionTime);
					Task.Run(async () => {
						LightStatus status = await lifxDevice.QueryLightStatus();
						double newBrightness = Math.Min(Math.Round(((double) status.Brightness / ushort.MaxValue) * 100), 99);
						IgnoreNextDeviceControl(devRef);
						hs.SetDeviceValueByRef(devRef, newBrightness, true);
					});
				} else {
					Task.Run(async () => {
						LightStatus status = await lifxDevice.QueryLightStatus();
						HSV color = new HSV {Hue = status.Hue, Saturation = status.Saturation};
						ushort temperature = status.Kelvin;
						ushort brightness = status.Brightness;
						
						switch (subType) {
							case SubDeviceType.Brightness:
								temperature = (ushort) hs.DeviceValue(bundle.Temperature);
								color = ColorConvert.rgbToHsv(
									ColorConvert.stringToRgb(hs.DeviceString(bundle.Color))
								);
								controlValue = Math.Min(controlValue, 99);
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
							lifxDevice.SetPowered(true, transitionTime);
						} else {
							lifxDevice.SetColor(hue, saturation, brightness, temperature, transitionTime);
						}
						
						if (!string.IsNullOrEmpty(controlString)) {
							hs.SetDeviceString(devRef, controlString, true);
						} else {
							IgnoreNextDeviceControl(devRef);
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
			actionSelector.AddItem("Recall Multi-Zone Theme...", LifxControlActionData.ACTION_RECALL_MZ_THEME.ToString(),
				actInfo.SubTANumber == LifxControlActionData.ACTION_RECALL_MZ_THEME);
			actionSelector.AddItem("Set transition time...", LifxControlActionData.ACTION_SET_TRANSITION_TIME.ToString(),
				actInfo.SubTANumber == LifxControlActionData.ACTION_SET_TRANSITION_TIME);
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
				clsJQuery.jqCheckBox overrideTransitionBox;
				
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

						overrideTransitionBox = new clsJQuery.jqCheckBox("OverrideTransitionTime" + unique, "Override transition time",
								"Events", true, true);
						overrideTransitionBox.@checked =
							data.HasFlag(LifxControlActionData.FLAG_OVERRIDE_TRANSITION_TIME);
						builder.Append(overrideTransitionBox.Build());
						
						break;
					
					case LifxControlActionData.ACTION_RECALL_MZ_THEME:
						clsJQuery.jqDropList themePicker = new clsJQuery.jqDropList("Color" + unique, "events", true);
						themePicker.AddItem("(Choose A Theme)", "", data.Color == "");
						foreach (string name in getMultizoneThemes(((DeviceClass) hs.GetDeviceByRef(data.DevRef)).get_PlugExtraData_Get(hs)).Keys) {
							themePicker.AddItem(name, name, data.Color == name);
						}

						builder.Append(themePicker.Build());
						
						overrideTransitionBox =
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

			if (actInfo.SubTANumber == LifxControlActionData.ACTION_RECALL_MZ_THEME && string.IsNullOrEmpty(data.Color)) {
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
				
				case LifxControlActionData.ACTION_RECALL_MZ_THEME:
					builder.Append("<span class=\"event_Txt_Selection\">Multi-Zone Theme</span>");
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
				
				case LifxControlActionData.ACTION_RECALL_MZ_THEME:
					builder.Append("<span class=\"event_Txt_Selection\">" + data.Color + "</span>");
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

			// Reset the poll timer so we don't run the risk of race conditions
			pollTimer.Stop();
			pollTimer.Start();
			
			LifxControlActionData data = LifxControlActionData.Unserialize(actInfo.DataIn);

			int rootRef;
			DeviceClass device;
			PlugExtraData.clsPlugExtraData extraData;
			Device lifxDevice;
			uint transitionTime;

			switch (actInfo.SubTANumber) {
				case LifxControlActionData.ACTION_SET_COLOR:
				case LifxControlActionData.ACTION_SET_COLOR_AND_BRIGHTNESS:
					rootRef = data.DevRef;
					device = (DeviceClass) hs.GetDeviceByRef(rootRef);
					DeviceBundle bundle = new DeviceBundle(device.get_Address(hs).Split('-')[0], this);
					bundle.Root = rootRef;
					bundle.TryFindChildren();
					if (!bundle.IsComplete()) {
						Program.WriteLog("error", "Didn't find a complete device bundle for root " + rootRef + " address " + bundle.Address + " for event " + actInfo.evRef);
						Program.WriteLog("error", bundle.Root + "," + bundle.Color + "," + bundle.Temperature + "," + bundle.Brightness);
						return false;
					}

					lifxDevice = lifxClient.GetDeviceByAddress(hs3AddressToLifxAddress(bundle.Address));
					if (lifxDevice == null) {
						Program.WriteLog("error",
							"No LIFX device found on the network for address " + bundle.Address + " for event " +
							actInfo.evRef);
						return false;
					}

					transitionTime = TRANSITION_TIME;
					extraData = device.get_PlugExtraData_Get(hs);
					try {
						object tempObj = extraData.GetNamed("TransitionRateMs");
						if (tempObj != null) {
							transitionTime = (uint) tempObj;
						}
					}
					catch (Exception) {}

					if (data.HasFlag(LifxControlActionData.FLAG_OVERRIDE_TRANSITION_TIME)) {
						transitionTime = data.TransitionTimeMilliseconds;
					}
					
					HSV color = ColorConvert.rgbToHsv(ColorConvert.stringToRgb(data.Color));
					ushort hue = (ushort) (color.Hue * ushort.MaxValue);
					ushort sat = (ushort) (color.Saturation * ushort.MaxValue);
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
							LightStatus status = await lifxDevice.QueryLightStatus();
							await Task.Delay((int) transitionTime);
							lifxDevice.SetColor(hue, sat, status.Brightness, temperature, 0);

							hs.SetDeviceString(bundle.Color, data.Color, true);
							if (actInfo.SubTANumber == LifxControlActionData.ACTION_SET_COLOR_AND_BRIGHTNESS) {
								IgnoreNextDeviceControl(bundle.Brightness);
								hs.SetDeviceValueByRef(bundle.Brightness, data.BrightnessPercent, true);
							}
						} else {
							// Brightness is > 0, so turn it on if necessary
							LightStatus status = await lifxDevice.QueryLightStatus();
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
								IgnoreNextDeviceControl(bundle.Brightness);
								hs.SetDeviceValueByRef(bundle.Brightness, data.BrightnessPercent, true);
							}
						}
					});
					
					return true;
				
				case LifxControlActionData.ACTION_RECALL_MZ_THEME:
					rootRef = data.DevRef;
					device = (DeviceClass) hs.GetDeviceByRef(rootRef);
					extraData = device.get_PlugExtraData_Get(hs);

					Dictionary<string, MultiZoneTheme> themes = getMultizoneThemes(extraData);
					if (!themes.ContainsKey(data.Color)) {
						return false;
					}
					
					transitionTime = TRANSITION_TIME;
					try {
						object tempObj = extraData.GetNamed("TransitionRateMs");
						if (tempObj != null) {
							transitionTime = (uint) tempObj;
						}
					}
					catch (Exception) {}

					if (data.HasFlag(LifxControlActionData.FLAG_OVERRIDE_TRANSITION_TIME)) {
						transitionTime = data.TransitionTimeMilliseconds;
					}

					MultiZoneTheme theme = themes[data.Color];
					string[] addressParts = device.get_Address(hs).Split('-');
					lifxDevice = lifxClient.GetDeviceByAddress(hs3AddressToLifxAddress(addressParts[0]));
					lifxDevice.SetPowered(true, transitionTime);
					lifxDevice.SetExtendedColorZones(transitionTime, theme.State.Index, theme.State.Colors);
					return true;

				case LifxControlActionData.ACTION_SET_TRANSITION_TIME:
					rootRef = data.DevRef;
					device = (DeviceClass) hs.GetDeviceByRef(rootRef);
					extraData = device.get_PlugExtraData_Get(hs);
					
					extraData.RemoveNamed("TransitionRateMs");
					extraData.AddNamed("TransitionRateMs", data.TransitionTimeMilliseconds);
					device.set_PlugExtraData_Set(hs, extraData);
					return true;
				
				default:
					return false;
			}
		}
		
		public override string ConfigDevice(int devRef, string user, int userRights, bool newDevice) {
			Program.WriteLog("debug", "ConfigDevice called for device " + devRef + " by user " + user + " with rights " + userRights);

			DeviceClass device = (DeviceClass) hs.GetDeviceByRef(devRef);
			if (device.get_Relationship(hs) != Enums.eRelationship.Parent_Root) {
				// find the parent
				DeviceClass parentDevice = null;
				foreach (int associatedRef in device.get_AssociatedDevices(hs)) {
					parentDevice = (DeviceClass) hs.GetDeviceByRef(associatedRef);
					if (parentDevice.get_Relationship(hs) == Enums.eRelationship.Parent_Root) {
						device = parentDevice;
						devRef = associatedRef;
						break;
					}
				}

				if (device != parentDevice) {
					return "Error finding parent device.";
				}
			}
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
			builder.Append("<br /><hr />Updates the device's name in HS3 if it changes in the LIFX app.<br />Do not enable this option if you want the device to have a different name in HS3 and in the LIFX app.<br />Enabling this option results in slightly more LAN traffic.");
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
			
			// Multi-zone saved stuff
			builder.Append("<table width=\"100%\" cellspacing=\"0\">");
			builder.Append("<tr><td class=\"tableheader\" colspan=\"8\">Multi-Zone Themes (supported LIFX devices only)</td></tr>");
			
			clsJQuery.jqDropList dropList = new clsJQuery.jqDropList("LifxDeleteMultizone", "DeviceUtility", true);
			dropList.AddItem("(Select a Theme to Delete)", "", true);
			foreach (string name in getMultizoneThemes(extraData).Keys) {
				dropList.AddItem(name, name, false);
			}

			builder.Append("<tr><td class=\"tablecell\" colspan=\"1\" align=\"left\">Delete Theme</td>");
			builder.Append("<td class=\"tablecell\" colspan=\"7\" align=\"left\">");
			if (dropList.items.Count > 1) {
				builder.Append(dropList.Build());
			} else {
				builder.Append("-- NO MULTIZONE THEMES --");
			}
			builder.Append("</td></tr>");

			builder.Append("<tr><td class=\"tablecell\" colspan=\"1\" align=\"left\">Save MultiZone Theme</td>");
			builder.Append("<td class=\"tablecell\" colspan=\"7\" align=\"left\">");
			
			clsJQuery.jqTextBox textBox = new clsJQuery.jqTextBox("LifxSaveMZName", "text", "", "DeviceUtility", 60, true);
			builder.Append(textBox.Build());
			builder.Append("<br />Type the name you desire for your MZ theme. The current device configuration will be saved under that name.");
			builder.Append("</td></tr>");
			
			builder.Append("</table>");

			clsJQuery.jqButton button = new clsJQuery.jqButton("LifxDone", "Done", "DeviceUtility", true);
			builder.Append("<br /><br />");
			builder.Append(button.Build());
			return builder.ToString();
		}

		public override Enums.ConfigDevicePostReturn ConfigDevicePost(int devRef, string data, string user, int userRights) {
			Program.WriteLog("debug",
				"ConfigDevicePost called by " + user + " with rights " + userRights + " for device " + devRef +
				" with data " + data);

			DeviceClass device = (DeviceClass) hs.GetDeviceByRef(devRef);
			if (device.get_Relationship(hs) != Enums.eRelationship.Parent_Root) {
				// find the parent
				DeviceClass parentDevice = null;
				foreach (int associatedRef in device.get_AssociatedDevices(hs)) {
					parentDevice = (DeviceClass) hs.GetDeviceByRef(associatedRef);
					if (parentDevice.get_Relationship(hs) == Enums.eRelationship.Parent_Root) {
						device = parentDevice;
						devRef = associatedRef;
						break;
					}
				}

				if (device != parentDevice) {
					throw new Exception("Error finding parent device.");
				}
			}
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
			DeviceDescriptor descriptor = findDeviceDescriptorByRef(devRef);
			bool shouldPollDevice = checkShouldPollDevice(descriptor, extraData);
			if (shouldPollDevice && !devicesToPoll.Contains(descriptor)) {
				devicesToPoll.Add(descriptor);
			} else if (!shouldPollDevice && devicesToPoll.Contains(descriptor)) {
				devicesToPoll.Remove(descriptor);
			}

			if ((val = postData.Get("LifxSaveMZName")) != null && val != "") {
				Program.WriteLog("info", $"Saving multizone theme {val}");
				string[] addressParts = device.get_Address(hs).Split('-');
				Device lifxDevice = lifxClient.GetDeviceByAddress(hs3AddressToLifxAddress(addressParts[0]));
				if (lifxDevice == null) {
					Program.WriteLog("error", $"No known LIFX device for ref {devRef} trying to save MZ");
					return Enums.ConfigDevicePostReturn.DoneAndCancelAndStay;
				}

				try {
					Task<ColorZoneState> task = lifxDevice.GetExtendedColorZones();
					task.Wait();
					BinaryFormatter formatter = new BinaryFormatter();
					MemoryStream stream = new MemoryStream();
					formatter.Serialize(stream, new MultiZoneTheme {Name = val, State = task.Result});
					
					extraData.AddNamed("mzt_" + val.ToLower(), stream.GetBuffer());
					device.set_PlugExtraData_Set(hs, extraData);

					stream.Dispose();
					return Enums.ConfigDevicePostReturn.DoneAndSave;
				} catch (Exception ex) {
					Program.WriteLog("error", $"Cannot save MZ: {ex.Message}");
					return Enums.ConfigDevicePostReturn.DoneAndCancelAndStay;
				}
			}

			if ((val = postData.Get("LifxDeleteMultizone")) != null && val != "") {
				Program.WriteLog("info", $"Deleting multizone theme {val}");
				extraData.RemoveNamed("mzt_" + val.ToLower());
				device.set_PlugExtraData_Set(hs, extraData);
				return Enums.ConfigDevicePostReturn.DoneAndSave;
			}

			return postData.Get("LifxDone") != null
				? Enums.ConfigDevicePostReturn.DoneAndSave
				: Enums.ConfigDevicePostReturn.DoneAndCancelAndStay;
		}

		private Dictionary<string, MultiZoneTheme> getMultizoneThemes(PlugExtraData.clsPlugExtraData extraData) {
			Dictionary<string, MultiZoneTheme> output = new Dictionary<string, MultiZoneTheme>();
			foreach (string key in extraData.GetNamedKeys()) {
				if (key.StartsWith("mzt_")) {
					BinaryFormatter formatter = new BinaryFormatter();
					MemoryStream stream = new MemoryStream((byte[]) extraData.GetNamed(key));
					MultiZoneTheme theme = (MultiZoneTheme) formatter.Deserialize(stream);
					output.Add(theme.Name, theme);
					stream.Dispose();
				}
			}

			return output;
		}

		private void processDiscoveredDevice(Device lifxDevice) {
			string hs3Addr = lifxAddressToHs3Address(lifxDevice.Address);
			Program.WriteLog("info", "Discovered LIFX device " + hs3Addr + " at " + lifxDevice.IPAddress);
			
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
						DevRef = enumeratedDevice.get_Ref(hs),
						DevAddress = enumeratedDevice.get_Address(hs).Split('-')[0],
					});
				}
			} while (!enumerator.Finished);

			knownDeviceCache = devices.OrderBy(d => d.DevName).ToList();
		}
		
		public DeviceDescriptor findDeviceDescriptorByRef(int devRef) {
			foreach (DeviceDescriptor device in knownDeviceCache) {
				if (device.DevRef == devRef) {
					return device;
				}
			}

			return null;
		}

		private bool checkShouldPollDevice(DeviceDescriptor device, PlugExtraData.clsPlugExtraData extraData = null) {
			try {
				if (extraData == null) {
					extraData = ((DeviceClass) hs.GetDeviceByRef(device.DevRef)).get_PlugExtraData_Get(hs);
				}

				object tempObj;
				if ((tempObj = extraData.GetNamed("SyncLabel")) != null && (bool) tempObj) {
					return true;
				}

				if ((tempObj = extraData.GetNamed("SyncState")) != null && (bool) tempObj) {
					return true;
				}

				return false;
			}
			catch (Exception ex) {
				Program.WriteLog("warn",
					"Exception trying to check whether we should poll device " + device.DevRef + ": " + ex.Message);
				return false;
			}
		}

		private void pollDevices() {
			foreach (DeviceDescriptor descriptor in devicesToPoll) {
				Device lifxDevice = lifxClient.GetDeviceByAddress(hs3AddressToLifxAddress(descriptor.DevAddress));
				if (lifxDevice == null) {
					continue;
				}

				DeviceClass hsDevice = (DeviceClass) hs.GetDeviceByRef(descriptor.DevRef);
				DeviceBundle bundle = new DeviceBundle(hsDevice.get_Address(hs), this) {Root = descriptor.DevRef};
				bool shouldUpdateLabel = false;
				bool shouldUpdateState = false;

				PlugExtraData.clsPlugExtraData extraData = hsDevice.get_PlugExtraData_Get(hs);
				try {
					object tempObj = extraData.GetNamed("SyncLabel");
					if (tempObj != null && (bool) tempObj) {
						shouldUpdateLabel = true;
					}
				}
				catch (Exception) {}
				
				try {
					object tempObj = extraData.GetNamed("SyncState");
					if (tempObj != null && (bool) tempObj) {
						shouldUpdateState = true;
					}
				}
				catch (Exception) {}

				Task.Run(async () => {
					Program.WriteLog("verbose", "Running poll on LIFX device " + bundle.Address + ", ref "+
					                            bundle.Root + " to sync " +
					                            (shouldUpdateLabel ? "label " : "") +
					                            (shouldUpdateState ? "state" : "")
					);
					LightStatus status = await lifxDevice.QueryLightStatus();
					bundle.TryFindChildren();
					if (!bundle.IsComplete()) {
						return;
					}
					
					if (shouldUpdateLabel && status.Label != hsDevice.get_Name(hs)) {
						// Name has changed, so sync it
						Program.WriteLog("info", "Syncing device " + bundle.Root + " name to \"" + status.Label + "\" (was \"" + hsDevice.get_Name(hs) + "\")");
						bundle.UpdateName(status.Label);
					}

					if (shouldUpdateState) {
						HSV hsv = new HSV();
						hsv.Hue = (double) status.Hue / ushort.MaxValue;
						hsv.Saturation = (double) status.Saturation / ushort.MaxValue;

						DeviceClass devBright = (DeviceClass) hs.GetDeviceByRef(bundle.Brightness);
						DeviceClass devColor = (DeviceClass) hs.GetDeviceByRef(bundle.Color);
						DeviceClass devTemp = (DeviceClass) hs.GetDeviceByRef(bundle.Temperature);

						byte actualBright = status.Powered
							? (byte) Math.Min(Math.Round(((double) status.Brightness / ushort.MaxValue) * 100.0), 99)
							: (byte) 0;
						byte hsBright = (byte) devBright.get_devValue(hs);
						if (actualBright != hsBright) {
							Program.WriteLog("info",
								"Updating brightness in HS3 for device " + bundle.Root + "; it is " + actualBright +
								" but HS3 believes it is " + hsBright);
							IgnoreNextDeviceControl(bundle.Brightness);
							hs.SetDeviceValueByRef(bundle.Brightness, actualBright, true);
						} else {
							Program.WriteLog("console", "Brightness in HS3 for device " + bundle.Root + " matches: " + actualBright + " vs " + hsBright);
						}

						string actualColor = ColorConvert.hsvToRgb(hsv).ToString().ToLower();
						string hsColor = devColor.get_devString(hs).ToLower();
						if (actualColor != hsColor) {
							Program.WriteLog("info",
								"Updating color in HS3 for device " + bundle.Root + "; it is " + actualColor +
								" but HS3 believes it is " + hsColor);
							hs.SetDeviceString(bundle.Color, actualColor, true);
						} else {
							Program.WriteLog("console", "Color in HS3 for device " + bundle.Root + " matches: " + actualColor + " vs " + hsColor);
						}

						int hsTemp = (int) devTemp.get_devValue(hs);
						if (status.Kelvin != hsTemp) {
							Program.WriteLog("info",
								"Updating temperature in HS3 for device " + bundle.Root + "; it is " + status.Kelvin +
								" but HS3 believes it is " + hsTemp);
							IgnoreNextDeviceControl(bundle.Temperature);
							hs.SetDeviceValueByRef(bundle.Temperature, status.Kelvin, true);
						} else {
							Program.WriteLog("console", "Temp in HS3 for device " + bundle.Root + " matches: " + status.Kelvin + " vs " + hsTemp);							
						}
					}
				});
			}
		}
		
		public void IgnoreNextDeviceControl(int devRef) {
			if (ignoreControls.ContainsKey(devRef)) {
				return;
			}

			Timer timer = new Timer(1000);
			timer.AutoReset = false;
			timer.Elapsed += (object src, ElapsedEventArgs args) => {
				if (ignoreControls.ContainsKey(devRef)) {
					ignoreControls.Remove(devRef);
				}
			};
			timer.Start();

			ignoreControls.Add(devRef, timer);
		}
	}

	public class DeviceDescriptor : IEquatable<DeviceDescriptor>
	{
		public int DevRef { get; set; }
		public string DevName { get; set; }
		public string DevAddress { get; set; }
		
		public bool Equals(DeviceDescriptor other) {
			if (ReferenceEquals(null, other)) return false;
			if (ReferenceEquals(this, other)) return true;
			return DevRef == other.DevRef && string.Equals(DevName, other.DevName) && string.Equals(DevAddress, other.DevAddress);
		}

		public override bool Equals(object obj) {
			if (ReferenceEquals(null, obj)) return false;
			if (ReferenceEquals(this, obj)) return true;
			if (obj.GetType() != this.GetType()) return false;
			return Equals((DeviceDescriptor) obj);
		}

		public override int GetHashCode() {
			unchecked {
				var hashCode = DevRef;
				hashCode = (hashCode * 397) ^ DevName.GetHashCode();
				hashCode = (hashCode * 397) ^ DevAddress.GetHashCode();
				return hashCode;
			}
		}
	}

	[Serializable]
	internal struct MultiZoneTheme {
		public string Name;
		public ColorZoneState State;
	}
}
