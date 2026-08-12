using System.Windows;

namespace DesktopTool.Views;

internal static class GridViewSort
{
	public static readonly DependencyProperty SortKeyProperty = DependencyProperty.RegisterAttached("SortKey", typeof(string), typeof(GridViewSort), new PropertyMetadata(string.Empty));

	public static void SetSortKey(DependencyObject element, string value)
	{
		element.SetValue(SortKeyProperty, value);
	}

	public static string GetSortKey(DependencyObject element)
	{
		return ((string)element.GetValue(SortKeyProperty)) ?? string.Empty;
	}
}
