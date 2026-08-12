using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DesktopTool.Services;

public static class WidgetAlignGlowService
{
	private struct RECT
	{
		public int left;

		public int top;

		public int right;

		public int bottom;
	}

	private sealed class Entry
	{
		public UIElement? Element;

		public Window? Win;

		public AlignmentGlowAdorner? Adorner;
	}

	private sealed class AlignmentGlowAdorner : Adorner
	{
		private WindowSnapService.EdgeMask _edges;

		public AlignmentGlowAdorner(UIElement adorned)
			: base(adorned)
		{
			base.IsHitTestVisible = false;
		}

		public void SetEdges(WindowSnapService.EdgeMask edges)
		{
			if (_edges != edges)
			{
				_edges = edges;
				InvalidateVisual();
			}
		}

		protected override void OnRender(DrawingContext dc)
		{
			base.OnRender(dc);
			if (_edges != WindowSnapService.EdgeMask.None)
			{
				Size size = base.AdornedElement.RenderSize;
				Color color = Color.FromRgb(79, 195, 247);
				if (_edges.HasFlag(WindowSnapService.EdgeMask.Top))
				{
					DrawGlow(dc, new Point(0.0, 1.0), new Point(size.Width, 1.0), color, 3.0);
				}
				if (_edges.HasFlag(WindowSnapService.EdgeMask.Bottom))
				{
					DrawGlow(dc, new Point(0.0, size.Height - 1.0), new Point(size.Width, size.Height - 1.0), color, 3.0);
				}
				if (_edges.HasFlag(WindowSnapService.EdgeMask.Left))
				{
					DrawGlow(dc, new Point(1.0, 0.0), new Point(1.0, size.Height), color, 3.0);
				}
				if (_edges.HasFlag(WindowSnapService.EdgeMask.Right))
				{
					DrawGlow(dc, new Point(size.Width - 1.0, 0.0), new Point(size.Width - 1.0, size.Height), color, 3.0);
				}
			}
		}

		private static void DrawGlow(DrawingContext dc, Point p1, Point p2, Color c, double w)
		{
			for (int i = 4; i >= 1; i--)
			{
				dc.DrawLine(new Pen(new SolidColorBrush(c)
				{
					Opacity = 0.12 * (double)i
				}, w * (double)i), p1, p2);
			}
			dc.DrawLine(new Pen(new SolidColorBrush(Colors.White)
			{
				Opacity = 0.9
			}, 1.5), p1, p2);
		}
	}

	private static readonly Dictionary<nint, Entry> _map = new Dictionary<nint, Entry>();

	private static readonly HashSet<nint> _lit = new HashSet<nint>();

	private static nint _curHwnd;

	private static WindowSnapService.EdgeMask _curDraggedEdges = WindowSnapService.EdgeMask.None;

	private static WindowSnapService.EdgeMask EdgesFromWmsz(int wmsz)
	{
		return wmsz switch
		{
			1 => WindowSnapService.EdgeMask.Left, 
			2 => WindowSnapService.EdgeMask.Right, 
			3 => WindowSnapService.EdgeMask.Top, 
			4 => WindowSnapService.EdgeMask.Left | WindowSnapService.EdgeMask.Top, 
			5 => WindowSnapService.EdgeMask.Top | WindowSnapService.EdgeMask.Right, 
			6 => WindowSnapService.EdgeMask.Bottom, 
			7 => WindowSnapService.EdgeMask.Left | WindowSnapService.EdgeMask.Bottom, 
			8 => WindowSnapService.EdgeMask.Right | WindowSnapService.EdgeMask.Bottom, 
			_ => WindowSnapService.EdgeMask.None, 
		};
	}

	public static void Register(nint hwnd, Window win)
	{
		if (hwnd != IntPtr.Zero && win != null)
		{
			_map[hwnd] = new Entry
			{
				Element = (win.Content as UIElement),
				Win = win
			};
		}
	}

	public static void Register(nint hwnd, UIElement element)
	{
		if (hwnd != IntPtr.Zero && element != null)
		{
			_map[hwnd] = new Entry
			{
				Element = element,
				Win = (element as Window)
			};
		}
	}

