using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DesktopTool.Models;
using DesktopTool.Services;
using DesktopTool.ViewModels;

namespace DesktopTool.Views;

public class MappingWindow
{
	private readonly MappingZone _zone;

	private readonly MappingWindowViewModel _vm;

	private MappingWidget _widget;

	private DesktopWidgetHost _host;

	private bool _isMinimized;

	private const double CollapsedHostHeight = 52.0;

	public string ZoneId => _zone.Id;

	public Dispatcher Dispatcher => Application.Current.Dispatcher;

	public double Left
	{
		get
		{
			return _host.Left;
		}
		set
		{
			_host.Left = value;
		}
	}

	public double Top
	{
		get
		{
			return _host.Top;
		}
		set
		{
			_host.Top = value;
		}
	}

	public double Width
	{
		get
		{
			return _host.Width;
		}
		set
		{
			_host.Width = value;
		}
	}

	public double Height
	{
		get
		{
			return _host.Height;
		}
		set
		{
			_host.Height = value;
		}
	}

	public WindowState WindowState
	{
		get
		{
			if (!_isMinimized)
			{
				return WindowState.Normal;
			}
			return WindowState.Minimized;
		}
		set
		{
			if (value == WindowState.Minimized)
			{
				_isMinimized = true;
				_host.Hide();
			}
			else
			{
				_isMinimized = false;
				_host.Show();
			}
		}
	}

	public bool IsVisible => _widget?.IsVisible ?? false;

	public event EventHandler Closed
	{
		add
		{
			_host.Closed += value;
		}
		remove
		{
			_host.Closed -= value;
		}
	}

	public MappingWindow(MappingZone zone, MainViewModel mainVm)
	{
		LogDiagnostic($"[MappingWindow] 构造开始 zone={zone.Name} x={zone.X:F0} y={zone.Y:F0} w={zone.Width:F0} h={zone.Height:F0}");
		_zone = zone;
		_vm = new MappingWindowViewModel(zone, mainVm);
		_widget = new MappingWidget(_vm, this);
		LogDiagnostic("[MappingWindow] MappingWidget 创建完成");
		_host = new DesktopWidgetHost(_widget, zone.X, zone.Y, zone.Width, zone.Height);
		LogDiagnostic($"[MappingWindow] DesktopWidgetHost 创建完成 hwnd=0x{_host.HostHwnd:X8}");
		_widget.AttachHost(_host);
		_widget.Initialize();
		if (_zone.IsCollapsed)
		{
			_host.Height = 52.0 * _host.DpiScaleY;
		}
		LogDiagnostic("[MappingWindow] 构造完成 zone=" + zone.Name);
	}

	private static void LogDiagnostic(string message)
	{
		LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_startup.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
	}

	private void RebuildHost()
	{
		try
		{
			_widget.Cleanup();
		}
		catch
		{
		}
		try
		{
			_host.Close(raiseClosed: false);
		}
		catch
		{
		}
		MappingWidget w = new MappingWidget(_vm, this);
		double initH = ((_vm.WindowHeight <= 57.0) ? 360.0 : _vm.WindowHeight);
		DesktopWidgetHost h = new DesktopWidgetHost(w, _vm.WindowLeft, _vm.WindowTop, _vm.WindowWidth, initH);
		w.AttachHost(h);
		w.Initialize();
		_widget = w;
		_host = h;
	}

	public void Show()
	{
		_host.Show();
	}

	public void Activate()
	{
		try
		{
			_host.Show();
		}
		catch
		{
		}
	}

	public void ForceTopMost()
	{
		try
		{
			_host.ForceTopMost();
		}
		catch
		{
		}
	}

	public void Close()
	{
		_host.Close();
	}

	public void CloseMappingZone()
	{
		try
		{
			_vm.MainVm.DeleteMappingZone(_zone);
		}
		catch
		{
		}
		Close();
	}

	public void RefreshAll()
	{
		_widget.RefreshAll();
	}

	public void RefreshVisualStyle()
	{
		_widget.RefreshVisualStyle();
	}

	public void ResetToScreenCenter()
	{
		var (left, top) = DesktopEmbedService.GetPrimaryScreenCenter(360.0, 420.0);
		_host.SetBounds(left, top, 360.0, 420.0);
		_vm.WindowLeft = left;
		_vm.WindowTop = top;
		_vm.WindowWidth = 360.0;
		_vm.WindowHeight = 420.0;
		_vm.MainVm.SaveMappings();
	}
}
