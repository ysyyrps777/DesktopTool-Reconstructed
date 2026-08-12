using System;
using System.IO;
using System.Threading;
using System.Windows.Threading;

namespace DesktopTool.Services;

internal static class UiHangWatchdog
{
	private const int ThresholdMs = 8000;

	private const int CheckIntervalMs = 2000;

	private static long _lastUiAlive = 0L;

	private static bool _dumped = false;

	private static readonly object _gate = new object();

	private static DispatcherTimer _heartbeat;

	private static Timer _watchdog;

	private static readonly string CrashLogPath = Path.Combine(Path.GetTempPath(), "DesktopTool_Crash.log");

	public static void Start()
	{
		_lastUiAlive = Environment.TickCount64;
		_heartbeat = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(1.0)
		};
		_heartbeat.Tick += delegate
		{
			_lastUiAlive = Environment.TickCount64;
		};
		_heartbeat.Start();
		_watchdog = new Timer(delegate
		{
			Check();
		}, null, 2000, 2000);
		LogHelper.Append(CrashLogPath, $"[UI看门狗] 已启动（阈值={8000}ms）；若 UI 假死将写出诊断转储到 {Path.Combine(Path.GetTempPath(), "DesktopTool_HangDumps")}{Environment.NewLine}");
	}

	private static void Check()
	{
		long elapsed = Environment.TickCount64 - _lastUiAlive;
		if (elapsed < 8000)
		{
			return;
		}
		bool shouldDump;
		lock (_gate)
		{
			shouldDump = !_dumped;
			_dumped = true;
		}
		if (!shouldDump)
		{
			return;
		}
		try
		{
			WriteHangDump(elapsed);
		}
		catch (Exception ex)
		{
			LogHelper.Append(CrashLogPath, "[UI看门狗] 写出转储异常: " + ex.Message + Environment.NewLine);
		}
	}

	private static void WriteHangDump(long elapsed)
	{
		string text = Path.Combine(Path.GetTempPath(), "DesktopTool_HangDumps");
		Directory.CreateDirectory(text);
		string path = Path.Combine(text, $"DesktopTool_Hang_{DateTime.Now:yyyyMMdd_HHmmss}.dmp");
		using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
		nint currentProcess = NativeMethods.GetCurrentProcess();
		int pid = NativeMethods.GetCurrentProcessId();
		NativeMethods.MINIDUMP_TYPE type = NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemory | NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData | NativeMethods.MINIDUMP_TYPE.MiniDumpWithProcessThreadData | NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo;
		bool ok = NativeMethods.MiniDumpWriteDump(currentProcess, pid, fs.SafeFileHandle.DangerousGetHandle(), type, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
		LogHelper.Append(CrashLogPath, $"[UI看门狗] 检测到 UI 线程假死(>{elapsed}ms)，已写出挂起诊断转储: {path} (MiniDumpWriteDump={ok}){Environment.NewLine}");
	}
}
