using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopTool;

public static class TwoLineTextHelper
{
	public static readonly DependencyProperty FullTextProperty = DependencyProperty.RegisterAttached("FullText", typeof(string), typeof(TwoLineTextHelper), new PropertyMetadata(null, OnFullTextChanged));

	public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.RegisterAttached("MaxLines", typeof(int), typeof(TwoLineTextHelper), new PropertyMetadata(2, OnMaxLinesChanged));

	private static readonly DependencyPropertyKey OriginalTextPropertyKey = DependencyProperty.RegisterAttachedReadOnly("OriginalText", typeof(string), typeof(TwoLineTextHelper), new PropertyMetadata(null));

	public static void SetFullText(DependencyObject element, string? value)
	{
		element.SetValue(FullTextProperty, value);
	}

	public static string? GetFullText(DependencyObject element)
	{
		return (string)element.GetValue(FullTextProperty);
	}

	public static void SetMaxLines(DependencyObject element, int value)
	{
		element.SetValue(MaxLinesProperty, value);
	}

	public static int GetMaxLines(DependencyObject element)
	{
		return (int)element.GetValue(MaxLinesProperty);
	}

	private static void OnFullTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is TextBlock tb)
		{
			tb.SetValue(OriginalTextPropertyKey, e.NewValue as string);
			tb.SizeChanged -= OnSizeChanged;
			tb.SizeChanged += OnSizeChanged;
			tb.Loaded -= OnLoaded;
			tb.Loaded += OnLoaded;
			Update(tb);
		}
	}

	private static void OnMaxLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is TextBlock tb)
		{
			Update(tb);
		}
	}

	private static void OnLoaded(object? sender, RoutedEventArgs e)
	{
		if (sender is TextBlock tb)
		{
			Update(tb);
		}
	}

	private static void OnSizeChanged(object? sender, SizeChangedEventArgs e)
	{
		if (sender is TextBlock tb)
		{
			Update(tb);
		}
	}

	private static void Update(TextBlock tb)
	{
		string full = ((string)tb.GetValue(OriginalTextPropertyKey.DependencyProperty)) ?? string.Empty;
		double measureWidth = ResolveMeasureWidth(tb);
		if (measureWidth <= 0.0 || tb.FontSize <= 0.0)
		{
			if (tb.Text != full)
			{
				tb.Text = full;
			}
			return;
		}
		int maxLines = GetMaxLines(tb);
		if (maxLines <= 0)
		{
			if (tb.Text != full)
			{
				tb.Text = full;
			}
			return;
		}
		if (full.Length == 0)
		{
			if (tb.Text != full)
			{
				tb.Text = full;
			}
			return;
		}
		Typeface typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
		double pdi = GetPixelsPerDip(tb);
		double lineHeight = new FormattedText("Mg", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, tb.FontSize, Brushes.Black, pdi).Height;
		if (lineHeight <= 0.0)
		{
			if (tb.Text != full)
			{
				tb.Text = full;
			}
			return;
		}
		double maxHeight = (tb.MaxHeight = lineHeight * (double)maxLines);
		tb.TextWrapping = TextWrapping.Wrap;
		if (new FormattedText(full, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, tb.FontSize, Brushes.Black, pdi)
		{
			MaxTextWidth = measureWidth
		}.Height <= maxHeight + 0.5)
		{
			if (tb.Text != full)
			{
				tb.Text = full;
			}
			return;
		}
		int left = 1;
		int right = full.Length;
		int best = 1;
		while (left <= right)
		{
			int mid = (left + right) / 2;
			if (Fits(typeface, pdi, full.Substring(0, mid) + "…", measureWidth, maxHeight - 0.5, tb.FontSize))
			{
				best = mid;
				left = mid + 1;
			}
			else
			{
				right = mid - 1;
			}
		}
		string result = full.Substring(0, best) + "…";
		if (tb.Text != result)
		{
			tb.Text = result;
		}
	}

	private static bool Fits(Typeface typeface, double pdi, string text, double width, double maxHeight, double fontSize)
	{
		return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, pdi)
		{
			MaxTextWidth = width
		}.Height <= maxHeight + 0.5;
	}

	private static double ResolveMeasureWidth(TextBlock tb)
	{
		double max = tb.MaxWidth;
		if (max > 0.0 && !double.IsInfinity(max) && !double.IsNaN(max))
		{
			return max;
		}
		if (tb.Parent is FrameworkElement { ActualWidth: >0.0 } fe && !double.IsInfinity(fe.ActualWidth))
		{
			return fe.ActualWidth;
		}
		return tb.ActualWidth;
	}

	private static double GetPixelsPerDip(TextBlock tb)
	{
		try
		{
			return VisualTreeHelper.GetDpi(tb).PixelsPerDip;
		}
		catch
		{
			return 1.0;
		}
	}
}
