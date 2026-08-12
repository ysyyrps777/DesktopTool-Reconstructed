using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopTool.Services;

public static class ToastNotifier
{
	private static readonly List<Window> ActiveToasts = new List<Window>();

	public static Action? OnActivate { get; set; }

	public static void Show(string title, string message, string? time = null)
	{
		Application.Current?.Dispatcher.Invoke(delegate
		{
			Window window = CreateToast(title, message, time);
			window.Loaded += delegate
			{
				LayoutToasts();
			};
			ActiveToasts.Add(window);
			LayoutToasts();
			window.Show();
		});
	}

	private static Window CreateToast(string title, string message, string? time)
	{
		Border border = new Border
		{
			Background = new SolidColorBrush(Color.FromRgb(43, 43, 43)),
			BorderBrush = new SolidColorBrush(Color.FromRgb(59, 59, 59)),
			BorderThickness = new Thickness(1.0, 1.0, 1.0, 1.0),
			CornerRadius = new CornerRadius(8.0),
			Padding = new Thickness(14.0, 12.0, 14.0, 12.0),
			MaxWidth = 320.0
		};
		Grid grid = new Grid();
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		if (!string.IsNullOrEmpty(time))
		{
			grid.RowDefinitions.Add(new RowDefinition
			{
				Height = GridLength.Auto
			});
		}
		Grid topRow = new Grid();
		topRow.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		topRow.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		TextBlock titleTb = new TextBlock
		{
			Text = title,
			Foreground = Brushes.White,
			FontWeight = FontWeights.SemiBold,
			FontSize = 13.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(titleTb, 0);
		TextBlock closeBtn = new TextBlock
		{
			Text = "✕",
			Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
			FontSize = 12.0,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Right,
			Cursor = Cursors.Hand,
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
			ToolTip = "关闭"
		};
		Grid.SetColumn(closeBtn, 1);
		topRow.Children.Add(titleTb);
		topRow.Children.Add(closeBtn);
		Grid.SetRow(topRow, 0);
		grid.Children.Add(topRow);
		TextBlock msgTb = new TextBlock
		{
			Text = message,
			Foreground = new SolidColorBrush(Color.FromRgb(243, 243, 243)),
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
		};
		Grid.SetRow(msgTb, 1);
		grid.Children.Add(msgTb);
		if (!string.IsNullOrEmpty(time))
		{
			TextBlock timeTb = new TextBlock
			{
				Text = time,
				Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
				FontSize = 11.0,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
			};
			Grid.SetRow(timeTb, 2);
			grid.Children.Add(timeTb);
		}
		border.Child = grid;
		Window win = null;
		closeBtn.MouseLeftButtonUp += delegate
		{
			if (win != null)
			{
				CloseToast(win);
			}
		};
		win = new Window
		{
			WindowStyle = WindowStyle.None,
			AllowsTransparency = true,
			Background = Brushes.Transparent,
			ShowInTaskbar = false,
			Topmost = true,
			ResizeMode = ResizeMode.NoResize,
			ShowActivated = false,
			Width = 340.0,
			SizeToContent = SizeToContent.Height,
			Content = border
		};
		return win;
	}

	private static void CloseToast(Window win)
	{
		if (ActiveToasts.Remove(win))
		{
			LayoutToasts();
		}
		try
		{
			win.Close();
		}
		catch
		{
		}
	}

	private static void LayoutToasts()
	{
		Rect work = SystemParameters.WorkArea;
		double bottom = work.Bottom - 12.0;
		double right = work.Right - 12.0;
		for (int i = 0; i < ActiveToasts.Count; i++)
		{
			Window w = ActiveToasts[i];
			double h = (double.IsNaN(w.Height) ? w.ActualHeight : w.Height);
			if (double.IsNaN(h) || h <= 0.0)
			{
				h = 64.0;
			}
			w.Left = right - w.Width;
			w.Top = bottom - h;
			bottom -= h + 10.0;
		}
	}
}
