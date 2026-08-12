using System;
using System.Globalization;
using System.Text;
using System.Windows.Data;

namespace DesktopTool.Converters;

[ValueConversion(typeof(string), typeof(string))]
public class LetterSpacingConverter : IValueConverter
{
	private const string CnSpace = "\u200a";

	private const string OtherSpace = "\u200b";

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (!(value is string s) || string.IsNullOrEmpty(s))
		{
			return value;
		}
		StringBuilder sb = new StringBuilder(s.Length * 2);
		for (int i = 0; i < s.Length; i++)
		{
			sb.Append(s[i]);
			if (i < s.Length - 1 && s[i] != ' ' && s[i + 1] != ' ' && s[i] != '\r' && s[i] != '\n' && s[i + 1] != '\r' && s[i + 1] != '\n')
			{
				bool curCn = IsChinese(s[i]);
				bool nextCn = IsChinese(s[i + 1]);
				sb.Append((curCn && nextCn) ? "\u200a" : "\u200b");
			}
		}
		return sb.ToString();
	}

	private static bool IsChinese(char c)
	{
		if (c < '一' || c > '鿿')
		{
			if (c >= '㐀')
			{
				return c <= '䶿';
			}
			return false;
		}
		return true;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return value;
	}
}
