using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;

namespace DesktopTool.Services;

public static class WindowSnapService
{
	[Flags]
	public enum EdgeMask
	{
		None = 0,
		Left = 1,
		Top = 2,
		Right = 4,
		Bottom = 8,
		All = 0xF
	}

	public const int AlignThreshold = 28;

	public const int GlowThreshold = 10;

	public const int ProximityThreshold = 40;

	private static readonly Dictionary<nint, Func<Rect>> _windows = new Dictionary<nint, Func<Rect>>();

	public static EdgeMask HitToEdgeMask(int resizeHit)
	{
		return resizeHit switch
		{
			10 => EdgeMask.Left, 
			11 => EdgeMask.Right, 
			12 => EdgeMask.Top, 
			15 => EdgeMask.Bottom, 
			13 => EdgeMask.Left | EdgeMask.Top, 
			14 => EdgeMask.Top | EdgeMask.Right, 
			16 => EdgeMask.Left | EdgeMask.Bottom, 
			17 => EdgeMask.Right | EdgeMask.Bottom, 
			_ => EdgeMask.None, 
		};
	}

	public static void Register(nint hwnd, Func<Rect> getBounds)
	{
		_windows[hwnd] = getBounds;
	}

	public static void Unregister(nint hwnd)
	{
		_windows.Remove(hwnd);
	}

	public static IEnumerable<(nint hwnd, Rect rect)> GetSiblings(nint selfHwnd)
	{
		foreach (KeyValuePair<nint, Func<Rect>> kv in _windows)
		{
			if (kv.Key != selfHwnd)
			{
				yield return (hwnd: kv.Key, rect: kv.Value());
			}
		}
	}

	private static bool VOverlap(Rect a, Rect b)
	{
		if (a.Top <= b.Bottom + 10.0)
		{
			return b.Top <= a.Bottom + 10.0;
		}
		return false;
	}

	private static bool HOverlap(Rect a, Rect b)
	{
		if (a.Left <= b.Right + 10.0)
		{
			return b.Left <= a.Right + 10.0;
		}
		return false;
	}

	public static (EdgeMask selfEdges, Dictionary<nint, EdgeMask> siblingEdges) DetectAlignment(nint selfHwnd, Rect my, EdgeMask draggedEdges)
	{
		EdgeMask self = EdgeMask.None;
		Dictionary<nint, EdgeMask> siblingEdges = new Dictionary<nint, EdgeMask>();
		double t = 10.0;
		foreach (var sibling in GetSiblings(selfHwnd))
		{
			nint hwnd = sibling.hwnd;
			Rect rect = sibling.rect;
			EdgeMask edges = EdgeMask.None;
			if (draggedEdges.HasFlag(EdgeMask.Left) && Math.Abs(my.Left - rect.Left) <= t && VOverlap(my, rect))
			{
				self |= EdgeMask.Left;
				edges |= EdgeMask.Left;
			}
			if (draggedEdges.HasFlag(EdgeMask.Top) && Math.Abs(my.Top - rect.Top) <= t && HOverlap(my, rect))
			{
				self |= EdgeMask.Top;
				edges |= EdgeMask.Top;
			}
			if (draggedEdges.HasFlag(EdgeMask.Right) && Math.Abs(my.Right - rect.Right) <= t && VOverlap(my, rect))
			{
				self |= EdgeMask.Right;
				edges |= EdgeMask.Right;
			}
			if (draggedEdges.HasFlag(EdgeMask.Bottom) && Math.Abs(my.Bottom - rect.Bottom) <= t && HOverlap(my, rect))
			{
				self |= EdgeMask.Bottom;
				edges |= EdgeMask.Bottom;
			}
			if (edges != EdgeMask.None)
			{
				siblingEdges[hwnd] = edges;
			}
		}
		return (selfEdges: self, siblingEdges: siblingEdges);
	}

