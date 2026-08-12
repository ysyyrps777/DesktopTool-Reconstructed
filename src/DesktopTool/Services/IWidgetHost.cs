using System;
using System.Windows;

namespace DesktopTool.Services;

public interface IWidgetHost
{
	nint HostHwnd { get; }

	double Left { get; set; }

	double Top { get; set; }

	double Width { get; set; }

	double Height { get; set; }

	bool IsVisible { get; }

	double DpiScaleY { get; }

	Window? OwnerForDialogs { get; }

	bool AllowMouseActivate { get; set; }

	event EventHandler LocationChanged;

	event EventHandler SizeChanged;

	event EventHandler Deactivated;

	event EventHandler Closed;

	void Show();

	void Hide();

	void Close();

	void SetBounds(double left, double top, double width, double height);

	void SetBoundsScreen(double screenLeft, double screenTop, double width, double height);

	void ResizeOnly(double width, double height);

	void ForceTopMost();

	void BringToTop();
}
