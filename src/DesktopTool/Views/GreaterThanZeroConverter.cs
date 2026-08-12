using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DesktopTool.Views;

public class GreaterThanZeroConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is double d && d > 0.0)
		{
			return Visibility.Visible;
		}
		if (value is int i && i > 0)
		{
			return Visibility.Visible;
		}
		return Visibility.Collapsed;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