	public static bool TryResizeSnap(nint selfHwnd, Window window, EdgeMask draggedEdges)
	{
		if (_windows.Count <= 1 || draggedEdges == EdgeMask.None)
		{
			return false;
		}
		double t = 10.0;
		double left = window.Left;
		double top = window.Top;
		double w = window.Width;
		double h = window.Height;
		double right = left + w;
		double bottom = top + h;
		double minW = ((window.MinWidth > 0.0) ? window.MinWidth : 1.0);
		double minH = ((window.MinHeight > 0.0) ? window.MinHeight : 1.0);
		bool changed = false;
		Rect myRect = new Rect(left, top, w, h);
		if (draggedEdges.HasFlag(EdgeMask.Left))
		{
			double best = t;
			double target = double.NaN;
			foreach (var sibling in GetSiblings(selfHwnd))
			{
				Rect r = sibling.rect;
				if (VOverlap(myRect, r))
				{
					double d = Math.Abs(left - r.Left);
					if (d <= best)
					{
						best = d;
						target = r.Left;
					}
				}
			}
			if (!double.IsNaN(target))
			{
				double nw = right - target;
				if (nw >= minW)
				{
					left = target;
					w = nw;
					changed = true;
				}
			}
		}
		if (draggedEdges.HasFlag(EdgeMask.Right))
		{
			double best2 = t;
			double target2 = double.NaN;
			foreach (var sibling2 in GetSiblings(selfHwnd))
			{
				Rect r2 = sibling2.rect;
				if (VOverlap(myRect, r2))
				{
					double d2 = Math.Abs(right - r2.Right);
					if (d2 <= best2)
					{
						best2 = d2;
						target2 = r2.Right;
					}
				}
			}
			if (!double.IsNaN(target2))
			{
				double nw2 = target2 - left;
				if (nw2 >= minW)
				{
					w = nw2;
					changed = true;
				}
			}
		}
		if (draggedEdges.HasFlag(EdgeMask.Top))
		{
			double best3 = t;
			double target3 = double.NaN;
			foreach (var sibling3 in GetSiblings(selfHwnd))
			{
				Rect r3 = sibling3.rect;
				if (HOverlap(myRect, r3))
				{
					double d3 = Math.Abs(top - r3.Top);
					if (d3 <= best3)
					{
						best3 = d3;
						target3 = r3.Top;
					}
				}
			}
			if (!double.IsNaN(target3))
			{
				double nh = bottom - target3;
				if (nh >= minH)
				{
					top = target3;
					h = nh;
					changed = true;
				}
			}
		}
		if (draggedEdges.HasFlag(EdgeMask.Bottom))
		{
			double best4 = t;
			double target4 = double.NaN;
			foreach (var sibling4 in GetSiblings(selfHwnd))
			{
				Rect r4 = sibling4.rect;
				if (HOverlap(myRect, r4))
				{
					double d4 = Math.Abs(bottom - r4.Bottom);
					if (d4 <= best4)
					{
						best4 = d4;
						target4 = r4.Bottom;
					}
				}
			}
			if (!double.IsNaN(target4))
			{
				double nh2 = target4 - top;
				if (nh2 >= minH)
				{
					h = nh2;
					changed = true;
				}
			}
		}
		if (changed)
		{
			window.Left = left;
			window.Top = top;
			window.Width = w;
			window.Height = h;
		}
		return changed;
	}

