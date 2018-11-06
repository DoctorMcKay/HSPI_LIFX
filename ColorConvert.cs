using System;
using System.Globalization;

namespace HSPI_LIFX
{
	public class ColorConvert
	{
		public static RGB stringToRgb(string input) {
			return new RGB {
				Red = byte.Parse(input.Substring(0, 2), NumberStyles.HexNumber),
				Green = byte.Parse(input.Substring(2, 2), NumberStyles.HexNumber),
				Blue = byte.Parse(input.Substring(4, 2), NumberStyles.HexNumber),
			};
		}
		
		public static HSV rgbToHsv(RGB input) {
			double r = input.Red / 255.0;
			double g = input.Green / 255.0;
			double b = input.Blue / 255.0;

			double min = Math.Min(Math.Min(r, g), b);
			double max = Math.Max(Math.Max(r, g), b);
			double deltaMax = max - min;

			HSV output = new HSV();
			output.Value = max;

			if (doubleEquals(deltaMax, 0)) {
				// Gray (which is white)
				output.Hue = 0;
				output.Saturation = 0;
			} else {
				output.Saturation = deltaMax / max;

				double deltaR = ((max - r) / 6 + deltaMax / 2) / deltaMax;
				double deltaG = ((max - g) / 6 + deltaMax / 2) / deltaMax;
				double deltaB = ((max - b) / 6 + deltaMax / 2) / deltaMax;

				if (doubleEquals(r, max)) {
					output.Hue = deltaB - deltaG;
				} else if (doubleEquals(g, max)) {
					output.Hue = (1.0 / 3.0) + deltaR - deltaB;
				} else if (doubleEquals(b, max)) {
					output.Hue = (2.0 / 3.0) + deltaG - deltaR;
				}

				if (output.Hue < 0) {
					output.Hue += 1;
				}

				if (output.Hue > 1) {
					output.Hue -= 1;
				}
			}

			return output;
		}

		/// <summary>
		/// Before you take this code from GitHub and use it in your own project, be aware that LIFX handles the "Value"
		/// component as a separate "brightness" value. Consequently, the "Value" in the input is ignored and is assumed
		/// to be maximum.
		/// http://www.easyrgb.com/en/math.php#text20
		/// </summary>
		/// <param name="input"></param>
		/// <returns></returns>
		public static RGB hsvToRgb(HSV input) {
			if (doubleEquals(input.Saturation, 0.0)) {
				return new RGB {Red = 255, Green = 255, Blue = 255};
			}
			
			RGB output = new RGB();
			double h = input.Hue * 6.0;
			if (doubleEquals(h, 6)) {
				h = 0;
			}

			int i = (int) h;
			double v1 = 1.0 - input.Saturation;
			double v2 = 1.0 - input.Saturation * (h - i);
			double v3 = 1.0 - input.Saturation * (1 - (h - i));

			switch (i) {
				case 0:
					output.Red = 255;
					output.Green = (byte) Math.Round(v3 * 255);
					output.Blue = (byte) Math.Round(v1 * 255);
					break;
				
				case 1:
					output.Red = (byte) Math.Round(v2 * 255);
					output.Green = 255;
					output.Blue = (byte) Math.Round(v1 * 255);
					break;
				
				case 2:
					output.Red = (byte) Math.Round(v1 * 255);
					output.Green = 255;
					output.Blue = (byte) Math.Round(v3 * 255);
					break;
				
				case 3:
					output.Red = (byte) Math.Round(v1 * 255);
					output.Green = (byte) Math.Round(v2 * 255);
					output.Blue = 255;
					break;
				
				case 4:
					output.Red = (byte) Math.Round(v3 * 255);
					output.Green = (byte) Math.Round(v1 * 255);
					output.Blue = 255;
					break;
				
				default:
					output.Red = 255;
					output.Green = (byte) Math.Round(v1 * 255);
					output.Blue = (byte) Math.Round(v2 * 255);
					break;
			}

			return output;
		}

		private static bool doubleEquals(double a, double b) {
			return Math.Abs(a - b) < 0.0000001;
		}
	}

	public class RGB
	{
		public byte Red { get; set; }
		public byte Green { get; set; }
		public byte Blue { get; set; }

		public override string ToString() {
			return Red.ToString("X2") + Green.ToString("X2") + Blue.ToString("X2");
		}
	}

	public class HSV
	{
		public double Hue { get; set; }
		public double Saturation { get; set; }
		public double Value { get; set; }
	}
}