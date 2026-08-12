using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DesktopTool.Converters;

public class BoolToInverseVisibilityConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		bool b = default(bool);
		int num;
		if (value is bool)
		{
			b = (bool)value;
			num = 1;
		}
		else
		{
			num = 0;
		}
		return (((uint)num & (b ? 1u : 0u)) != 0) ? Visibility.Collapsed : Visibility.Visible;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return value is Visibility v && v != Visibility.Visible;
	}
}