	public static bool TryResizeSnap(nint selfHwnd, Rect currentBounds, EdgeMask draggedEdges, double minWidth, double minHeight, out Rect adjusted)
	{
		adjusted = currentBounds;
		if (_windows.Count <= 1 || draggedEdges == EdgeMask.None)
		{
			return false;
		}
		double t = 10.0;
		double left = currentBounds.Left;
		double top = currentBounds.Top;
		double right = currentBounds.Right;
		double bottom = currentBounds.Bottom;
		minWidth = Math.Max(minWidth, 1.0);
		minHeight = Math.Max(minHeight, 1.0);
		bool changed = false;
		Rect myRect = currentBounds;
		if (draggedEdges.HasFlag(EdgeMask.Left))
		{
			double best = t;
			double target = double.NaN;
			foreach (var sibling in GetSiblings(selfHwnd))
			{
				Rect r = sibling.rect;
				if (VOverlap(myRect, r))
				{
					double d = Math.Abs(left - r.Left);
					if (d <= best)
					{
						best = d;
						target = r.Left;
					}
				}
			}
			if (!double.IsNaN(target) && right - target >= minWidth)
			{
				left = target;
				changed = true;
			}
		}
		if (draggedEdges.HasFlag(EdgeMask.Right))
		{
			double best2 = t;
			double target2 = double.NaN;
			foreach (var sibling2 in GetSiblings(selfHwnd))
			{
				Rect r2 = sibling2.rect;
				if (VOverlap(myRect, r2))
				{
					double d2 = Math.Abs(right - r2.Right);
					if (d2 <= best2)
					{
						best2 = d2;
						target2 = r2.Right;
					}
				}
			}
			if (!double.IsNaN(target2) && target2 - left >= minWidth)
			{
				right = target2;
				changed = true;
			}
		}
		if (draggedEdges.HasFlag(EdgeMask.Top))
		{
			double best3 = t;
			double target3 = double.NaN;
			foreach (var sibling3 in GetSiblings(selfHwnd))
			{
				Rect r3 = sibling3.rect;
				if (HOverlap(myRect, r3))
				{
					double d3 = Math.Abs(top - r3.Top);
					if (d3 <= best3)
					{
						best3 = d3;
						target3 = r3.Top;
					}
				}
			}
			if (!double.IsNaN(target3) && bottom - target3 >= minHeight)
			{
				top = target3;
				changed = true;
			}
		}
		if (draggedEdges.HasFlag(EdgeMask.Bottom))
		{
			double best4 = t;
			double target4 = double.NaN;
			foreach (var sibling4 in GetSiblings(selfHwnd))
			{
				Rect r4 = sibling4.rect;
				if (HOverlap(myRect, r4))
				{
					double d4 = Math.Abs(bottom - r4.Bottom);
					if (d4 <= best4)
					{
						best4 = d4;
						target4 = r4.Bottom;
					}
				}
			}
			if (!double.IsNaN(target4) && target4 - top >= minHeight)
			{
				bottom = target4;
				changed = true;
			}
		}
		if (changed)
		{
			adjusted = new Rect(left, top, right - left, bottom - top);
		}
		return changed;
	}

	public static bool TryDockAlign(nint selfHwnd, Window window)
	{
		Rect my = new Rect(window.Left, window.Top, window.Width, window.Height);
		NativeMethods.GetCursorPos(out var rawCur);
		double dC = 1.0;
		nint mC = NativeMethods.MonitorFromWindow(selfHwnd, 2u);
		if (mC != IntPtr.Zero)
		{
			NativeMethods.GetDpiForMonitor(mC, 0, out var dxC, out var _);
			if (dxC != 0)
			{
				dC = (double)dxC / 96.0;
			}
		}
		Point? cursor = new Point((double)rawCur.X / dC, (double)rawCur.Y / dC);
		if (!TryDockAlign(selfHwnd, my, cursor, out var adjusted))
		{
			return false;
		}
		window.Left = adjusted.Left;
		window.Top = adjusted.Top;
		return true;
	}

	public static bool TryDockAlign(nint selfHwnd, Rect selfBounds, out Rect adjusted)
	{
		return TryDockAlign(selfHwnd, selfBounds, null, out adjusted);
	}

