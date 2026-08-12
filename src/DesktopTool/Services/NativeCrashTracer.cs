using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopTool.Services;

internal static class NativeCrashTracer
{
	private delegate uint UnhandledExceptionFilterDelegate(nint exceptionInfo);

	private struct EXCEPTION_POINTERS
	{
		public nint ExceptionRecord;

		public nint ContextRecord;
	}

	private struct EXCEPTION_RECORD
	{
		public uint ExceptionCode;

		public uint ExceptionFlags;

		public nint ExceptionRecord;

		public nint ExceptionAddress;

		public uint NumberParameters;

		public nint ExceptionInformation0;
	}

	private static readonly string CrashLogPath = Path.Combine(Path.GetTempPath(), "DesktopTool_Crash.log");

	private static UnhandledExceptionFilterDelegate? _filterDelegate;

	[DllImport("kernel32.dll")]
	private static extern nint SetUnhandledExceptionFilter(UnhandledExceptionFilterDelegate lpTopLevelExceptionFilter);

	[DllImport("kernel32.dll")]
	private static extern uint CaptureStackBackTrace(uint framesToSkip, uint framesToCapture, nint[] stack, nint backTraceHash);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetModuleHandleEx(uint dwFlags, nint lpModuleName, out nint phModule);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern uint GetModuleFileName(nint hModule, StringBuilder lpFilename, uint nSize);

	public static void Install()
	{
		try
		{
			_filterDelegate = TopLevelFilter;
			SetUnhandledExceptionFilter(_filterDelegate);
		}
		catch
		{
		}
	}

	private static uint TopLevelFilter(nint exceptionInfo)
	{
		try
		{
			if (exceptionInfo == IntPtr.Zero)
			{
				return 0u;
			}
			EXCEPTION_RECORD er = Marshal.PtrToStructure<EXCEPTION_RECORD>(Marshal.PtrToStructure<EXCEPTION_POINTERS>(exceptionInfo).ExceptionRecord);
			StringBuilder sb = new StringBuilder();
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(37, 1, stringBuilder);
			handler.AppendLiteral("========== [NATIVE CRASH] ");
			handler.AppendFormatted(DateTime.Now, "yyyy-MM-dd HH:mm:ss.fff");
			handler.AppendLiteral(" ==========");
			stringBuilder2.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder3 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(17, 1, stringBuilder);
			handler.AppendLiteral("ExceptionCode: 0x");
			handler.AppendFormatted(er.ExceptionCode, "X8");
			stringBuilder3.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder4 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder);
			handler.AppendLiteral("ExceptionAddress: 0x");
			handler.AppendFormatted(((IntPtr)er.ExceptionAddress).ToInt64(), "X16");
			stringBuilder4.AppendLine(ref handler);
			if (GetModuleHandleEx(6u, er.ExceptionAddress, out var hMod) && hMod != IntPtr.Zero)
			{
				StringBuilder sbName = new StringBuilder(1024);
				if (GetModuleFileName(hMod, sbName, (uint)sbName.Capacity) != 0)
				{
					stringBuilder = sb;
					StringBuilder stringBuilder5 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder);
					handler.AppendLiteral("FaultingModule: ");
					handler.AppendFormatted(sbName);
					stringBuilder5.AppendLine(ref handler);
				}
			}
			try
			{
				nint[] frames = new nint[48];
				uint captured = CaptureStackBackTrace(0u, (uint)frames.Length, frames, IntPtr.Zero);
				stringBuilder = sb;
				StringBuilder stringBuilder6 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder);
				handler.AppendLiteral("StackTrace(frames=");
				handler.AppendFormatted(captured);
				handler.AppendLiteral("):");
				stringBuilder6.AppendLine(ref handler);
				for (int i = 0; i < captured; i++)
				{
					stringBuilder = sb;
					StringBuilder stringBuilder7 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(6, 2, stringBuilder);
					handler.AppendLiteral("  ");
					handler.AppendFormatted(i);
					handler.AppendLiteral(": 0x");
					handler.AppendFormatted(((IntPtr)frames[i]).ToInt64(), "X16");
					stringBuilder7.AppendLine(ref handler);
				}
			}
			catch (Exception ex)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder8 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(19, 1, stringBuilder);
				handler.AppendLiteral("StackTrace: 无法获取 (");
				handler.AppendFormatted(ex.Message);
				handler.AppendLiteral(")");
				stringBuilder8.AppendLine(ref handler);
			}
			File.AppendAllText(CrashLogPath, sb.ToString());
		}
		catch
		{
		}
		return 0u;
	}
}