	public static void Unregister(nint hwnd)
	{
		if (_map.TryGetValue(hwnd, out var e))
		{
			if (e.Adorner != null)
			{
				AdornerLayer.GetAdornerLayer(e.Element)?.Remove(e.Adorner);
			}
			_map.Remove(hwnd);
		}
		_lit.Remove(hwnd);
	}

	public static void OnSizing(nint hwnd, nint wParam, nint lParam)
	{
		if (_map.TryGetValue(hwnd, out var e) && e.Element != null)
		{
			RECT r = Marshal.PtrToStructure<RECT>(lParam);
			DpiScale dpi = VisualTreeHelper.GetDpi(e.Element);
			double s = ((dpi.DpiScaleX > 0.0) ? dpi.DpiScaleX : 1.0);
			Rect my = new Rect((double)r.left / s, (double)r.top / s, (double)(r.right - r.left) / s, (double)(r.bottom - r.top) / s);
			WindowSnapService.EdgeMask draggedEdges = EdgesFromWmsz((int)wParam);
			_curHwnd = hwnd;
			_curDraggedEdges = draggedEdges;
			UpdateAlignment(hwnd, my, draggedEdges);
		}
	}

	public static void UpdateAlignment(nint hwnd, Rect myRectDip, WindowSnapService.EdgeMask draggedEdges)
	{
		if (!_map.TryGetValue(hwnd, out var e) || e.Element == null)
		{
			return;
		}
		AlignmentGlowAdorner adorner = EnsureAdorner(e);
		if (adorner == null)
		{
			return;
		}
		(WindowSnapService.EdgeMask selfEdges, Dictionary<nint, WindowSnapService.EdgeMask> siblingEdges) tuple = WindowSnapService.DetectAlignment(hwnd, myRectDip, draggedEdges);
		WindowSnapService.EdgeMask selfEdges = tuple.selfEdges;
		Dictionary<nint, WindowSnapService.EdgeMask> siblingEdges = tuple.siblingEdges;
		HashSet<nint> involved = new HashSet<nint> { hwnd };
		foreach (nint k in siblingEdges.Keys)
		{
			involved.Add(k);
		}
		foreach (nint h in _lit)
		{
			if (!involved.Contains(h) && _map.TryGetValue(h, out var pe) && pe.Adorner != null)
			{
				pe.Adorner.SetEdges(WindowSnapService.EdgeMask.None);
			}
		}
		adorner.SetEdges(selfEdges);
		foreach (KeyValuePair<nint, WindowSnapService.EdgeMask> kv in siblingEdges)
		{
			if (_map.TryGetValue(kv.Key, out var se) && se.Adorner != null)
			{
				se.Adorner.SetEdges(kv.Value);
			}
		}
		_lit.Clear();
		foreach (nint h2 in involved)
		{
			_lit.Add(h2);
		}
	}

	public static void OnExitSizeMove(nint hwnd)
	{
		if (_curHwnd == hwnd && _curDraggedEdges != WindowSnapService.EdgeMask.None && _map.TryGetValue(hwnd, out var e) && e.Win != null)
		{
			try
			{
				WindowSnapService.TryResizeSnap(hwnd, e.Win, _curDraggedEdges);
			}
			catch
			{
			}
		}
		_curHwnd = IntPtr.Zero;
		_curDraggedEdges = WindowSnapService.EdgeMask.None;
		ClearGlows();
	}

	public static void ClearGlows()
	{
		foreach (nint h in _lit)
		{
			if (_map.TryGetValue(h, out var e) && e.Adorner != null)
			{
				e.Adorner.SetEdges(WindowSnapService.EdgeMask.None);
			}
		}
		_lit.Clear();
	}

	private static AlignmentGlowAdorner? EnsureAdorner(Entry e)
	{
		if (e.Adorner != null)
		{
			return e.Adorner;
		}
		if (e.Element == null)
		{
			return null;
		}
		AdornerLayer layer = AdornerLayer.GetAdornerLayer(e.Element);
		if (layer == null)
		{
			return null;
		}
		e.Adorner = new AlignmentGlowAdorner(e.Element);
		layer.Add(e.Adorner);
		return e.Adorner;
	}
}
