using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace DesktopTool.Converters;

public class MappingTabActiveConverter : MarkupExtension, IMultiValueConverter
{
	public override object ProvideValue(IServiceProvider serviceProvider)
	{
		return this;
	}

	public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
	{
		if (values.Length >= 2)
		{
			return values[0] == values[1];
		}
		return false;
	}

	public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}