	public static bool TryDockAlign(nint selfHwnd, Rect selfBounds, Point? cursor, out Rect adjusted)
	{
		adjusted = selfBounds;
		if (_windows.Count <= 1)
		{
			return false;
		}
		Rect my = selfBounds;
		bool aligned = false;
		NativeMethods.RECT selfRect = new NativeMethods.RECT
		{
			Left = (int)Math.Round(my.X + my.Width / 2.0),
			Top = (int)Math.Round(my.Y + my.Height / 2.0),
			Right = (int)Math.Round(my.X + my.Width / 2.0),
			Bottom = (int)Math.Round(my.Y + my.Height / 2.0)
		};
		nint selfMon = NativeMethods.MonitorFromRect(ref selfRect, 2u);
		Rect? bestTarget = null;
		bool dockToLeft = false;
		double bestScore = double.MaxValue;
		double triggerA = 28.0;
		double overlapMax = triggerA * 2.0;
		nint key;
		Func<Rect> value;
		foreach (KeyValuePair<nint, Func<Rect>> window in _windows)
		{
			window.Deconstruct(out key, out value);
			nint num = key;
			Func<Rect> getBounds = value;
			if (num == selfHwnd)
			{
				continue;
			}
			Rect t = getBounds();
			if (selfMon != IntPtr.Zero)
			{
				NativeMethods.RECT tCenter = new NativeMethods.RECT
				{
					Left = (int)Math.Round(t.X + t.Width / 2.0),
					Top = (int)Math.Round(t.Y + t.Height / 2.0),
					Right = (int)Math.Round(t.X + t.Width / 2.0),
					Bottom = (int)Math.Round(t.Y + t.Height / 2.0)
				};
				if (NativeMethods.MonitorFromRect(ref tCenter, 2u) != selfMon)
				{
					continue;
				}
			}
			if (!VOverlap(my, t) || !(Math.Min(my.Bottom, t.Bottom) - Math.Max(my.Top, t.Top) >= Math.Min(my.Height, t.Height) * 0.5))
			{
				continue;
			}
			double gapA = t.Left - my.Right;
			if (cursor.HasValue && double.IsFinite(cursor.Value.X) && gapA > 0.0)
			{
				double ga = t.Left - cursor.Value.X;
				if (Math.Abs(ga) < Math.Abs(gapA))
				{
					gapA = ga;
				}
			}
			if (gapA <= triggerA && gapA >= 0.0 - overlapMax && Math.Abs(gapA) < bestScore)
			{
				bestScore = Math.Abs(gapA);
				bestTarget = t;
				dockToLeft = true;
			}
			double gapB = my.Left - t.Right;
			if (cursor.HasValue && double.IsFinite(cursor.Value.X) && gapB > 0.0)
			{
				double gb = cursor.Value.X - t.Right;
				if (Math.Abs(gb) < Math.Abs(gapB))
				{
					gapB = gb;
				}
			}
			if (gapB <= triggerA && gapB >= 0.0 - overlapMax && Math.Abs(gapB) < bestScore)
			{
				bestScore = Math.Abs(gapB);
				bestTarget = t;
				dockToLeft = false;
			}
		}
		if (bestTarget.HasValue)
		{
			Rect target = bestTarget.Value;
			double num2 = Math.Abs(my.Top - target.Top);
			double botClose = Math.Abs(my.Bottom - target.Bottom);
			double newTop = ((num2 <= botClose) ? target.Top : (target.Bottom - my.Height));
			adjusted = new Rect(dockToLeft ? (target.Left - my.Width) : target.Right, newTop, my.Width, my.Height);
			aligned = true;
		}
		if (!aligned)
		{
			Rect? bestTarget2 = null;
			bool aboveTarget = false;
			double bestScore2 = double.MaxValue;
			foreach (KeyValuePair<nint, Func<Rect>> window2 in _windows)
			{
				window2.Deconstruct(out key, out value);
				nint num3 = key;
				Func<Rect> getBounds2 = value;
				if (num3 == selfHwnd)
				{
					continue;
				}
				Rect t2 = getBounds2();
				if (selfMon != IntPtr.Zero)
				{
					NativeMethods.RECT tCenter2 = new NativeMethods.RECT
					{
						Left = (int)Math.Round(t2.X + t2.Width / 2.0),
						Top = (int)Math.Round(t2.Y + t2.Height / 2.0),
						Right = (int)Math.Round(t2.X + t2.Width / 2.0),
						Bottom = (int)Math.Round(t2.Y + t2.Height / 2.0)
					};
					if (NativeMethods.MonitorFromRect(ref tCenter2, 2u) != selfMon)
					{
						continue;
					}
				}
				if (!HOverlap(my, t2))
				{
					continue;
				}
				bool hEnough = Math.Min(my.Right, t2.Right) - Math.Max(my.Left, t2.Left) >= Math.Min(my.Width, t2.Width) * 0.5;
				if (!hEnough)
				{
					continue;
				}
				double num4 = Math.Abs(my.Left - t2.Left);
				double rightClose = Math.Abs(my.Right - t2.Right);
				if (!(num4 > 28.0) || !(rightClose > 28.0))
				{
					double dAbove = my.Bottom - t2.Top;
					if (Math.Abs(dAbove) <= 28.0 && hEnough && Math.Abs(dAbove) < bestScore2)
					{
						bestScore2 = Math.Abs(dAbove);
						bestTarget2 = t2;
						aboveTarget = true;
					}
					double dBelow = t2.Bottom - my.Top;
					if (Math.Abs(dBelow) <= 28.0 && hEnough && Math.Abs(dBelow) < bestScore2)
					{
						bestScore2 = Math.Abs(dBelow);
						bestTarget2 = t2;
						aboveTarget = false;
					}
				}
			}
			if (bestTarget2.HasValue)
			{
				Rect target2 = bestTarget2.Value;
				double num5 = Math.Abs(my.Left - target2.Left);
				double rightClose2 = Math.Abs(my.Right - target2.Right);
				double newLeft = ((num5 <= rightClose2) ? target2.Left : (target2.Right - my.Width));
				double newTop2 = (aboveTarget ? (target2.Top - my.Height) : target2.Bottom);
				adjusted = new Rect(newLeft, newTop2, my.Width, my.Height);
				aligned = true;
			}
		}
		if (!aligned)
		{
			foreach (KeyValuePair<nint, Func<Rect>> window3 in _windows)
			{
				window3.Deconstruct(out key, out value);
				nint num6 = key;
				Func<Rect> getBounds3 = value;
				if (num6 == selfHwnd)
				{
					continue;
				}
				Rect t3 = getBounds3();
				if (selfMon != IntPtr.Zero)
				{
					NativeMethods.RECT tCenter3 = new NativeMethods.RECT
					{
						Left = (int)Math.Round(t3.X + t3.Width / 2.0),
						Top = (int)Math.Round(t3.Y + t3.Height / 2.0),
						Right = (int)Math.Round(t3.X + t3.Width / 2.0),
						Bottom = (int)Math.Round(t3.Y + t3.Height / 2.0)
					};
					if (NativeMethods.MonitorFromRect(ref tCenter3, 2u) != selfMon)
					{
						continue;
					}
				}
				if (VOverlap(my, t3) && HOverlap(my, t3) && Math.Abs(my.Bottom - t3.Bottom) <= 28.0)
				{
					adjusted = new Rect(my.Left, t3.Bottom - my.Height, my.Width, my.Height);
					aligned = true;
					break;
				}
			}
		}
		if (!aligned)
		{
			foreach (KeyValuePair<nint, Func<Rect>> window4 in _windows)
			{
				window4.Deconstruct(out key, out value);
				nint num7 = key;
				Func<Rect> getBounds4 = value;
				if (num7 == selfHwnd)
				{
					continue;
				}
				Rect t4 = getBounds4();
				if (selfMon != IntPtr.Zero)
				{
					NativeMethods.RECT tCenter4 = new NativeMethods.RECT
					{
						Left = (int)Math.Round(t4.X + t4.Width / 2.0),
						Top = (int)Math.Round(t4.Y + t4.Height / 2.0),
						Right = (int)Math.Round(t4.X + t4.Width / 2.0),
						Bottom = (int)Math.Round(t4.Y + t4.Height / 2.0)
					};
					if (NativeMethods.MonitorFromRect(ref tCenter4, 2u) != selfMon)
					{
						continue;
					}
				}
				if (VOverlap(my, t4) && Math.Abs(my.Left - t4.Left) <= 28.0)
				{
					adjusted = new Rect(t4.Left, my.Top, my.Width, my.Height);
					aligned = true;
					break;
				}
			}
		}
		if (!aligned)
		{
			foreach (KeyValuePair<nint, Func<Rect>> window5 in _windows)
			{
				window5.Deconstruct(out key, out value);
				nint num8 = key;
				Func<Rect> getBounds5 = value;
				if (num8 == selfHwnd)
				{
					continue;
				}
				Rect t5 = getBounds5();
				if (selfMon != IntPtr.Zero)
				{
					NativeMethods.RECT tCenter5 = new NativeMethods.RECT
					{
						Left = (int)Math.Round(t5.X + t5.Width / 2.0),
						Top = (int)Math.Round(t5.Y + t5.Height / 2.0),
						Right = (int)Math.Round(t5.X + t5.Width / 2.0),
						Bottom = (int)Math.Round(t5.Y + t5.Height / 2.0)
					};
					if (NativeMethods.MonitorFromRect(ref tCenter5, 2u) != selfMon)
					{
						continue;
					}
				}
				if (VOverlap(my, t5) && Math.Abs(my.Right - t5.Right) <= 28.0)
				{
					adjusted = new Rect(t5.Right - my.Width, my.Top, my.Width, my.Height);
					aligned = true;
					break;
				}
			}
		}
		return aligned;
	}

