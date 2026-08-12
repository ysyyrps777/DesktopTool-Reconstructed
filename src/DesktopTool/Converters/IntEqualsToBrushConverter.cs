using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace DesktopTool.Converters;

public class IntEqualsToBrushConverter : MarkupExtension, IValueConverter
{
	private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(10, 132, byte.MaxValue));

	private static readonly Brush NormalBrush = Brushes.Transparent;

	public override object ProvideValue(IServiceProvider serviceProvider)
	{
		return this;
	}

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is int a && parameter is string ps && int.TryParse(ps, out var b))
		{
			if (a != b)
			{
				return NormalBrush;
			}
			return SelectedBrush;
		}
		return NormalBrush;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}
