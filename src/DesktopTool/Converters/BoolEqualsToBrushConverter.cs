using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace DesktopTool.Converters;

public class BoolEqualsToBrushConverter : MarkupExtension, IValueConverter
{
	private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(10, 132, byte.MaxValue));

	private static readonly Brush NormalBrush = Brushes.Transparent;

	public override object ProvideValue(IServiceProvider serviceProvider)
	{
		return this;
	}

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is bool a && parameter is string ps)
		{
			bool target = string.Equals(ps, "true", StringComparison.OrdinalIgnoreCase);
			if (a != target)
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
