using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace DesktopTool.Services;

public sealed class WidgetDragAlignController
{
	public sealed class Config
	{
		public nint Hwnd;

		public Func<nint>? GetLiveHwnd;

		public UIElement GlowTarget;

		public Func<Rect> GetBoundsDip;

		public Action<Rect> SetBoundsDip;

		public Action<int, int, int, int> ApplyDragPosition;

		public Func<double> MinWidth;

		public Func<double> MinHeight;

		public Func<bool> IsLocked;

		public Action<Rect> Save;

		public Action? ResizeGlowTick;

		public Action? ResizeGlowEnd;
	}

	private static readonly string _dragLog = Path.Combine(App.GetAppDataDir(), "debug_drag.log");

	private readonly Config _cfg;

	private readonly DispatcherTimer _snapTimer;

	private int _lastPhysX;

	private int _lastPhysY;

	private bool _disposed;

	private bool _pendingRegister;

	private nint _registeredHwnd = IntPtr.Zero;

	private nint Hwnd()
	{
		if (_cfg.GetLiveHwnd != null)
		{
			nint live = _cfg.GetLiveHwnd();
			if (live != IntPtr.Zero)
			{
				return live;
			}
		}
		return _cfg.Hwnd;
	}

	private void EnsureRegistered()
	{
		nint h = Hwnd();
		if (h != IntPtr.Zero && (_pendingRegister || h != _registeredHwnd))
		{
			if (_registeredHwnd != IntPtr.Zero)
			{
				WindowSnapService.Unregister(_registeredHwnd);
				WidgetAlignGlowService.Unregister(_registeredHwnd);
			}
			WindowSnapService.Register(h, () => _cfg.GetBoundsDip());
			WidgetAlignGlowService.Register(h, _cfg.GlowTarget);
			_registeredHwnd = h;
			_pendingRegister = false;
			DebugLog($"[align-重注册] hwnd=0x{h:X8}");
		}
	}

