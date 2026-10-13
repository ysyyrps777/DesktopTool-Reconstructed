using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace DesktopTool.Services;

internal static class NativeMethods
{
	public struct POINT
	{
		public int X;

		public int Y;

		public POINT(int x, int y)
		{
			X = x;
			Y = y;
		}
	}

	public struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;

		public int Width => Right - Left;

		public int Height => Bottom - Top;
	}

	public struct WINDOWPOS
	{
		public nint hwnd;

		public nint hwndInsertAfter;

		public int x;

		public int y;

		public int cx;

		public int cy;

		public uint flags;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	public struct LOGFONT
	{
		public int lfHeight;

		public int lfWidth;

		public int lfEscapement;

		public int lfOrientation;

		public int lfWeight;

		public byte lfItalic;

		public byte lfUnderline;

		public byte lfStrikeOut;

		public byte lfCharSet;

		public byte lfOutPrecision;

		public byte lfClipPrecision;

		public byte lfQuality;

		public byte lfPitchAndFamily;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string lfFaceName;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	public struct LVITEM
	{
		public uint mask;

		public int iItem;

		public int iSubItem;

		public uint state;

		public uint stateMask;

		[MarshalAs(UnmanagedType.LPTStr)]
		public string pszText;

		public int cchTextMax;

		public int iImage;

		public nint lParam;

		public int iIndent;

		public int iGroupId;

		public uint cColumns;

		public nint puColumns;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	public struct MONITORINFO
	{
		public int cbSize;

		public RECT rcMonitor;

		public RECT rcWork;

		public uint dwFlags;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct SHELLEXECUTEINFO
	{
		public int cbSize;

		public uint fMask;

		public nint hwnd;

		[MarshalAs(UnmanagedType.LPTStr)]
		public string? lpVerb;

		[MarshalAs(UnmanagedType.LPTStr)]
		public string? lpFile;

		[MarshalAs(UnmanagedType.LPTStr)]
		public string? lpParameters;

		[MarshalAs(UnmanagedType.LPTStr)]
		public string? lpDirectory;

		public int nShow;

		public nint hInstApp;

		public nint lpIDList;

		[MarshalAs(UnmanagedType.LPTStr)]
		public string? lpClass;

		public nint hKeyClass;

		public uint dwHotKey;

		public nint hIconOrMonitor;

		public nint hProcess;
	}

	[Flags]
	public enum ShellExecuteMaskFlags : uint
	{
		SEE_MASK_NOCLOSEPROCESS = 0x40u,
		SEE_MASK_FLAG_NO_UI = 0x400u,
		SEE_MASK_ASYNCOK = 0x100000u
	}

	private sealed class LnkResolveResult
	{
		public string TargetPath = string.Empty;

		public string? Arguments;

		public string? WorkingDirectory;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
	public struct SHELLSTATE
	{
		public uint dwBits1;
	}

	public struct RM_UNIQUE_PROCESS
	{
		public uint dwProcessId;

		public FILETIME ProcessStartTime;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct RM_PROCESS_INFO
	{
		public RM_UNIQUE_PROCESS Process;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string strAppName;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string strServiceShortName;

		public int ApplicationType;

		public uint AppStatus;

		public uint TSSessionId;

		[MarshalAs(UnmanagedType.Bool)]
		public bool bRestartable;
	}

	public struct FILE_BASIC_INFO
	{
		public long CreationTime;

		public long LastAccessTime;

		public long LastWriteTime;

		public long ChangeTime;

		public uint FileAttributes;
	}

	public enum ASSOC_FILTER
	{
		ASSOC_FILTER_NONE,
		ASSOC_FILTER_RECOMMENDED
	}

	[ComImport]
	[Guid("973810ae-6319-4a8f-93ae-5ad19b8f27e2")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface IEnumAssocHandlers
	{
		[PreserveSig]
		int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IAssocHandler rgelt, out uint pceltFetched);
	}

	[ComImport]
	[Guid("f0406a60-6159-44d9-88a4-39bf5e2e54a9")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface IAssocHandler
	{
		[PreserveSig]
		int GetName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);

		[PreserveSig]
		int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);

		[PreserveSig]
		int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string ppszPath, out int pIndex);

		[PreserveSig]
		int IsRecommended(out bool pbRecommended);

		[PreserveSig]
		int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string pszDescription);

		[PreserveSig]
		int Invoke([MarshalAs(UnmanagedType.Interface)] object pdo);

		[PreserveSig]
		int CreateInvoker([MarshalAs(UnmanagedType.Interface)] object pdo, [MarshalAs(UnmanagedType.Interface)] out object ppInvoker);
	}

	public struct LUID
	{
		public uint LowPart;

		public int HighPart;
	}

	public struct TOKEN_PRIVILEGES
	{
		public uint PrivilegeCount;

		public LUID Luid;

		public uint Attributes;
	}

	public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

	public struct KEYBDINPUT
	{
		public ushort wVk;

		public ushort wScan;

		public uint dwFlags;

		public uint time;

		public nint dwExtraInfo;
	}

	[StructLayout(LayoutKind.Explicit)]
	public struct INPUT_UNION
	{
		[FieldOffset(0)]
		public KEYBDINPUT ki;
	}

	public struct INPUT
	{
		public uint type;

		public INPUT_UNION u;
	}

	public struct WINDOWPLACEMENT
	{
		public int length;

		public int flags;

		public int showCmd;

		public POINT ptMinPosition;

		public POINT ptMaxPosition;

		public RECT rcNormalPosition;
	}

	[Flags]
	public enum MINIDUMP_TYPE : uint
	{
		MiniDumpNormal = 0u,
		MiniDumpWithFullMemory = 2u,
		MiniDumpWithHandleData = 4u,
		MiniDumpWithProcessThreadData = 0x100u,
		MiniDumpWithThreadInfo = 0x1000u
	}

	public delegate void WinEventDelegate(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

	public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

	public struct GUITHREADINFO
	{
		public int cbSize;

		public uint flags;

		public nint hwndActive;

		public nint hwndFocus;

		public nint hwndCapture;

		public nint hwndMenuOwner;

		public nint hwndMoveSize;

		public nint hwndCaret;

		public RECT rcCaret;
	}

	[ComImport]
	[Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface ITaskbarList
	{
		void HrInit();

		void AddTab(nint hwnd);

		void DeleteTab(nint hwnd);

		void ActivateTab(nint hwnd);

		void SetActiveAlt(nint hwnd);
	}

	[ComImport]
	[Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
	public class TaskbarList
	{
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct SHFILEOPSTRUCT
	{
		public nint hwnd;

		public uint wFunc;

		public string pFrom;

		public string? pTo;

		public ushort fFlags;

		public int fAnyOperationsAborted;

		public nint hNameMappings;

		public string? lpszProgressTitle;
	}

	public const int GWL_EXSTYLE = -20;

	public const int GWL_STYLE = -16;

	public const uint DRIVE_UNKNOWN = 0u;

	public const uint DRIVE_NO_ROOT_DIR = 1u;

	public const uint DRIVE_REMOVABLE = 2u;

	public const uint DRIVE_FIXED = 3u;

	public const uint DRIVE_REMOTE = 4u;

	public const uint DRIVE_CDROM = 5u;

	public const uint DRIVE_RAMDISK = 6u;

	public const uint SPI_GETICONTITLELOGFONT = 31u;

	public const int GWL_HWNDPARENT = -8;

	public const int WS_EX_TOOLWINDOW = 128;

	public const int WS_EX_NOACTIVATE = 134217728;

	public const int WS_EX_TRANSPARENT = 32;

	public const int WS_EX_LAYERED = 524288;

	public const int WS_EX_TOPMOST = 8;

	public const int WS_MINIMIZEBOX = 131072;

	public const int WS_THICKFRAME = 262144;

	public const int WS_DLGFRAME = 4194304;

	public const int WS_BORDER = 8388608;

	public const int WS_POPUP = int.MinValue;

	public const int WS_VISIBLE = 268435456;

	public const int WS_CHILD = 1073741824;

	public const uint WM_NCLBUTTONDOWN = 161u;

	public const uint WM_SYSCOMMAND = 274u;

	public const int HT_CAPTION = 2;

	public const int HT_LEFT = 10;

	public const int HT_RIGHT = 11;

	public const int HT_TOP = 12;

	public const int HT_TOPLEFT = 13;

	public const int HT_TOPRIGHT = 14;

	public const int HT_BOTTOM = 15;

	public const int HT_BOTTOMLEFT = 16;

	public const int HT_BOTTOMRIGHT = 17;

	public const int SC_MOVE = 61456;

	public const int SC_MINIMIZE = 61472;

	public const uint WM_WINDOWPOSCHANGING = 70u;

	public const int SW_SHOWNOACTIVATE = 4;

	public const uint SWP_NOSIZE = 1u;

	public const uint SWP_NOMOVE = 2u;

	public const uint SWP_NOACTIVATE = 16u;

	public const uint SWP_ASYNCWINDOWPOS = 16384u;

	public const uint SWP_SHOWWINDOW = 64u;

	public const uint SWP_HIDEWINDOW = 128u;

	public const uint SWP_NOZORDER = 4u;

	public const uint SWP_FRAMECHANGED = 32u;

	public const uint SWP_NOCOPYBITS = 256u;

	public static readonly nint HWND_BOTTOM = new IntPtr(1);

	public static readonly nint HWND_TOP = IntPtr.Zero;

	public static readonly nint HWND_TOPMOST = new IntPtr(-1);

	public static readonly nint HWND_NOTOPMOST = new IntPtr(-2);

	public const uint LVM_FIRST = 4096u;

	public const uint LVM_GETITEMCOUNT = 4100u;

	public const uint LVM_GETITEMPOSITION = 4112u;

	public const uint LVM_SETITEMPOSITION = 4111u;

	public const uint LVM_GETITEMTEXT = 4211u;

	public const uint LVM_GETITEMW = 4171u;

	public const uint LVM_SETITEMW = 4172u;

	public const uint LVM_REDRAWITEMS = 4117u;

	public const uint LVM_UPDATE = 4138u;

	public const uint LVM_ARRANGE = 4118u;

	public const uint LVM_GETITEM = 4171u;

	public const uint LVIF_TEXT = 1u;

	public const uint LVIF_IMAGE = 2u;

	public const uint LVIF_PARAM = 4u;

	public const uint LVIF_STATE = 8u;

	public const uint LVS_AUTOARRANGE = 256u;

	public const uint LVA_DEFAULT = 0u;

	public const uint LVA_ALIGNLEFT = 1u;

	public const uint LVA_ALIGNTOP = 2u;

	public const uint LVA_SNAPTOGRID = 5u;

	public const uint WM_SETTINGCHANGE = 26u;

	public const uint PROCESS_VM_OPERATION = 8u;

	public const uint PROCESS_VM_READ = 16u;

	public const uint PROCESS_VM_WRITE = 32u;

	public const uint MEM_COMMIT = 4096u;

	public const uint MEM_RESERVE = 8192u;

	public const uint MEM_RELEASE = 32768u;

	public const uint PAGE_READWRITE = 4u;

	public const uint FILE_ATTRIBUTE_READONLY = 1u;

	public const uint FILE_ATTRIBUTE_HIDDEN = 2u;

	public const uint FILE_ATTRIBUTE_SYSTEM = 4u;

	public const uint FILE_ATTRIBUTE_DIRECTORY = 16u;

	public const uint FILE_ATTRIBUTE_ARCHIVE = 32u;

	public const uint FILE_ATTRIBUTE_NORMAL = 128u;

	public const uint FILE_ATTRIBUTE_TEMPORARY = 256u;

	public const uint FILE_ATTRIBUTE_COMPRESSED = 2048u;

	public const uint FILE_ATTRIBUTE_OFFLINE = 4096u;

	public const uint FILE_ATTRIBUTE_NOT_CONTENT_INDEXED = 8192u;

	public const uint FILE_ATTRIBUTE_ENCRYPTED = 16384u;

	public const uint INVALID_FILE_ATTRIBUTES = uint.MaxValue;

	public const uint SMTO_ABORTIFHUNG = 2u;

	public const uint SHCNE_CREATE = 2u;

	public const uint SHCNE_UPDATEITEM = 8192u;

	public const uint SHCNE_UPDATEDIR = 4096u;

	public const uint SHCNE_ASSOCCHANGED = 134217728u;

	public const uint SHCNF_PATHW = 5u;

	public const uint SHCNF_FLUSH = 4096u;

	public const uint SHCNF_IDLIST = 0u;

	public const int MDT_EFFECTIVE_DPI = 0;

	public const uint MONITOR_DEFAULTTONULL = 0u;

	public const uint MONITOR_DEFAULTTOPRIMARY = 1u;

	public const uint MONITOR_DEFAULTTONEAREST = 2u;

	public const uint MSGFLT_ALLOW = 1u;

	public const uint WM_DROPFILES = 563u;

	public const uint WM_COPYDATA = 74u;

	public const uint WM_COPYGLOBALDATA = 73u;

	public const uint SHOP_FILEPATH = 2u;

	public const uint SSF_SHOWALLOBJECTS = 1u;

	public const uint SSF_SHOWSYSFILES = 32u;

	public const uint SSF_SHOWSUPERHIDDEN = 262144u;

	public const int SHELLSTATE_BIT_ALLOBJECTS = 0;

	public const int SHELLSTATE_BIT_SYSFILES = 3;

	public const int SHELLSTATE_BIT_SUPERHIDDEN = 15;

	public const int RmUnknownApp = 0;

	public const int RmOtherSession = 1;

	public const uint GENERIC_READ = 2147483648u;

	public const uint GENERIC_WRITE = 1073741824u;

	public const uint FILE_SHARE_READ = 1u;

	public const uint FILE_SHARE_WRITE = 2u;

	public const uint FILE_SHARE_DELETE = 4u;

	public const uint OPEN_EXISTING = 3u;

	public const uint INVALID_HANDLE_VALUE = uint.MaxValue;

	public const int ERROR_SHARING_VIOLATION = 32;

	public const int ERROR_FILE_NOT_FOUND = 2;

	public const int ERROR_PATH_NOT_FOUND = 3;

	public const uint FILE_WRITE_ATTRIBUTES = 256u;

	public const uint FILE_READ_ATTRIBUTES = 128u;

	public const int FileBasicInfo = 0;

	public const uint GW_OWNER = 4u;

	public const uint GW_HWNDNEXT = 2u;

	public const uint TOKEN_ADJUST_PRIVILEGES = 32u;

	public const uint TOKEN_QUERY = 8u;

	public const uint SE_PRIVILEGE_ENABLED = 2u;

	public const string SE_RESTORE_NAME = "SeRestorePrivilege";

	public const int WH_KEYBOARD_LL = 13;

	public const int WM_KEYDOWN = 256;

	public const int WM_SYSKEYDOWN = 260;

	public const int WM_KEYUP = 257;

	public const int WM_SYSKEYUP = 261;

	public const int VK_F5 = 116;

	public const int VK_LWIN = 91;

	public const int VK_RWIN = 92;

	public const int VK_D = 68;

	public const int KEYEVENTF_KEYUP = 2;

	public const uint INPUT_KEYBOARD = 1u;

	public const uint KEYEVENTF_KEYDOWN = 0u;

	public const uint KEYEVENTF_KEYUP_SI = 2u;

	public const int DWMWA_CLOAKED = 14;

	public const int DWMWA_CLOAK = 13;

	public const int DWMWA_EXCLUDED_FROM_PEEK = 12;

	public const int SW_HIDE = 0;

	public const int SW_SHOWNORMAL = 1;

	public const int SW_SHOWMINIMIZED = 2;

	public const int SW_SHOWMAXIMIZED = 3;

	public const int SW_MINIMIZE = 6;

	public const int SW_SHOWMINNOACTIVE = 7;

	public const uint EVENT_OBJECT_CLOAKED = 32791u;

	public const uint EVENT_OBJECT_UNCLOAKED = 32792u;

	public const uint EVENT_OBJECT_HIDE = 32772u;

	public const uint EVENT_OBJECT_STATECHANGE = 32778u;

	public const uint EVENT_OBJECT_CREATE = 32768u;

	public const uint EVENT_SYSTEM_MENUSTART = 4u;

	public const uint EVENT_SYSTEM_MENUEND = 5u;

	public const uint EVENT_SYSTEM_MENUPOPUPSTART = 6u;

	public const uint EVENT_SYSTEM_MENUPOPUPUP = 7u;

	public const uint EVENT_OBJECT_SHOW = 32770u;

	public const uint WINEVENT_OUTOFCONTEXT = 0u;

	public const uint WINEVENT_SKIPOWNPROCESS = 2u;

	public static readonly nint GWLP_HWNDPARENT = new IntPtr(-8);

	public static uint WM_DESKTOOL_WIND;

	public static uint WM_DESKTOOL_SHOWMAIN;

	public const uint GUI_INMENUMODE = 4u;

	public const uint GUI_POPUPMENUMODE = 16u;

	public const uint GUI_SYSTEMMENUMODE = 8u;

	public static uint WM_TASKBARCREATED;

	public const uint FO_DELETE = 3u;

	public const ushort FOF_ALLOWUNDO = 64;

	public const ushort FOF_NOCONFIRMATION = 16;

	public const ushort FOF_SILENT = 4;

	public const ushort FOF_WANTNUKEWARNING = 16384;

	public const int WM_HOTKEY = 786;

	public const uint MOD_ALT = 1u;

	public const uint MOD_CONTROL = 2u;

	public const uint MOD_SHIFT = 4u;

	public const uint MOD_WIN = 8u;

	public const uint MOD_NOREPEAT = 16384u;

	public const int WM_WTSSESSION_CHANGE = 689;

	public const int NOTIFY_FOR_THIS_SESSION = 0;

	public const int NOTIFY_FOR_ALL_SESSIONS = 1;

	public const int WTS_CONSOLE_CONNECT = 1;

	public const int WTS_CONSOLE_DISCONNECT = 2;

	public const int WTS_REMOTE_CONNECT = 3;

	public const int WTS_REMOTE_DISCONNECT = 4;

	public const int WTS_SESSION_LOGON = 5;

	public const int WTS_SESSION_LOGOFF = 6;

	public const int WTS_SESSION_LOCK = 7;

	public const int WTS_SESSION_UNLOCK = 8;

	public const int WM_POWERBROADCAST = 536;

	public const int PBT_APMQUERYSUSPEND = 0;

	public const int PBT_APMQUERYSTANDBY = 1;

	public const int PBT_APMSUSPEND = 4;

	public const int PBT_APMRESUMECRITICAL = 6;

	public const int PBT_APMRESUMESUSPEND = 7;

	public const int PBT_APMRESUMESTANDBY = 8;

	public const int PBT_APMRESUMEAUTOMATIC = 18;

	[DllImport("kernel32.dll", CharSet = CharSet.Auto)]
	public static extern uint GetDriveType(string lpRootPathName);

	[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref LOGFONT pvParam, uint fWinIni);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern uint GetFileAttributes(string lpFileName);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetFileAttributes(string lpFileName, uint dwFileAttributes);

	[DllImport("kernel32.dll")]
	public static extern void SetLastError(uint dwErrCode);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes);

	[DllImport("user32.dll")]
	public static extern nint FindWindow(string? lpClassName, string? lpWindowName);

	[DllImport("user32.dll")]
	public static extern nint FindWindowEx(nint hwndParent, nint hwndChildAfter, string? lpszClass, string? lpszWindow);

	[DllImport("user32.dll")]
	public static extern int GetWindowLong(nint hWnd, int nIndex);

	[DllImport("user32.dll")]
	public static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

	[DllImport("user32.dll")]
	public static extern int GetDpiForWindow(nint hWnd);

	[DllImport("user32.dll")]
	public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

	[DllImport("user32.dll")]
	public static extern bool ShowWindow(nint hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	public static extern nint SetFocus(nint hWnd);

	[DllImport("user32.dll")]
	public static extern nint GetFocus();

	[DllImport("user32.dll")]
	public static extern nint SendMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

	[DllImport("user32.dll")]
	public static extern nint SendMessage(nint hWnd, uint Msg, nint wParam, StringBuilder lParam);

	[DllImport("user32.dll")]
	public static extern nint SendMessage(nint hWnd, uint Msg, nint wParam, ref POINT lParam);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool PostMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

	[DllImport("user32.dll")]
	public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

	[DllImport("user32.dll")]
	public static extern bool ClientToScreen(nint hWnd, out POINT lpPoint);

	[DllImport("user32.dll")]
	public static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

	[DllImport("Shcore.dll")]
	public static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

	[DllImport("user32.dll")]
	public static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

	[DllImport("user32.dll")]
	public static extern nint MonitorFromRect(ref RECT lprc, uint dwFlags);

	[DllImport("user32.dll")]
	public static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern nint SetParent(nint hWndChild, nint hWndNewParent);

	[DllImport("ole32.dll")]
	public static extern int RevokeDragDrop(nint hwnd);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool ChangeWindowMessageFilterEx(nint hwnd, uint msg, uint action, nint pChangeFilterStruct);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern nint GetProp(nint hWnd, string lpString);

	[DllImport("user32.dll")]
	public static extern bool GetCursorPos(out POINT lpPoint);

	[DllImport("user32.dll")]
	public static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("kernel32.dll")]
	public static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

	[DllImport("kernel32.dll")]
	public static extern nint VirtualAllocEx(nint hProcess, nint lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

	[DllImport("kernel32.dll")]
	public static extern bool VirtualFreeEx(nint hProcess, nint lpAddress, uint dwSize, uint dwFreeType);

	[DllImport("kernel32.dll")]
	public static extern bool WriteProcessMemory(nint hProcess, nint lpBaseAddress, nint lpBuffer, uint nSize, out uint lpNumberOfBytesWritten);

	[DllImport("kernel32.dll")]
	public static extern bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, nint lpBuffer, uint nSize, out uint lpNumberOfBytesRead);

	[DllImport("kernel32.dll")]
	public static extern bool CloseHandle(nint hObject);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	public static extern nint ShellExecute(nint hwnd, string lpOperation, string lpFile, string? lpParameters, string? lpDirectory, int nShowCmd);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO pExecInfo);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	public static extern nint ILCreateFromPath(string pszPath);

	[DllImport("shell32.dll")]
	public static extern void ILFree(nint pidl);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	public static extern int SHOpenFolderAndSelectItems(nint pidlFolder, uint cidl, nint[]? apidl, uint dwFlags);

	private static LnkResolveResult? ResolveLnk(string lnkPath)
	{
		try
		{
			Type wsType = Type.GetTypeFromProgID("WScript.Shell");
			if (wsType == null)
			{
				return null;
			}
			dynamic ws = Activator.CreateInstance(wsType);
			dynamic shortcut = ws.CreateShortcut(lnkPath);
			string target = shortcut.TargetPath as string;
			if (string.IsNullOrEmpty(target))
			{
				return null;
			}
			LnkResolveResult lnkResolveResult = new LnkResolveResult();
			lnkResolveResult.TargetPath = target;
			lnkResolveResult.Arguments = shortcut.Arguments as string;
			lnkResolveResult.WorkingDirectory = shortcut.WorkingDirectory as string;
			LnkResolveResult r = lnkResolveResult;
			try
			{
				Marshal.ReleaseComObject(shortcut);
			}
			catch
			{
			}
			try
			{
				Marshal.ReleaseComObject(ws);
			}
			catch
			{
			}
			return r;
		}
		catch
		{
			return null;
		}
	}

	private static bool TryOpenFolder(string filePath, string dbg)
	{
		try
		{
			string target = filePath;
			if (filePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
			{
				target = ResolveLnk(filePath)?.TargetPath;
				if (string.IsNullOrEmpty(target))
				{
					return false;
				}
			}
			if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
			{
				nint pidl = ILCreateFromPath(target);
				if (pidl == IntPtr.Zero)
				{
					return false;
				}
				try
				{
					GCHandle childPin = GCHandle.Alloc(new byte[2], GCHandleType.Pinned);
					try
					{
						nint[] apidl = new nint[1] { childPin.AddrOfPinnedObject() };
						int hr = SHOpenFolderAndSelectItems(pidl, 1u, apidl, 0u);
						bool ok = hr >= 0;
						LogHelper.Append(dbg, $"[{DateTime.Now:HH:mm:ss.fff}] END ok={ok} err={hr} (导航进入文件夹 DDE-free){Environment.NewLine}");
						return true;
					}
					finally
					{
						childPin.Free();
					}
				}
				finally
				{
					ILFree(pidl);
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.Append(dbg, $"[{DateTime.Now:HH:mm:ss.fff}] FOLDER_FALLBACK {ex.Message}{Environment.NewLine}");
		}
		return false;
	}

	public static void ShellExecuteOpenAsync(string file, nint hwnd, string verb = "open", string? parameters = null)
	{
		string dir = null;
		try
		{
			dir = Path.GetDirectoryName(file);
			if (string.IsNullOrEmpty(dir))
			{
				dir = null;
			}
		}
		catch
		{
		}
		string filePath = file;
		string verbLocal = verb;
		nint hwndLocal = hwnd;
		string parametersLocal = parameters;
		string dirLocal = dir;
		string dbg = Path.Combine(App.GetAppDataDir(), "debug_launch.log");
		LogHelper.Append(dbg, $"[{DateTime.Now:HH:mm:ss.fff}] START file={filePath} verb={verbLocal} hwnd={hwndLocal}{Environment.NewLine}");
		Thread thread = new Thread((ThreadStart)delegate
		{
			Dispatcher disp = Dispatcher.CurrentDispatcher;
			Timer timer = new Timer(delegate
			{
				try
				{
					disp.InvokeShutdown();
				}
				catch
				{
				}
			}, null, 45000, -1);
			disp.BeginInvoke((Action)delegate
			{
				try
				{
					string text = (filePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? filePath : null);
					string text2 = null;
					string parameters2 = parametersLocal;
					string text3 = dirLocal;
					bool flag = false;
					if (text != null)
					{
						LnkResolveResult lnkResolveResult = ResolveLnk(text);
						if (lnkResolveResult != null && !string.IsNullOrEmpty(lnkResolveResult.TargetPath) && File.Exists(lnkResolveResult.TargetPath))
						{
							text2 = lnkResolveResult.TargetPath;
							parameters2 = (string.IsNullOrEmpty(lnkResolveResult.Arguments) ? parametersLocal : lnkResolveResult.Arguments);
							if (!string.IsNullOrEmpty(lnkResolveResult.WorkingDirectory) && Directory.Exists(lnkResolveResult.WorkingDirectory))
							{
								text3 = lnkResolveResult.WorkingDirectory;
							}
							else if (string.IsNullOrEmpty(text3))
							{
								try
								{
									text3 = Path.GetDirectoryName(lnkResolveResult.TargetPath);
								}
								catch
								{
								}
							}
							flag = IsDdeDependentTarget(lnkResolveResult.TargetPath);
							if (verbLocal == "runas")
							{
								flag = true;
							}
						}
					}
					bool flag2 = false;
					if (text != null)
					{
						flag2 = TryOpenFolder(text, dbg);
					}
					if (!flag2)
					{
						flag2 = TryOpenFolder(filePath, dbg);
					}
					if (!flag2)
					{
						if (text != null && flag && text2 != null)
						{
							if (!ShellExecOpen(text, parametersLocal, dirLocal, verbLocal, hwndLocal, dbg, "lnk本体(DDE优先)"))
							{
								LogHelper.Append(dbg, $"[{DateTime.Now:HH:mm:ss.fff}] LNK_FALLBACK 本体失败，改用解析exe：{text2}{Environment.NewLine}");
								ShellExecOpen(text2, parameters2, text3, verbLocal, hwndLocal, dbg, "lnk解析exe(兜底)");
							}
						}
						else if (text != null && text2 != null)
						{
							ShellExecOpen(text2, parameters2, text3, verbLocal, hwndLocal, dbg, "lnk解析exe(V2.7.92默认)");
						}
						else if (IsExecutableByShell(filePath))
						{
							ShellExecOpen(filePath, parametersLocal, dirLocal, verbLocal, hwndLocal, dbg, "普通文件(可执行)");
						}
						else if (!ShellExecOpen("explorer.exe", "/open,\"" + filePath + "\"", dirLocal, verbLocal, hwndLocal, dbg, "普通文件(explorer间接)"))
						{
							ShellExecOpen(filePath, parametersLocal, dirLocal, verbLocal, hwndLocal, dbg, "普通文件(兜底直启)");
						}
					}
				}
				catch (Exception ex)
				{
					LogHelper.Append(dbg, $"[{DateTime.Now:HH:mm:ss.fff}] EXCEPTION {ex.Message}{Environment.NewLine}");
				}
				finally
				{
					new DispatcherTimer(TimeSpan.FromSeconds(3.0), DispatcherPriority.Normal, delegate(object? s, EventArgs e2)
					{
						try
						{
							((DispatcherTimer)s).Stop();
						}
						catch
						{
						}
						try
						{
							disp.InvokeShutdown();
						}
						catch
						{
						}
					}, disp).Start();
				}
			});
			Dispatcher.Run();
			try
			{
				timer.Dispose();
			}
			catch
			{
			}
		});
		thread.Name = "DesktopTool-ShellLaunch";
		thread.IsBackground = true;
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
	}

	private static bool ShellExecOpen(string file, string? parameters, string? dir, string verb, nint hwnd, string dbg, string tag)
	{
		SHELLEXECUTEINFO info = new SHELLEXECUTEINFO
		{
			cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
			fMask = 1024u,
			hwnd = hwnd,
			lpVerb = verb,
			lpFile = file,
			lpParameters = parameters,
			lpDirectory = dir,
			nShow = 1
		};
		bool ok = ShellExecuteEx(ref info);
		int err = Marshal.GetLastWin32Error();
		LogHelper.Append(dbg, $"[{DateTime.Now:HH:mm:ss.fff}] END[{tag}] ok={ok} err={err} (同步STA泵){Environment.NewLine}");
		return ok;
	}

	private static bool IsDdeDependentTarget(string targetPath)
	{
		if (string.IsNullOrEmpty(targetPath))
		{
			return false;
		}
		string name = null;
		try
		{
			name = Path.GetFileName(targetPath);
		}
		catch
		{
			return false;
		}
		if (string.IsNullOrEmpty(name))
		{
			return false;
		}
		name = name.ToLowerInvariant();
		if (!name.Contains("sldworks") && !name.Contains("zwsoft") && !name.Contains("zwcad") && !name.Contains("acad") && !name.Contains(" inventor") && !name.Contains("creo") && !name.Contains("nx"))
		{
			return name.Contains("catia");
		}
		return true;
	}

	private static bool IsExecutableByShell(string filePath)
	{
		if (string.IsNullOrEmpty(filePath))
		{
			return false;
		}
		string ext = null;
		try
		{
			ext = Path.GetExtension(filePath);
		}
		catch
		{
			return false;
		}
		if (string.IsNullOrEmpty(ext))
		{
			return false;
		}
		ext = ext.ToLowerInvariant();
		switch (ext)
		{
		default:
			return ext == ".appref-ms";
		case ".exe":
		case ".lnk":
		case ".msi":
		case ".bat":
		case ".cmd":
		case ".com":
		case ".scr":
		case ".dll":
		case ".cpl":
		case ".msc":
		case ".vbs":
		case ".js":
		case ".jar":
		case ".ps1":
		case ".url":
			return true;
		}
	}

	public static bool IsRunningAsMsix()
	{
		try
		{
			string[] obj = new string[3] { "Windows.ApplicationModel.Package, Microsoft.Windows.SDK.NET", "Windows.ApplicationModel.Package", "Windows.ApplicationModel.Package, Windows" };
			Type pkgType = null;
			string[] array = obj;
			for (int i = 0; i < array.Length; i++)
			{
				pkgType = Type.GetType(array[i]);
				if (pkgType != null)
				{
					break;
				}
			}
			if (pkgType == null)
			{
				return false;
			}
			return pkgType.GetProperty("Current")?.GetValue(null) != null;
		}
		catch
		{
			return false;
		}
	}

	public static bool CanRunAsAdmin(string? filePath)
	{
		if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
		{
			return false;
		}
		string ext = null;
		try
		{
			ext = Path.GetExtension(filePath);
		}
		catch
		{
			return false;
		}
		if (string.IsNullOrEmpty(ext))
		{
			return false;
		}
		ext = ext.ToLowerInvariant();
		switch (ext)
		{
		default:
			return ext == ".cmd";
		case ".exe":
		case ".lnk":
		case ".msi":
		case ".bat":
			return true;
		}
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	public static extern bool SHObjectProperties(nint hwnd, uint shopObjectType, string pszName, string pszPage);

	[DllImport("shell32.dll")]
	public static extern void SHChangeNotify(uint wEventId, uint uFlags, nint dwItem1, nint dwItem2);

	[DllImport("shell32.dll")]
	public static extern void SHGetSetSettings(ref SHELLSTATE lpss, uint dwMask, bool bSet);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

	[DllImport("user32.dll")]
	public static extern bool InvalidateRect(nint hWnd, nint lpRect, bool bErase);

	[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	public static extern nint SendMessageTimeout(nint hWnd, uint Msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

	public static void BringToFront(nint hWnd)
	{
		if (hWnd != IntPtr.Zero)
		{
			ShowWindow(hWnd, 9);
			SetForegroundWindow(hWnd);
		}
	}

	[DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
	public static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

	[DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
	public static extern int RmRegisterResources(uint dwSessionHandle, uint nFiles, string[] rgsFileNames, uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

	[DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, EntryPoint = "RmRegisterResources")]
	public static extern int RmRegisterResourcesEx(uint dwSessionHandle, uint nFiles, nint[] rgsFileNames, uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

	[DllImport("rstrtmgr.dll")]
	public static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In][Out] RM_PROCESS_INFO[]? rgAffectedApps, out uint lpdwRebootReasons);

	[DllImport("rstrtmgr.dll")]
	public static extern int RmEndSession(uint dwSessionHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetFileInformationByHandle(nint hFile, int FileInformationClass, ref FILE_BASIC_INFO lpFileInformation, uint dwBufferSize);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern nint GetWindowTextW(nint hWnd, StringBuilder lpString, int nMaxCount);

	[DllImport("user32.dll")]
	public static extern bool IsWindowVisible(nint hWnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool IsWindow(nint hWnd);

	[DllImport("user32.dll")]
	public static extern nint GetWindow(nint hWnd, uint uCmd);

	[DllImport("user32.dll")]
	public static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	public static extern nint GetDesktopWindow();

	[DllImport("user32.dll")]
	public static extern nint GetShellWindow();

	[DllImport("user32.dll")]
	public static extern nint GetTopWindow(nint hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

	[DllImport("user32.dll")]
	public static extern int GetSystemMetrics(int nIndex);

	[DllImport("user32.dll")]
	public static extern nint GetParent(nint hWnd);

	[DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
	private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, nint ppvReserved);

	[DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
	private static extern int StrCmpLogicalW(string psz1, string psz2);

	public static int NaturalCompare(string? a, string? b)
	{
		return StrCmpLogicalW(a ?? string.Empty, b ?? string.Empty);
	}

	public static string? LoadIndirectString(string source)
	{
		if (string.IsNullOrEmpty(source) || !source.StartsWith("@"))
		{
			return source;
		}
		StringBuilder sb = new StringBuilder(512);
		if (SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero) == 0)
		{
			return sb.ToString();
		}
		return null;
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	public static extern int SHAssocEnumHandlers(string pszExtra, ASSOC_FILTER afFilter, [MarshalAs(UnmanagedType.Interface)] out IEnumAssocHandlers ppEnumHandler);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern nint CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool OpenProcessToken(nint ProcessHandle, uint DesiredAccess, out nint TokenHandle);

	[DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool AdjustTokenPrivileges(nint TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, nint PreviousState, nint ReturnLength);

	[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	public static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

	[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool UnhookWindowsHookEx(nint hhk);

	[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	public static extern nint GetModuleHandle(string lpModuleName);

	[DllImport("user32.dll")]
	public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray)] INPUT[] pInputs, int cbSize);

	[DllImport("dwmapi.dll")]
	public static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

	[DllImport("dwmapi.dll")]
	public static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

	[DllImport("user32.dll")]
	public static extern nint WindowFromPoint(POINT Point);

	[DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool MiniDumpWriteDump(nint hProcess, int processId, nint hFile, MINIDUMP_TYPE dumpType, nint exceptionParam, nint userStreamParam, nint callbackParam);

	[DllImport("kernel32.dll", SetLastError = true)]
	public static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll")]
	public static extern int GetCurrentProcessId();

	[DllImport("user32.dll")]
	public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool UnhookWinEvent(nint hWinEventHook);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	public static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
	public static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

	[DllImport("user32.dll")]
	public static extern short GetAsyncKeyState(int vKey);

	[DllImport("user32.dll")]
	public static extern short GetKeyState(int vKey);

	[DllImport("user32.dll")]
	public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool IsIconic(nint hWnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

	public static void RegisterDesktopToolMessage()
	{
		WM_DESKTOOL_WIND = RegisterWindowMessage("DesktopTool_WinD");
		WM_DESKTOOL_SHOWMAIN = RegisterWindowMessage("DesktopTool_ShowMain");
		WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");
	}

	public static bool RemoveTaskbarButton(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return false;
		}
		try
		{
			ITaskbarList obj = (ITaskbarList)new TaskbarList();
			obj.HrInit();
			obj.DeleteTab(hwnd);
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static void SendShowMainMessage(nint hWnd)
	{
		if (hWnd != IntPtr.Zero)
		{
			uint msg = RegisterWindowMessage("DesktopTool_ShowMain");
			if (msg != 0)
			{
				SendMessageTimeout(hWnd, msg, IntPtr.Zero, IntPtr.Zero, 0u, 2000u, out var _);
			}
		}
	}

	public static bool SendShowMainMessageToProcess(int processId)
	{
		uint msg = RegisterWindowMessage("DesktopTool_ShowMain");
		if (msg == 0)
		{
			return false;
		}
		uint targetPid = (uint)processId;
		bool found = false;
		EnumWindows(delegate(nint hWnd, nint lParam)
		{
			GetWindowThreadProcessId(hWnd, out var lpdwProcessId);
			if (lpdwProcessId == targetPid)
			{
				found = true;
				SendMessageTimeout(hWnd, msg, IntPtr.Zero, IntPtr.Zero, 0u, 2000u, out var _);
			}
			return true;
		}, IntPtr.Zero);
		return found;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern uint RegisterWindowMessage(string lpString);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool UnregisterHotKey(nint hWnd, int id);

	[DllImport("wtsapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool WTSRegisterSessionNotification(nint hWnd, int dwFlags);

	[DllImport("wtsapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool WTSUnRegisterSessionNotification(nint hWnd);
}