	public static string Diagnose(nint selfHwnd)
	{
		StringBuilder sb = new StringBuilder();
		StringBuilder stringBuilder = sb;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(21, 2, stringBuilder);
		handler.AppendLiteral("[诊断-开始] hwnd=0x");
		handler.AppendFormatted(selfHwnd, "X8");
		handler.AppendLiteral(" 注册窗数=");
		handler.AppendFormatted(_windows.Count);
		stringBuilder2.AppendLine(ref handler);
		if (_windows.Count <= 1)
		{
			sb.AppendLine("[诊断-结束] 注册窗数<=1，无兄弟可吸");
			return sb.ToString();
		}
		Rect my = NativeDragHelper.GetDipRect(selfHwnd);
		stringBuilder = sb;
		StringBuilder stringBuilder3 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(18, 4, stringBuilder);
		handler.AppendLiteral("[诊断] selfDip=(");
		handler.AppendFormatted(my.X, "F0");
		handler.AppendLiteral(",");
		handler.AppendFormatted(my.Y, "F0");
		handler.AppendLiteral(",");
		handler.AppendFormatted(my.Width, "F0");
		handler.AppendLiteral(",");
		handler.AppendFormatted(my.Height, "F0");
		handler.AppendLiteral(")");
		stringBuilder3.AppendLine(ref handler);
		Rect adj;
		bool docked = TryDockAlign(selfHwnd, my, null, out adj);
		stringBuilder = sb;
		StringBuilder stringBuilder4 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(22, 5, stringBuilder);
		handler.AppendLiteral("[诊断] docked=");
		handler.AppendFormatted(docked);
		handler.AppendLiteral(" adj=(");
		handler.AppendFormatted(adj.X, "F0");
		handler.AppendLiteral(",");
		handler.AppendFormatted(adj.Y, "F0");
		handler.AppendLiteral(",");
		handler.AppendFormatted(adj.Width, "F0");
		handler.AppendLiteral(",");
		handler.AppendFormatted(adj.Height, "F0");
		handler.AppendLiteral(")");
		stringBuilder4.AppendLine(ref handler);
		foreach (var (hwnd, getBounds) in _windows)
		{
			if (hwnd != selfHwnd)
			{
				Rect t = getBounds();
				double gapA = t.Left - my.Right;
				double gapB = my.Left - t.Right;
				double gapC = my.Bottom - t.Top;
				double gapD = t.Bottom - my.Top;
				bool vOverlap = VOverlap(my, t);
				bool hOverlap = HOverlap(my, t);
				stringBuilder = sb;
				StringBuilder stringBuilder5 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(67, 11, stringBuilder);
				handler.AppendLiteral("[诊断] sib=0x");
				handler.AppendFormatted(hwnd, "X8");
				handler.AppendLiteral(" tRect=(");
				handler.AppendFormatted(t.X, "F0");
				handler.AppendLiteral(",");
				handler.AppendFormatted(t.Y, "F0");
				handler.AppendLiteral(",");
				handler.AppendFormatted(t.Width, "F0");
				handler.AppendLiteral(",");
				handler.AppendFormatted(t.Height, "F0");
				handler.AppendLiteral(") gapA=");
				handler.AppendFormatted(gapA, "F0");
				handler.AppendLiteral(" gapB=");
				handler.AppendFormatted(gapB, "F0");
				handler.AppendLiteral(" gapC=");
				handler.AppendFormatted(gapC, "F0");
				handler.AppendLiteral(" gapD=");
				handler.AppendFormatted(gapD, "F0");
				handler.AppendLiteral(" VOverlap=");
				handler.AppendFormatted(vOverlap);
				handler.AppendLiteral(" HOverlap=");
				handler.AppendFormatted(hOverlap);
				stringBuilder5.AppendLine(ref handler);
			}
		}
		sb.AppendLine("[诊断-结束]");
		return sb.ToString();
	}

	public static void LogDiagnose(nint selfHwnd)
	{
		try
		{
			File.AppendAllText(Path.Combine(App.GetAppDataDir(), "debug_drag.log"), $"{DateTime.Now:HH:mm:ss.fff} {Diagnose(selfHwnd)}{Environment.NewLine}");
		}
		catch
		{
		}
	}
}