	public WidgetDragAlignController(Config cfg, Dispatcher dispatcher)
	{
		_cfg = cfg ?? throw new ArgumentNullException("cfg");
		if (cfg.Hwnd == IntPtr.Zero && cfg.GetLiveHwnd == null)
		{
			return;
		}
		EnsureRegistered();
		_snapTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(300.0)
		};
		_snapTimer.Tick += delegate
		{
			_snapTimer.Stop();
			if (!_disposed && Hwnd() != IntPtr.Zero)
			{
				if (!_cfg.IsLocked() && WindowSnapService.TryDockAlign(Hwnd(), _cfg.GetBoundsDip(), out var adjusted))
				{
					_cfg.SetBoundsDip(adjusted);
				}
				_cfg.ResizeGlowEnd?.Invoke();
			}
		};
	}

	public void SetPosition(int x, int y, int w, int h)
	{
		EnsureRegistered();
		if (Hwnd() != IntPtr.Zero)
		{
			_lastPhysX = x;
			_lastPhysY = y;
			_cfg.ApplyDragPosition(x, y, w, h);
		}
	}

	public void OnDragCommitted(int resizeHit)
	{
		EnsureRegistered();
		nint h = Hwnd();
		if (h == IntPtr.Zero)
		{
			DebugLog("[对齐诊断-SKIP] Hwnd=0 (构造时Hwnd=0且实时获取也为0)");
			return;
		}
		_cfg.ResizeGlowEnd?.Invoke();
		Rect cur = _cfg.GetBoundsDip();
		Rect finalRect = cur;
		try
		{
			NativeMethods.RECT rawPhys = default(NativeMethods.RECT);
			NativeMethods.GetWindowRect(h, out rawPhys);
			NativeMethods.GetCursorPos(out var rawCur);
			double d = 1.0;
			nint m = NativeMethods.MonitorFromWindow(h, 2u);
			if (m != IntPtr.Zero)
			{
				NativeMethods.GetDpiForMonitor(m, 0, out var dx, out var _);
				if (dx != 0)
				{
					d = (double)dx / 96.0;
				}
			}
			double rawDipX = (double)rawPhys.Left / d;
			double rawDipY = (double)rawPhys.Top / d;
			DebugLog($"[对齐诊断-真实] hwnd=0x{h:X8} curDip=({cur.X:F0},{cur.Y:F0}) rawPhysDip=({rawDipX:F0},{rawDipY:F0}) 滞后差=({cur.X - rawDipX:F0},{cur.Y - rawDipY:F0}) lastPhys=({_lastPhysX},{_lastPhysY}) cursor=({rawCur.X},{rawCur.Y})");
		}
		catch (Exception ex)
		{
			DebugLog("[对齐诊断-真实-异常] " + ex.Message);
		}
		if (resizeHit != 0)
		{
			WindowSnapService.EdgeMask edges = WindowSnapService.HitToEdgeMask(resizeHit);
			if (WindowSnapService.TryResizeSnap(h, cur, edges, _cfg.MinWidth(), _cfg.MinHeight(), out var snapped))
			{
				finalRect = snapped;
			}
		}
		else
		{
			bool docked = false;
			try
			{
				docked = WindowSnapService.TryDockAlign(cursor: GetCursorDip(), selfHwnd: h, selfBounds: cur, adjusted: out var adj);
				if (docked)
				{
					finalRect = adj;
					DebugLog($"[对齐诊断-结果] hwnd=0x{h:X8} docked={docked} cur=({cur.X:F0},{cur.Y:F0}) adj=({adj.X:F0},{adj.Y:F0}) adjW={adj.Width:F0} 光标位移=({adj.X - cur.X:F0},{adj.Y - cur.Y:F0})");
					try
					{
						WindowSnapService.LogDiagnose(h);
					}
					catch
					{
					}
				}
			}
			catch (Exception ex2)
			{
				DebugLog($"[对齐诊断-异常] hwnd=0x{h:X8} selfDip=({cur.X:F0},{cur.Y:F0}) ex={ex2.GetType().Name}:{ex2.Message}");
				docked = false;
			}
			if (!docked)
			{
				int thr = 28;
				NativeMethods.RECT selfCenter = new NativeMethods.RECT
				{
					Left = (int)Math.Round(cur.X + cur.Width / 2.0),
					Top = (int)Math.Round(cur.Y + cur.Height / 2.0),
					Right = (int)Math.Round(cur.X + cur.Width / 2.0),
					Bottom = (int)Math.Round(cur.Y + cur.Height / 2.0)
				};
				nint selfMonGeo = NativeMethods.MonitorFromRect(ref selfCenter, 2u);
				nint selfMonHwnd = NativeMethods.MonitorFromWindow(h, 2u);
				IEnumerable<(nint, Rect)> sibs = WindowSnapService.GetSiblings(h);
				DebugLog($"[对齐诊断] hwnd=0x{h:X8} selfDip=({cur.X:F0},{cur.Y:F0},{cur.Width:F0},{cur.Height:F0}) 真实阈值={thr} selfMonHwnd=0x{selfMonHwnd:X8} selfMonGeo=0x{selfMonGeo:X8} siblings={sibs.Count()}");
				foreach (var s in sibs)
				{
					Rect t = s.Item2;
					double vThr = 10.0;
					bool pass2nd = cur.Top <= t.Bottom + vThr && t.Top <= cur.Bottom + vThr;
					double gA = t.Left - cur.Right;
					double gB = cur.Left - t.Right;
					double trigA = thr;
					double ovMax = trigA * 2.0;
					bool passGap = (gA <= trigA && gA >= 0.0 - ovMax) || (gB <= trigA && gB >= 0.0 - ovMax);
					NativeMethods.RECT tCenter = new NativeMethods.RECT
					{
						Left = (int)Math.Round(t.X + t.Width / 2.0),
						Top = (int)Math.Round(t.Y + t.Height / 2.0),
						Right = (int)Math.Round(t.X + t.Width / 2.0),
						Bottom = (int)Math.Round(t.Y + t.Height / 2.0)
					};
					nint tMonGeo = NativeMethods.MonitorFromRect(ref tCenter, 2u);
					bool crossSkip = selfMonGeo != IntPtr.Zero && tMonGeo != selfMonGeo;
					DebugLog($"[对齐诊断-细节] sib=0x{s.Item1:X8} tRect=({t.X:F0},{t.Y:F0},{t.Width:F0},{t.Height:F0}) VOverlap={pass2nd} 过第二条件={pass2nd} gapA={gA:F0} gapB={gB:F0} 过Gap={passGap} 跨屏跳过(几何)={crossSkip} tMonGeo=0x{tMonGeo:X8}");
				}
				try
				{
					NativeMethods.GetCursorPos(out var cp);
					Point curDip = GetCursorDip();
					double curGap = double.MaxValue;
					double ptrGap = double.MaxValue;
					double trigGap = double.MaxValue;
					foreach (var item in sibs)
					{
						Rect t2 = item.Item2;
						double g1 = t2.Left - cur.Right;
						double g2 = cur.Left - t2.Right;
						double cg1 = t2.Left - curDip.X;
						double cg2 = curDip.X - t2.Right;
						curGap = Math.Min(curGap, (Math.Abs(g1) <= Math.Abs(g2)) ? g1 : g2);
						ptrGap = Math.Min(ptrGap, (Math.Abs(cg1) <= Math.Abs(cg2)) ? cg1 : cg2);
						double nearA = ((Math.Abs(cg1) < Math.Abs(g1)) ? cg1 : g1);
						double nearB = ((Math.Abs(cg2) < Math.Abs(g2)) ? cg2 : g2);
						trigGap = Math.Min(trigGap, Math.Min(Math.Abs(nearA), Math.Abs(nearB)));
					}
					DebugLog($"[对齐诊断-间隙] 阈值={thr} 整窗间隙={curGap:F0} 光标间隙={ptrGap:F0} 触发间隙={trigGap:F0} 光标raw=({cp.X},{cp.Y})");
					try
					{
						Rect adjusted;
						bool pure = WindowSnapService.TryDockAlign(h, cur, out adjusted);
						DebugLog($"[对齐诊断-纯整窗] 纯整窗边缘阈值{thr}可吸={pure}");
					}
					catch (Exception ex3)
					{
						DebugLog("[对齐诊断-纯整窗-异常] " + ex3.GetType().Name + ":" + ex3.Message);
					}
				}
				catch (Exception ex4)
				{
					DebugLog("[对齐诊断-间隙-异常] " + ex4.GetType().Name + ":" + ex4.Message);
				}
			}
			_cfg.SetBoundsDip(finalRect);
		}
		_cfg.Save(finalRect);
	}

	public void ResizeGlowTick()
	{
		_cfg.ResizeGlowTick?.Invoke();
	}

	public void ResizeGlowEnd()
	{
		_cfg.ResizeGlowEnd?.Invoke();
	}

	public void ScheduleSnapAlign()
	{
		_snapTimer?.Start();
	}

	public void StopSnapAlign()
	{
		_snapTimer?.Stop();
	}

	public void Dispose()
	{
		_disposed = true;
		_snapTimer?.Stop();
		if (_registeredHwnd != IntPtr.Zero)
		{
			WindowSnapService.Unregister(_registeredHwnd);
			WidgetAlignGlowService.Unregister(_registeredHwnd);
			_registeredHwnd = IntPtr.Zero;
		}
	}

	private static Point GetCursorDip()
	{
		NativeMethods.GetCursorPos(out var cp);
		nint mon = NativeMethods.MonitorFromPoint(cp, 2u);
		double scale = 1.0;
		if (mon != IntPtr.Zero && NativeMethods.GetDpiForMonitor(mon, 0, out var dpiX, out var _) == 0)
		{
			scale = (double)dpiX / 96.0;
		}
		if (scale <= 0.0 || !double.IsFinite(scale))
		{
			scale = 1.0;
		}
		return new Point((double)cp.X / scale, (double)cp.Y / scale);
	}

	private static void DebugLog(string msg)
	{
		try
		{
			File.AppendAllText(_dragLog, $"{DateTime.Now:HH:mm:ss.ff} {msg}\n");
		}
		catch
		{
		}
	}
}
