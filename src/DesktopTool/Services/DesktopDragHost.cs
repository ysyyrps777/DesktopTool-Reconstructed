using System;
using System.Windows;

namespace DesktopTool.Services;

public sealed class DesktopDragHost : NativeDragHelper.INativeDragHost
{
	private readonly Func<nint> _hwnd;

	private readonly Func<nint> _desktopOwner;

	private readonly Func<double> _minW;

	private readonly Func<double> _minH;

	private readonly UIElement _dragElement;

	private readonly Action _bringToTop;

	private readonly Action<bool> _setSuppressSave;

	private readonly Action<int, int, int, int> _setPosition;

	private readonly Action<int> _onCommitted;

	private readonly Action _onResizeGlowTick;

	private readonly Action _onResizeGlowEnd;

	nint NativeDragHelper.INativeDragHost.Hwnd => _hwnd();

	bool NativeDragHelper.INativeDragHost.IsRealChild => false;

	nint NativeDragHelper.INativeDragHost.DesktopOwner => _desktopOwner();

	UIElement NativeDragHelper.INativeDragHost.DragElement => _dragElement;

	double NativeDragHelper.INativeDragHost.HostMinWidth => _minW();

	double NativeDragHelper.INativeDragHost.HostMinHeight => _minH();

	public DesktopDragHost(Func<nint> hwnd, Func<nint> desktopOwner, Func<double> minW, Func<double> minH, UIElement dragElement, Action bringToTop, Action<bool> setSuppressSave, Action<int, int, int, int> setPosition, Action<int> onCommitted, Action onResizeGlowTick = null, Action onResizeGlowEnd = null)
	{
		_hwnd = hwnd ?? throw new ArgumentNullException("hwnd");
		_desktopOwner = desktopOwner ?? ((Func<nint>)(() => DesktopEmbedService.GetOwnerHwnd(_hwnd())));
		_minW = minW ?? ((Func<double>)(() => 0.0));
		_minH = minH ?? ((Func<double>)(() => 0.0));
		_dragElement = dragElement;
		_bringToTop = bringToTop ?? ((Action)delegate
		{
		});
		_setSuppressSave = setSuppressSave ?? ((Action<bool>)delegate
		{
		});
		_setPosition = setPosition ?? ((Action<int, int, int, int>)delegate
		{
		});
		_onCommitted = onCommitted ?? ((Action<int>)delegate
		{
		});
		_onResizeGlowTick = onResizeGlowTick ?? ((Action)delegate
		{
		});
		_onResizeGlowEnd = onResizeGlowEnd ?? ((Action)delegate
		{
		});
	}

	void NativeDragHelper.INativeDragHost.BringToTop()
	{
		_bringToTop();
	}

	void NativeDragHelper.INativeDragHost.SetSuppressSave(bool suppress)
	{
		_setSuppressSave(suppress);
	}

	void NativeDragHelper.INativeDragHost.SetPosition(int x, int y, int w, int h)
	{
		_setPosition(x, y, w, h);
	}

	void NativeDragHelper.INativeDragHost.OnDragCommitted(int resizeHit)
	{
		_onCommitted(resizeHit);
	}

	void NativeDragHelper.INativeDragHost.OnResizeGlowTick()
	{
		_onResizeGlowTick();
	}

	void NativeDragHelper.INativeDragHost.OnResizeGlowEnd()
	{
		_onResizeGlowEnd();
	}
}
