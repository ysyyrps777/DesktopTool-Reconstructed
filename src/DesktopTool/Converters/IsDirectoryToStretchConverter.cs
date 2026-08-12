using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DesktopTool.Converters;

public class IsDirectoryToStretchConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		bool isDir = default(bool);
		int num;
		if (value is bool)
		{
			isDir = (bool)value;
			num = 1;
		}
		else
		{
			num = 0;
		}
		if (((uint)num & (isDir ? 1u : 0u)) != 0)
		{
			return Stretch.Fill;
		}
		return Stretch.Uniform;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}
