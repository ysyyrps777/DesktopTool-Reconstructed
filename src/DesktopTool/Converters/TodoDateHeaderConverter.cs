using System;
using System.Globalization;
using System.Windows.Data;

namespace DesktopTool.Converters;

public class TodoDateHeaderConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (!(value is DateTime dt))
		{
			return value?.ToString() ?? string.Empty;
		}
		DateTime today = DateTime.Today;
		string s = dt.ToString("yyyy-MM-dd", culture);
		if (dt.Date == today)
		{
			return s + "  今天";
		}
		if (dt.Date == today.AddDays(-1.0))
		{
			return s + "  昨天";
		}
		if (dt.Date == today.AddDays(1.0))
		{
			return s + "  明天";
		}
		string wd = (new string[7] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" })[(int)dt.DayOfWeek];
		return s + "  " + wd;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}
