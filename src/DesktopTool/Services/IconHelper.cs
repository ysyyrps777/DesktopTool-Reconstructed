using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DesktopTool.Services;

public static class IconHelper
{
	[ComImport]
	[Guid("00021401-0000-0000-C000-000000000046")]
	private class ShellLink
	{
	}

	[ComImport]
	[Guid("000214EE-0000-0000-C000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellLinkW
	{
		void GetPath([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, nint pfd, uint fFlags);
	}

	[ComImport]
	[Guid("0000010b-0000-0000-c000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPersistFile
	{
		void GetClassID(out Guid pClassId);

		void IsDirty();

		void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint mode);
	}

	private struct SHFILEINFO
	{
		public nint hIcon;

		public int iIcon;

		public uint dwAttributes;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string szDisplayName;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
		public string szTypeName;
	}

	private struct BITMAPINFOHEADER
	{
		public uint biSize;

		public int biWidth;

		public int biHeight;

		public ushort biPlanes;

		public ushort biBitCount;

		public uint biCompression;

		public uint biSizeImage;

		public int biXPelsPerMeter;

		public int biYPelsPerMeter;

		public uint biClrUsed;

		public uint biClrImportant;
	}

	private struct BITMAPINFO
	{
		public BITMAPINFOHEADER bmiHeader;
	}

	private struct ICONINFO
	{
		public bool fIcon;

		public int xHotspot;

		public int yHotspot;

		public nint hbmMask;

		public nint hbmColor;
	}

	private struct BITMAP
	{
		public int bmType;

		public int bmWidth;

		public int bmHeight;

		public int bmWidthBytes;

		public ushort bmPlanes;

		public ushort bmBitsPixel;

		public nint bmBits;
	}

	[ComImport]
	[Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IImageList
	{
		int Add(nint hbmImage, nint hbmMask, out int pi);

		int ReplaceIcon(int i, nint hicon, out int pi);

		int SetOverlayImage(int iImage, int iOverlay);

		int Replace(int i, nint hbmImage, nint hbmMask);

		int AddMasked(nint hbmImage, int crMask, out int pi);

		int Draw(nint pimldp);

		int Remove(int i);

		[PreserveSig]
		int GetIcon(int i, int flags, out nint picon);
	}

	private struct SIZE
	{
		public int cx;

		public int cy;
	}

	[ComImport]
	[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellItemImageFactory
	{
		[PreserveSig]
		int GetImage(SIZE size, int flags, out nint phbm);
	}

	private sealed class IcoFrame
	{
		public int width;

		public int height;

		public byte[] bytes = Array.Empty<byte>();

		public bool isPng;
	}

	[ComImport]
	[Guid("00021401-0000-0000-C000-000000000046")]
	private class ShellLinkFull
	{
	}

	[ComImport]
	[Guid("000214EE-0000-0000-C000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellLinkWFull
	{
		void GetPath([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, nint pfd, uint fFlags);

		void GetIDList(out nint ppidl);

		void SetIDList(nint pidl);

		void GetDescription([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

		void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

		void GetWorkingDirectory([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

		void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

		void GetArguments([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

		void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

		void GetHotkey(out ushort pwHotkey);

		void SetHotkey(ushort wHotkey);

		void GetShowCmd(out int piShowCmd);

		void SetShowCmd(int iShowCmd);

		void GetIconLocation([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);

		void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
	}

	[ComImport]
	[Guid("0000010b-0000-0000-c000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPersistFileFull
	{
		void GetClassID(out Guid pClassId);

		void IsDirty();

		void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint mode);

		void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, bool fRemember);

		void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

		void GetCurFile([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFileName);
	}

	private sealed class MemCacheEntry
	{
		public string Key = "";

		public BitmapSource Image;
	}

	[ThreadStatic]
	private static bool _iconRetryInProgress;

	private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tiff", ".tif" };

	private static readonly HashSet<string> PerFileThumbnailExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".crd" };

	private static readonly HashSet<string> _desktopDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private const uint SHGFI_ICON = 256u;

	private const uint SHGFI_LARGEICON = 0u;

	private const uint SHGFI_SMALLICON = 1u;

	private const uint SHGFI_SHELLICONSIZE = 4u;

	private const uint SHGFI_USEFILEATTRIBUTES = 16u;

	private const uint SHGFI_SYSICONINDEX = 16384u;

	private const int SHIL_JUMBO = 4;

	private const int SHIL_EXTRALARGE = 2;

	private const int ILD_TRANSPARENT = 1;

	private static readonly string _logPath = Path.Combine(Path.GetTempPath(), "DesktopTool_IconDebug.log");

	private static readonly object _logLock = new object();

	private static bool _logHeaderWritten = false;

	private const uint BI_RGB = 0u;

	private const uint DIB_RGB_COLORS = 0u;

	private const int SIIGBF_BIGGERSIZEOK = 1;

	private const int SIIGBF_SCALEUP = 2;

	private const int SIIGBF_ICONONLY = 4;

	private static readonly string IconCacheDir = Path.Combine(App.GetAppDataDir(), "IconCache");

	private const int MaxIconCacheFiles = 1000;

	private const int MaxIconCacheAgeDays = 15;

	private const int CacheCleanupIntervalMs = 1800000;

	private const int CacheCleanupEveryNSaves = 2000;

	private static readonly object _cacheCleanupLock = new object();

	private static Timer? _cacheCleanupTimer;

	private static int _iconCacheSaveCounter;

	private static readonly ConcurrentDictionary<string, DateTime> _lastRetryTouchUtc = new ConcurrentDictionary<string, DateTime>();

	private const int RetryTouchCooldownMinutes = 5;

	private static readonly string LastGoodDir = Path.Combine(App.GetAppDataDir(), "IconCache", "lastgood");

	private const int MaxMemoryCacheEntries = 512;

	private static readonly Dictionary<string, LinkedListNode<MemCacheEntry>> _memCache = new Dictionary<string, LinkedListNode<MemCacheEntry>>(StringComparer.Ordinal);

	private static readonly LinkedList<MemCacheEntry> _memLru = new LinkedList<MemCacheEntry>();

	private static readonly object _memCacheLock = new object();

	private static DateTime _lastCacheClear = DateTime.MinValue;

	private static readonly object _cacheClearLock = new object();

	private static readonly DateTime _appStartTime = DateTime.UtcNow;

	private const int StartupGracePeriodSeconds = 60;

	private static bool IsDesktopDirectory(string dirPath)
	{
		if (_desktopDirectories.Count == 0)
		{
			try
			{
				AddDir(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
				AddDir(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
				AddDir(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
			}
			catch
			{
			}
		}
		return _desktopDirectories.Contains(dirPath);
		static void AddDir(string? p)
		{
			if (!string.IsNullOrEmpty(p))
			{
				_desktopDirectories.Add(p);
			}
		}
	}

	public static bool IsImageFile(string filePath)
	{
		string ext = Path.GetExtension(filePath);
		if (!string.IsNullOrEmpty(ext))
		{
			return ImageExtensions.Contains(ext);
		}
		return false;
	}

	private static void LogIcon(string msg)
	{
		try
		{
			lock (_logLock)
			{
				if (!_logHeaderWritten)
				{
					File.WriteAllText(_logPath, $"=== DesktopTool Icon Debug Log === {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n");
					_logHeaderWritten = true;
				}
				LogHelper.Append(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
			}
		}
		catch
		{
		}
	}

	public static void LogIconDiagnostic(string msg)
	{
		LogIcon(msg);
	}

	[DllImport("shell32.dll", CharSet = CharSet.Auto)]
	private static extern nint SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeInfo, uint uFlags);

	[DllImport("user32.dll")]
	private static extern bool DestroyIcon(nint hIcon);

	[DllImport("user32.dll")]
	private static extern bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

	[DllImport("gdi32.dll")]
	private static extern int GetObject(nint hgdiobj, int cbBuffer, out BITMAP lpbm);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteObject(nint hObject);

	[DllImport("gdi32.dll")]
	private static extern int GetDIBits(nint hdc, nint hbmp, uint start, uint cLines, [Out] byte[] lpvBits, ref BITMAPINFO lpbmi, uint usage);

	[DllImport("user32.dll")]
	private static extern nint GetDC(nint hwnd);

	[DllImport("user32.dll")]
	private static extern int ReleaseDC(nint hwnd, nint hdc);

	[DllImport("shell32.dll", CharSet = CharSet.Auto)]
	private static extern int ExtractIconEx(string lpszFile, int nIconIndex, out nint phiconLarge, out nint phiconSmall, int nIcons);

	[DllImport("shell32.dll")]
	private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
	private static extern void SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath, nint pbc, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

	private static BitmapSource? GetIconViaShellItemImageFactory(string path, int targetSize, bool iconOnly, bool scaleUp = false)
	{
		try
		{
			double dpiScale = GetSystemDpi().dpiX / 96.0;
			int phys = (int)Math.Round((double)targetSize * dpiScale);
			if (phys < 16)
			{
				phys = ((targetSize > 0) ? targetSize : 48);
			}
			int request = (scaleUp ? phys : Math.Max(phys, 256));
			Guid iid = typeof(IShellItemImageFactory).GUID;
			SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
			if (factory == null)
			{
				return null;
			}
			try
			{
				SIZE size = new SIZE
				{
					cx = request,
					cy = request
				};
				int flags = 1 | (scaleUp ? 2 : 0) | (iconOnly ? 4 : 0);
				nint hBitmap;
				int hr = factory.GetImage(size, flags, out hBitmap);
				if (hr != 0 || hBitmap == IntPtr.Zero)
				{
					LogIcon($"  ShellItemImage: GetImage hr=0x{hr:X} for {Path.GetFileName(path)}");
					return null;
				}
				try
				{
					BitmapSource bs = HBitmapToBitmapSource(hBitmap);
					if (bs == null)
					{
						return null;
					}
					bs = CropTransparentPadding(bs);
					if (bs.PixelWidth != phys || bs.PixelHeight != phys)
					{
						bs = ResizeToSize(bs, targetSize);
					}
					bs.Freeze();
					LogIcon($"  ShellItemImage: OK {Path.GetFileName(path)} → {bs.PixelWidth}x{bs.PixelHeight}");
					return bs;
				}
				finally
				{
					DeleteObject(hBitmap);
				}
			}
			finally
			{
				Marshal.ReleaseComObject(factory);
			}
		}
		catch (Exception ex)
		{
			LogIcon("  ShellItemImage: EXCEPTION " + ex.Message + " for " + Path.GetFileName(path));
			return null;
		}
	}

	private static BitmapSource? HBitmapToBitmapSource(nint hBitmap)
	{
		try
		{
			GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var bmp);
			int width = bmp.bmWidth;
			int height = Math.Abs(bmp.bmHeight);
			if (width <= 0 || height <= 0)
			{
				return null;
			}
			byte[] pixels = new byte[width * height * 4];
			BITMAPINFO bi = new BITMAPINFO
			{
				bmiHeader = 
				{
					biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
					biWidth = width,
					biHeight = -height,
					biPlanes = 1,
					biBitCount = 32,
					biCompression = 0u
				}
			};
			nint hdc = GetDC(IntPtr.Zero);
			try
			{
				if (GetDIBits(hdc, hBitmap, 0u, (uint)height, pixels, ref bi, 0u) == 0)
				{
					return null;
				}
			}
			finally
			{
				ReleaseDC(IntPtr.Zero, hdc);
			}
			bool anyAlpha = false;
			for (int i = 3; i < pixels.Length; i += 4)
			{
				if (pixels[i] != 0)
				{
					anyAlpha = true;
					break;
				}
			}
			if (!anyAlpha)
			{
				for (int j = 3; j < pixels.Length; j += 4)
				{
					pixels[j] = byte.MaxValue;
				}
			}
			(double dpiX, double dpiY) systemDpi = GetSystemDpi();
			double dpiX = systemDpi.dpiX;
			double dpiY = systemDpi.dpiY;
			BitmapSource bitmapSource = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Pbgra32, null, pixels, width * 4);
			bitmapSource.Freeze();
			return bitmapSource;
		}
		catch (Exception ex)
		{
			LogIcon("  HBitmapToBitmapSource: ERROR " + ex.Message);
			return null;
		}
	}

	public static BitmapSource? GetFileIcon(string filePath, bool large = true, double displaySize = 0.0)
	{
		int size = ((displaySize > 0.0) ? ((int)Math.Ceiling(Math.Max(displaySize, 16.0))) : (large ? 64 : 48));
		string memKey = (IsGenericTypeExtensionIcon(filePath) ? $"EXT|{Path.GetExtension(filePath).ToLowerInvariant()}|{size:D4}" : ComputeCacheKey(filePath, size));
		BitmapSource mem = TryGetFromMemoryCache(memKey);
		if (mem != null)
		{
			LogIcon($"GetFileIcon: MEM HIT {Path.GetFileName(filePath)} ({mem.PixelWidth}x{mem.PixelHeight})");
			return mem;
		}
		string cacheKey = ComputeCacheKey(filePath, size);
		if (!string.IsNullOrEmpty(cacheKey))
		{
			BitmapSource cached = LoadIconFromCache(cacheKey);
			if (cached != null)
			{
				LogIcon($"GetFileIcon: CACHE HIT {Path.GetFileName(filePath)} ({cached.PixelWidth}x{cached.PixelHeight})");
				AddToMemoryCache(memKey, cached);
				return cached;
			}
		}
		BitmapSource result = GetFileIconInternal(filePath, large, displaySize);
		if (result != null)
		{
			if (IsBlankOrWhiteIcon(result))
			{
				BitmapSource recovered = RecoverNonWhiteIcon(filePath, cacheKey, size);
				if (recovered != null)
				{
					LogIcon($"GetFileIcon: recovered non-white from cache/lastgood {Path.GetFileName(filePath)} ({recovered.PixelWidth}x{recovered.PixelHeight})");
					result = recovered;
				}
				else
				{
					LogIcon("GetFileIcon: NOT cached (blank/white) " + Path.GetFileName(filePath));
				}
			}
			if (result != null && !IsBlankOrWhiteIcon(result))
			{
				if (!string.IsNullOrEmpty(cacheKey))
				{
					SaveIconToCache(cacheKey, result);
				}
				AddToMemoryCache(memKey, result);
				SaveLastGoodIcon(filePath, result, size);
			}
		}
		return result;
	}

	public static BitmapSource? GetFileIconSafe(string filePath, bool large = true, double displaySize = 0.0)
	{
		int size = ((displaySize > 0.0) ? ((int)Math.Ceiling(Math.Max(displaySize, 16.0))) : (large ? 64 : 48));
		BitmapSource first = GetFileIcon(filePath, large, displaySize);
		if (first != null && !IsBlankOrWhiteIcon(first))
		{
			return first;
		}
		BitmapSource lg = LoadLastGoodIcon(filePath, size);
		if (lg != null)
		{
			LogIcon("GetFileIconSafe: served last-good icon for " + Path.GetFileName(filePath));
			return lg;
		}
		LogIcon("GetFileIconSafe: first attempt blank/white for " + Path.GetFileName(filePath) + ", re-extracting...");
		try
		{
			InvalidateIconCache(filePath);
		}
		catch
		{
		}
		BitmapSource second = GetFileIcon(filePath, large, displaySize);
		if (second != null && !IsBlankOrWhiteIcon(second))
		{
			return second;
		}
		BitmapSource generic = GetGenericFileTypeIcon(filePath, size);
		if (generic != null && !IsBlankOrWhiteIcon(generic))
		{
			LogIcon("GetFileIconSafe: fell back to generic type icon for " + Path.GetFileName(filePath));
			return generic;
		}
		return LoadLastGoodIcon(filePath, size) ?? first ?? second ?? GetDefaultIcon(size);
	}

	private static (string? iconFile, int iconIndex) ParseUrlFileIcon(string urlFilePath)
	{
		try
		{
			string[] array = File.ReadAllLines(urlFilePath);
			bool inInternetShortcut = false;
			string iconFile = null;
			int iconIndex = 0;
			string[] array2 = array;
			for (int i = 0; i < array2.Length; i++)
			{
				string trimmed = array2[i].Trim();
				if (trimmed.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase))
				{
					inInternetShortcut = true;
				}
				else if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
				{
					inInternetShortcut = false;
				}
				else if (inInternetShortcut)
				{
					if (trimmed.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
					{
						iconFile = trimmed.Substring("IconFile=".Length).Trim();
					}
					else if (trimmed.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
					{
						int.TryParse(trimmed.Substring("IconIndex=".Length).Trim(), out iconIndex);
					}
				}
			}
			if (!string.IsNullOrEmpty(iconFile))
			{
				iconFile = Environment.ExpandEnvironmentVariables(iconFile);
				LogIcon($"  .url IconFile={iconFile} IconIndex={iconIndex}");
				return (iconFile: iconFile, iconIndex: iconIndex);
			}
		}
		catch (Exception ex)
		{
			LogIcon("  .url parse failed: " + ex.Message);
		}
		return (iconFile: null, iconIndex: 0);
	}

	private static string? ReadUrlTarget(string urlFilePath)
	{
		try
		{
			string[] array = File.ReadAllLines(urlFilePath);
			for (int i = 0; i < array.Length; i++)
			{
				string line = array[i].Trim();
				if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
				{
					return line.Substring("URL=".Length).Trim();
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static int ParseSteamAppId(string steamUrl)
	{
		Match m = Regex.Match(steamUrl, "steam://(?:rungameid|run|launch|store|app)/(\\d+)", RegexOptions.IgnoreCase);
		if (m.Success && int.TryParse(m.Groups[1].Value, out var id))
		{
			return id;
		}
		return 0;
	}

	private static string? GetSteamInstallPath()
	{
		string path = null;
		try
		{
			using RegistryKey lm = Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\Valve\\Steam");
			path = lm?.GetValue("InstallPath") as string;
		}
		catch
		{
		}
		if (string.IsNullOrEmpty(path))
		{
			try
			{
				using RegistryKey hkcu = Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam");
				path = hkcu?.GetValue("SteamPath") as string;
			}
			catch
			{
			}
		}
		if (string.IsNullOrEmpty(path))
		{
			try
			{
				using RegistryKey lm2 = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Valve\\Steam");
				path = lm2?.GetValue("InstallPath") as string;
			}
			catch
			{
			}
		}
		if (!string.IsNullOrEmpty(path))
		{
			return path;
		}
		return null;
	}

	private static BitmapSource? LoadBitmapFromFile(string imagePath, int size)
	{
		try
		{
			BitmapImage bi = new BitmapImage();
			bi.BeginInit();
			bi.CacheOption = BitmapCacheOption.OnLoad;
			bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
			bi.UriSource = new Uri(imagePath, UriKind.Absolute);
			if (size > 0)
			{
				bi.DecodePixelWidth = size;
				bi.DecodePixelHeight = size;
			}
			bi.EndInit();
			if (bi.CanFreeze)
			{
				bi.Freeze();
			}
			return bi;
		}
		catch
		{
			return null;
		}
	}

	private static string? ParseAcfValue(string acfPath, string key)
	{
		try
		{
			string[] array = File.ReadAllLines(acfPath);
			for (int i = 0; i < array.Length; i++)
			{
				string line = array[i].Trim();
				int idx = line.IndexOf('"');
				if (idx < 0)
				{
					continue;
				}
				int idx2 = line.IndexOf('"', idx + 1);
				if (idx2 < 0 || !line.Substring(idx + 1, idx2 - idx - 1).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				int v1 = line.IndexOf('"', idx2 + 1);
				if (v1 < 0)
				{
					continue;
				}
				int v2 = line.IndexOf('"', v1 + 1);
				if (v2 >= 0)
				{
					string v3 = line.Substring(v1 + 1, v2 - v1 - 1).Trim();
					if (!string.IsNullOrEmpty(v3))
					{
						return v3;
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static string? FindMainExecutable(string gameDir)
	{
		if (!Directory.Exists(gameDir))
		{
			return null;
		}
		string[] blacklist;
		try
		{
			string dirName = Path.GetFileName(gameDir.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;
			string dirKey = dirName.Replace(" ", "").ToLowerInvariant();
			blacklist = new string[19]
			{
				"crashpad", "crashhandler", "installer", "setup", "_commonredist", "vc_redist", "vcredist", "dxwebsetup", "dxsetup", "redist",
				"easyanticheat", "eac", "bethesdanetlauncher", "epicgameslauncher", "steam_redistributable", "dotnet", "unitycrashhandler", "unrealcrashhandler", "launcher"
			};
			string exact = null;
			string partial = null;
			string shortest = null;
			foreach (string item in Directory.EnumerateFiles(gameDir, "*.exe", SearchOption.TopDirectoryOnly))
			{
				Consider(item);
			}
			if (exact == null && partial == null)
			{
				foreach (string item2 in Directory.EnumerateFiles(gameDir, "*.exe", SearchOption.AllDirectories))
				{
					Consider(item2);
				}
			}
			return exact ?? partial ?? shortest;
			void Consider(string exe)
			{
				string name = Path.GetFileNameWithoutExtension(exe) ?? string.Empty;
				if (!IsHelper(name))
				{
					if (exact == null && name.Equals(dirName, StringComparison.OrdinalIgnoreCase))
					{
						exact = exe;
					}
					if (partial == null && dirKey.Length > 0 && name.Replace(" ", "").ToLowerInvariant().Contains(dirKey))
					{
						partial = exe;
					}
					if (shortest == null || Path.GetFileName(exe).Length < Path.GetFileName(shortest).Length)
					{
						shortest = exe;
					}
				}
			}
		}
		catch
		{
			return null;
		}
		bool IsHelper(string n)
		{
			string l = n.ToLowerInvariant();
			if (l.Contains("uninstall"))
			{
				return true;
			}
			string[] array = blacklist;
			foreach (string b in array)
			{
				if (l.Contains(b))
				{
					return true;
				}
			}
			return false;
		}
	}

	private static IEnumerable<string> EnumerateSteamLibraryRoots(string steamPath)
	{
		yield return steamPath;
		string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
		if (!File.Exists(vdf))
		{
			yield break;
		}
		List<string> extra = new List<string>();
		try
		{
			foreach (string item in File.ReadLines(vdf))
			{
				string line = item.Trim();
				if (line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}
				int v1 = line.IndexOf('"', line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase) + 6);
				if (v1 < 0)
				{
					continue;
				}
				int v2 = line.IndexOf('"', v1 + 1);
				if (v2 >= 0)
				{
					string p = line.Substring(v1 + 1, v2 - v1 - 1).Replace("\\\\", "\\").Trim();
					if (!string.IsNullOrEmpty(p) && Directory.Exists(p) && !p.Equals(steamPath, StringComparison.OrdinalIgnoreCase))
					{
						extra.Add(p);
					}
				}
			}
		}
		catch
		{
		}
		foreach (string item2 in extra)
		{
			yield return item2;
		}
	}

	private static BitmapSource? TryResolveSteamUrlIcon(string urlFilePath, int size)
	{
		try
		{
			string url = ReadUrlTarget(urlFilePath);
			if (string.IsNullOrEmpty(url) || !url.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			int appId = ParseSteamAppId(url);
			if (appId <= 0)
			{
				return null;
			}
			string steamPath = GetSteamInstallPath();
			if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
			{
				return null;
			}
			string libCacheDir = Path.Combine(steamPath, "appcache", "librarycache");
			string libIcon = Path.Combine(libCacheDir, $"{appId}_icon.jpg");
			if (File.Exists(libIcon))
			{
				BitmapSource bmp = LoadBitmapFromFile(libIcon, size);
				if (bmp != null && !IsBlankOrWhiteIcon(bmp))
				{
					return bmp;
				}
			}
			string libSubDir = Path.Combine(libCacheDir, appId.ToString());
			if (Directory.Exists(libSubDir))
			{
				try
				{
					foreach (string f in Directory.EnumerateFiles(libSubDir))
					{
						if (Path.GetFileName(f).IndexOf("icon", StringComparison.OrdinalIgnoreCase) < 0)
						{
							continue;
						}
						string ext0 = Path.GetExtension(f).ToLowerInvariant();
						if (!(ext0 != ".jpg") || !(ext0 != ".png") || !(ext0 != ".ico"))
						{
							BitmapSource bmp2 = LoadBitmapFromFile(f, size);
							if (bmp2 != null && !IsBlankOrWhiteIcon(bmp2))
							{
								return bmp2;
							}
						}
					}
				}
				catch
				{
				}
			}
			foreach (string libRoot in EnumerateSteamLibraryRoots(steamPath))
			{
				string manifest = Path.Combine(libRoot, "steamapps", $"appmanifest_{appId}.acf");
				if (File.Exists(manifest))
				{
					string installdir = ParseAcfValue(manifest, "installdir");
					if (!string.IsNullOrEmpty(installdir))
					{
						string exe = FindMainExecutable(Path.Combine(libRoot, "steamapps", "common", installdir));
						if (exe != null)
						{
							BitmapSource emb = GetIconViaShellItemImageFactory(exe, size, iconOnly: true);
							if (emb != null && !IsBlankOrWhiteIcon(emb))
							{
								return emb;
							}
							BitmapSource ext1 = TryExtractEmbeddedIcon(exe, size);
							if (ext1 != null && !IsBlankOrWhiteIcon(ext1))
							{
								return ext1;
							}
							break;
						}
						break;
					}
					break;
				}
			}
			string steamExe = Path.Combine(steamPath, "steam.exe");
			if (File.Exists(steamExe))
			{
				BitmapSource sIcon = GetIconViaShellItemImageFactory(steamExe, size, iconOnly: true);
				if (sIcon != null && !IsBlankOrWhiteIcon(sIcon))
				{
					return sIcon;
				}
			}
		}
		catch (Exception ex)
		{
			LogIcon("  TryResolveSteamUrlIcon failed: " + ex.Message);
		}
		return null;
	}

	private static BitmapSource? GetFileIconInternal(string filePath, bool large = true, double displaySize = 0.0)
	{
		string resolvedPath = filePath;
		bool isShortcut = false;
		if (Path.GetExtension(filePath).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
		{
			isShortcut = true;
			string target = ResolveShortcutTarget(filePath);
			if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)))
			{
				resolvedPath = target;
			}
		}
		string urlIconPath = null;
		if (Path.GetExtension(filePath).Equals(".url", StringComparison.OrdinalIgnoreCase))
		{
			urlIconPath = ParseUrlFileIcon(filePath).iconFile;
		}
		if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
		{
			return GetDefaultIcon((displaySize > 0.0) ? ((int)Math.Ceiling(displaySize)) : 48);
		}
		bool hadHS = false;
		bool hsRemovalFailed = false;
		string extractPath = resolvedPath;
		try
		{
			if (File.Exists(resolvedPath) || Directory.Exists(resolvedPath))
			{
				FileAttributes attrs = File.GetAttributes(resolvedPath);
				hadHS = (attrs & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System);
				if (hadHS)
				{
					try
					{
						File.SetAttributes(resolvedPath, attrs & ~(FileAttributes.Hidden | FileAttributes.System));
						if ((File.GetAttributes(resolvedPath) & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System))
						{
							hsRemovalFailed = true;
							LogIcon("  H+S removal FAILED (verified still set) for " + Path.GetFileName(resolvedPath));
						}
						else
						{
							LogIcon("  H+S removed temporarily for icon extraction: " + Path.GetFileName(resolvedPath));
						}
					}
					catch (Exception ex)
					{
						hsRemovalFailed = true;
						LogIcon($"  H+S removal FAILED ({ex.GetType().Name}: {ex.Message}) for {Path.GetFileName(resolvedPath)}");
					}
				}
			}
		}
		catch
		{
		}
		if (hsRemovalFailed && File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
		{
			try
			{
				string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + Path.GetExtension(resolvedPath));
				File.Copy(resolvedPath, tmp, overwrite: true);
				try
				{
					File.SetAttributes(tmp, File.GetAttributes(tmp) & ~(FileAttributes.Hidden | FileAttributes.System));
				}
				catch
				{
				}
				extractPath = tmp;
				LogIcon("  Using temp copy for icon extraction: " + tmp);
			}
			catch (Exception ex2)
			{
				LogIcon("  temp copy fallback failed: " + ex2.Message);
			}
		}
		bool canRetry = !_iconRetryInProgress;
		try
		{
			int size = ((displaySize > 0.0) ? ((int)Math.Ceiling(Math.Max(displaySize, 16.0))) : (large ? 64 : 48));
			LogIcon($"GetFileIcon: path={Path.GetFileName(filePath)}, displaySize={displaySize:F0}, targetSize={size}");
			if (Directory.Exists(extractPath))
			{
				return GetFolderIconViaSHGetFileInfo(extractPath, size);
			}
			if (IsImageFile(extractPath))
			{
				return GetImageThumbnail(extractPath, size);
			}
			if (IsEmbeddedIconBinary(extractPath))
			{
				BitmapSource shellEmb = GetIconViaShellItemImageFactory(extractPath, size, iconOnly: true);
				if (shellEmb != null && !IsBlankOrWhiteIcon(shellEmb))
				{
					return shellEmb;
				}
				BitmapSource embedded = TryExtractEmbeddedIcon(extractPath, size);
				if (embedded != null && !IsBlankOrWhiteIcon(embedded))
				{
					return embedded;
				}
			}
			if (Path.GetExtension(filePath).Equals(".url", StringComparison.OrdinalIgnoreCase))
			{
				bool iconFileIsSteamExe = urlIconPath != null && Path.GetFileName(urlIconPath).Equals("steam.exe", StringComparison.OrdinalIgnoreCase);
				if (urlIconPath != null && !iconFileIsSteamExe && Path.GetExtension(urlIconPath).Equals(".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(urlIconPath))
				{
					BitmapSource preferred = TryExtractHighResIcoIcon(urlIconPath, Math.Max(size, 256));
					if (preferred != null && !IsBlankOrWhiteIcon(preferred))
					{
						LogIcon("  .url IconFile(.ico) hit: " + Path.GetFileName(filePath));
						return NormalizeAppIcon(preferred, size);
					}
				}
				BitmapSource steamIcon = TryResolveSteamUrlIcon(filePath, size);
				if (steamIcon != null)
				{
					LogIcon("  .url steam resolver hit (fallback): " + Path.GetFileName(filePath));
					return NormalizeAppIcon(steamIcon, size);
				}
				if (urlIconPath != null && !iconFileIsSteamExe && File.Exists(urlIconPath))
				{
					BitmapSource preferredExt = TryExtractEmbeddedIcon(urlIconPath, size);
					if (preferredExt != null && !IsBlankOrWhiteIcon(preferredExt))
					{
						return NormalizeAppIcon(preferredExt, size);
					}
					BitmapSource preferred2 = GetIconViaShellItemImageFactory(urlIconPath, size, iconOnly: true);
					if (preferred2 != null && !IsBlankOrWhiteIcon(preferred2))
					{
						return NormalizeAppIcon(preferred2, size);
					}
				}
			}
			if (urlIconPath != null && File.Exists(urlIconPath))
			{
				BitmapSource urlEmb = GetIconViaShellItemImageFactory(urlIconPath, size, iconOnly: true);
				if (urlEmb != null && !IsBlankOrWhiteIcon(urlEmb))
				{
					return NormalizeAppIcon(urlEmb, size);
				}
				BitmapSource urlExtracted = TryExtractEmbeddedIcon(urlIconPath, size);
				if (urlExtracted != null && !IsBlankOrWhiteIcon(urlExtracted))
				{
					return NormalizeAppIcon(urlExtracted, size);
				}
				LogIcon("  .url IconFile extraction failed for " + Path.GetFileName(urlIconPath));
			}
			if (isShortcut)
			{
				bool fileHadHS = false;
				try
				{
					FileAttributes fileAttrs = File.GetAttributes(filePath);
					if ((fileAttrs & FileAttributes.Hidden) == FileAttributes.Hidden && (fileAttrs & FileAttributes.System) == FileAttributes.System)
					{
						fileHadHS = true;
						File.SetAttributes(filePath, fileAttrs & ~(FileAttributes.Hidden | FileAttributes.System));
					}
				}
				catch
				{
				}
				BitmapSource selfIcon = null;
				try
				{
					selfIcon = GetIconViaShellItemImageFactory(filePath, Math.Max(size, 256), iconOnly: true);
				}
				catch (Exception ex3)
				{
					LogIcon("  .lnk self-icon extraction failed: " + ex3.Message);
					selfIcon = null;
				}
				finally
				{
					if (fileHadHS)
					{
						try
						{
							FileAttributes a = File.GetAttributes(filePath);
							File.SetAttributes(filePath, a | FileAttributes.Hidden | FileAttributes.System);
						}
						catch
						{
						}
					}
				}
				if (selfIcon != null && !IsBlankOrWhiteIcon(selfIcon))
				{
					LogIcon("  .lnk self-icon extracted (shortcut-consistent): " + Path.GetFileName(filePath));
					return NormalizeAppIcon(selfIcon, size);
				}
				LogIcon("  .lnk self-icon blank/failed, falling back to target resolution");
			}
			BitmapSource hqIcon = GetHighQualityIconFromSystem(extractPath, 0u, size);
			if (hqIcon != null)
			{
				return hqIcon;
			}
			BitmapSource shIcon = GetIconViaSHGetFileInfo(extractPath, size);
			if (shIcon != null)
			{
				return shIcon;
			}
			BitmapSource fallbackIcon = null;
			if (isShortcut && resolvedPath != filePath)
			{
				fallbackIcon = GetHighQualityIconFromSystem(filePath, 0u, size) ?? GetIconViaSHGetFileInfo(filePath, size);
			}
			Icon icon = Icon.ExtractAssociatedIcon(extractPath);
			if (icon == null)
			{
				if (fallbackIcon != null)
				{
					if (IsBlankOrWhiteIcon(fallbackIcon) && canRetry)
					{
						LogIcon("  GetFileIcon: fallback is blank, retrying with cache clear...");
						return RetryGetFileIcon(filePath, large, displaySize);
					}
					return fallbackIcon;
				}
				if (isShortcut && resolvedPath != filePath)
				{
					Icon lnkIcon = Icon.ExtractAssociatedIcon(filePath);
					if (lnkIcon != null)
					{
						BitmapSource lnkBs = Imaging.CreateBitmapSourceFromHIcon(lnkIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
						lnkIcon.Dispose();
						lnkBs = SmartResizeOrPad(lnkBs, size);
						lnkBs.Freeze();
						if (IsBlankOrWhiteIcon(lnkBs) && canRetry)
						{
							LogIcon("  GetFileIcon: lnk icon is blank, retrying with cache clear...");
							return RetryGetFileIcon(filePath, large, displaySize);
						}
						return lnkBs;
					}
				}
				BitmapSource genericIcon = GetGenericFileTypeIcon(extractPath, size);
				if (genericIcon != null)
				{
					return genericIcon;
				}
				if (isShortcut && resolvedPath == filePath)
				{
					genericIcon = GetGenericFileTypeIcon(filePath, size);
					if (genericIcon != null)
					{
						return genericIcon;
					}
				}
				if (canRetry)
				{
					LogIcon("  GetFileIcon: all methods returned null, retrying with cache clear...");
					return RetryGetFileIcon(filePath, large, displaySize);
				}
				return GetDefaultIcon(size);
			}
			BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			icon.Dispose();
			bs = SmartResizeOrPad(bs, size);
			bs.Freeze();
			if (IsBlankOrWhiteIcon(bs) && canRetry)
			{
				LogIcon("  GetFileIcon: ExtractAssociatedIcon result is blank, retrying with cache clear...");
				return RetryGetFileIcon(filePath, large, displaySize);
			}
			return bs;
		}
		catch
		{
			if (canRetry)
			{
				LogIcon("  GetFileIcon: exception occurred, retrying with cache clear...");
				return RetryGetFileIcon(filePath, large, displaySize);
			}
			return GetDefaultIcon((displaySize > 0.0) ? ((int)Math.Ceiling(displaySize)) : 48);
		}
		finally
		{
			if (hadHS && !hsRemovalFailed)
			{
				try
				{
					if (File.Exists(resolvedPath) || Directory.Exists(resolvedPath))
					{
						FileAttributes restoreAttrs = File.GetAttributes(resolvedPath);
						File.SetAttributes(resolvedPath, restoreAttrs | FileAttributes.Hidden | FileAttributes.System);
						LogIcon("  H+S restored after icon extraction: " + Path.GetFileName(resolvedPath));
					}
				}
				catch (Exception ex4)
				{
					LogIcon("  Failed to restore H+S after extraction: " + ex4.Message);
				}
			}
			if (extractPath != resolvedPath)
			{
				try
				{
					File.Delete(extractPath);
				}
				catch
				{
				}
			}
		}
	}

	private static BitmapSource? RetryGetFileIcon(string filePath, bool large, double displaySize)
	{
		if (_iconRetryInProgress)
		{
			return null;
		}
		_iconRetryInProgress = true;
		try
		{
			LogIcon("  RetryGetFileIcon: START for " + Path.GetFileName(filePath));
			try
			{
				if (File.Exists(filePath))
				{
					DateTime nowUtc = DateTime.UtcNow;
					bool shouldTouch;
					if (_lastRetryTouchUtc.TryGetValue(filePath, out var lastTouch) && (nowUtc - lastTouch).TotalMinutes < 5.0)
					{
						shouldTouch = false;
					}
					else
					{
						_lastRetryTouchUtc[filePath] = nowUtc;
						shouldTouch = true;
					}
					if (shouldTouch)
					{
						File.SetLastWriteTime(filePath, DateTime.Now);
						LogIcon("  RetryGetFileIcon: touched file timestamp");
					}
					else
					{
						LogIcon($"  RetryGetFileIcon: touch skipped (cooldown {5}min)");
					}
					if (filePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
					{
						if (IsInStartupGracePeriod())
						{
							LogIcon("  RetryGetFileIcon: SHCNE_CREATE SKIPPED (startup grace period)");
						}
						else
						{
							nint ptr = Marshal.StringToHGlobalUni(filePath);
							NativeMethods.SHChangeNotify(2u, 5u, ptr, IntPtr.Zero);
							Marshal.FreeHGlobal(ptr);
							LogIcon("  RetryGetFileIcon: SHCNE_CREATE sent for .lnk");
						}
					}
				}
			}
			catch (Exception ex)
			{
				LogIcon("  RetryGetFileIcon: touch failed: " + ex.Message);
			}
			try
			{
				string parentDir = Path.GetDirectoryName(filePath);
				if (!string.IsNullOrEmpty(parentDir) && !IsDesktopDirectory(parentDir))
				{
					nint ptr2 = Marshal.StringToHGlobalUni(parentDir);
					NativeMethods.SHChangeNotify(4096u, 5u, ptr2, IntPtr.Zero);
					Marshal.FreeHGlobal(ptr2);
					LogIcon("  RetryGetFileIcon: SHCNE_UPDATEDIR sent to " + Path.GetFileName(parentDir));
				}
				else if (!string.IsNullOrEmpty(parentDir))
				{
					LogIcon("  RetryGetFileIcon: SHCNE_UPDATEDIR skipped (Desktop dir, Win11 would open folder)");
				}
			}
			catch (Exception ex2)
			{
				LogIcon("  RetryGetFileIcon: UPDATEDIR failed: " + ex2.Message);
			}
			ClearSystemIconCache();
			Thread.Sleep(200);
			LogIcon("  RetryGetFileIcon: retrying GetFileIcon...");
			return GetFileIcon(filePath, large, displaySize);
		}
		finally
		{
			_iconRetryInProgress = false;
		}
	}

	private static BitmapSource? TryGetFolderCustomIconFromIni(string folderPath, int targetSize)
	{
		try
		{
			string iniPath = Path.Combine(folderPath, "desktop.ini");
			if (!File.Exists(iniPath))
			{
				return null;
			}
			string iconResource = null;
			string iconFile = null;
			int iconIndex = 0;
			bool inShell = false;
			string[] array = File.ReadAllLines(iniPath);
			for (int i = 0; i < array.Length; i++)
			{
				string line = array[i].Trim();
				if (line.StartsWith("[", StringComparison.OrdinalIgnoreCase))
				{
					inShell = line.Equals("[.ShellClassInfo]", StringComparison.OrdinalIgnoreCase);
				}
				else
				{
					if (!inShell)
					{
						continue;
					}
					if (line.StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase))
					{
						string v = line.Substring("IconResource=".Length).Trim();
						if (v.Length > 0 && !v.Equals("*", StringComparison.OrdinalIgnoreCase))
						{
							iconResource = v;
						}
					}
					else if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
					{
						iconFile = line.Substring("IconFile=".Length).Trim();
					}
					else if (line.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
					{
						int.TryParse(line.Substring("IconIndex=".Length).Trim(), out iconIndex);
					}
				}
			}
			string res = iconResource ?? iconFile;
			if (string.IsNullOrEmpty(res))
			{
				return null;
			}
			string iconPath = res;
			int idx = iconIndex;
			int comma = res.LastIndexOf(',');
			if (comma > 0 && int.TryParse(res.Substring(comma + 1).Trim(), out var parsedIdx))
			{
				iconPath = res.Substring(0, comma);
				idx = parsedIdx;
			}
			iconPath = Environment.ExpandEnvironmentVariables(iconPath).Trim().Trim('"');
			if (string.IsNullOrEmpty(iconPath))
			{
				return null;
			}
			if (!Path.IsPathRooted(iconPath))
			{
				iconPath = Path.Combine(folderPath, iconPath);
			}
			if (!File.Exists(iconPath))
			{
				LogIcon("  IniCustomIcon: target not found " + iconPath);
				return null;
			}
			LogIcon($"  IniCustomIcon: folder={Path.GetFileName(folderPath)} res={iconPath} idx={idx}");
			BitmapSource bs = null;
			string ext = Path.GetExtension(iconPath);
			bs = ((!ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) && !ImageExtensions.Contains(ext) && !ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)) ? ExtractIconByIndex(iconPath, idx, targetSize) : LoadImageFileIcon(iconPath, targetSize));
			if (bs == null)
			{
				return null;
			}
			bs = CropTransparentPadding(bs);
			if (bs.PixelWidth != targetSize || bs.PixelHeight != targetSize)
			{
				bs = ResizeToSize(bs, targetSize);
			}
			bs.Freeze();
			if (IsBlankOrWhiteIcon(bs))
			{
				LogIcon("  IniCustomIcon: BLANK, discard");
				return null;
			}
			LogIcon($"  IniCustomIcon: OK {Path.GetFileName(folderPath)} → {bs.PixelWidth}x{bs.PixelHeight}");
			return bs;
		}
		catch (Exception ex)
		{
			LogIcon("  IniCustomIcon: EX " + ex.Message);
			return null;
		}
	}

	private static BitmapSource? LoadImageFileIcon(string path, int targetSize)
	{
		try
		{
			double dpiX = GetSystemDpi().dpiX;
			double dpiScale = dpiX / 96.0;
			int physTarget = (int)Math.Round((double)targetSize * dpiScale);
			if (physTarget <= 0)
			{
				physTarget = targetSize;
			}
			BitmapImage bi = new BitmapImage();
			using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			bi.BeginInit();
			bi.CacheOption = BitmapCacheOption.OnLoad;
			bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
			bi.StreamSource = fs;
			bi.DecodePixelWidth = physTarget;
			bi.DecodePixelHeight = physTarget;
			bi.EndInit();
			bi.Freeze();
			return ResizeToSize(bi, targetSize);
		}
		catch (Exception ex)
		{
			LogIcon("  LoadImageFileIcon: EX " + ex.Message + " for " + Path.GetFileName(path));
			return null;
		}
	}

	private static BitmapSource? ExtractIconByIndex(string path, int index, int targetSize)
	{
		try
		{
			nint hLarge;
			nint hSmall;
			int n = ExtractIconEx(path, index, out hLarge, out hSmall, 1);
			try
			{
				if (hLarge != IntPtr.Zero)
				{
					BitmapSource bs = HIconToFullResBitmapSource(hLarge) ?? Imaging.CreateBitmapSourceFromHIcon(hLarge, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
					if (bs != null)
					{
						bs = CropTransparentPadding(bs);
						if (bs.PixelWidth != targetSize || bs.PixelHeight != targetSize)
						{
							bs = ResizeToSize(bs, targetSize);
						}
						bs.Freeze();
						return bs;
					}
				}
				LogIcon($"  ExtractIconByIndex: no large icon (n={n}) for {Path.GetFileName(path)}");
			}
			finally
			{
				if (hLarge != IntPtr.Zero)
				{
					DestroyIcon(hLarge);
				}
				if (hSmall != IntPtr.Zero)
				{
					DestroyIcon(hSmall);
				}
			}
		}
		catch (Exception ex)
		{
			LogIcon("  ExtractIconByIndex: EX " + ex.Message + " for " + Path.GetFileName(path));
		}
		return null;
	}

	private static BitmapSource? GetFolderIconViaSHGetFileInfo(string folderPath, int targetSize)
	{
		BitmapSource iniIcon = TryGetFolderCustomIconFromIni(folderPath, targetSize);
		if (iniIcon != null && !IsBlankOrWhiteIcon(iniIcon))
		{
			return iniIcon;
		}
		BitmapSource custom = GetIconViaShellItemImageFactory(folderPath, targetSize, iconOnly: true);
		if (custom != null && !IsBlankOrWhiteIcon(custom))
		{
			return custom;
		}
		BitmapSource hq = GetHighQualityIconFromSystem(folderPath, 0u, targetSize);
		if (hq != null)
		{
			return hq;
		}
		try
		{
			SHFILEINFO shinfo = default(SHFILEINFO);
			uint flags = 256u;
			if (SHGetFileInfo(folderPath, 0u, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags) != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
			{
				try
				{
					BitmapSource bs = HIconToFullResBitmapSource(shinfo.hIcon) ?? Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
					DestroyIcon(shinfo.hIcon);
					if (bs.PixelWidth != targetSize || bs.PixelHeight != targetSize)
					{
						bs = ResizeToSize(bs, targetSize);
					}
					bs.Freeze();
					return bs;
				}
				catch
				{
					DestroyIcon(shinfo.hIcon);
				}
			}
		}
		catch
		{
		}
		try
		{
			SHFILEINFO shinfo2 = default(SHFILEINFO);
			uint flags2 = 272u;
			if (SHGetFileInfo(folderPath, 16u, ref shinfo2, (uint)Marshal.SizeOf(shinfo2), flags2) != IntPtr.Zero && shinfo2.hIcon != IntPtr.Zero)
			{
				try
				{
					BitmapSource bs2 = HIconToFullResBitmapSource(shinfo2.hIcon) ?? Imaging.CreateBitmapSourceFromHIcon(shinfo2.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
					DestroyIcon(shinfo2.hIcon);
					if (bs2.PixelWidth != targetSize || bs2.PixelHeight != targetSize)
					{
						bs2 = ResizeToSize(bs2, targetSize);
					}
					bs2.Freeze();
					return bs2;
				}
				catch
				{
					DestroyIcon(shinfo2.hIcon);
				}
			}
		}
		catch
		{
		}
		return GetDefaultFolderIcon(targetSize);
	}

	private static BitmapSource? GetHighQualityIconFromSystem(string filePath, uint fileAttributes, int targetSize)
	{
		try
		{
			LogIcon($"GetHQIcon: path={Path.GetFileName(filePath)}, attrs=0x{fileAttributes:X}, target={targetSize}");
			SHFILEINFO shinfo = default(SHFILEINFO);
			uint flags = 16384u;
			if (fileAttributes != 0)
			{
				flags |= 0x10;
			}
			if (SHGetFileInfo(filePath, fileAttributes, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags) == IntPtr.Zero)
			{
				LogIcon("  FAIL: SHGetFileInfo returned NULL");
				return null;
			}
			LogIcon($"  IconIndex={shinfo.iIcon}");
			Guid iidImageList = typeof(IImageList).GUID;
			int hr = SHGetImageList(4, ref iidImageList, out var imageList);
			string listType = "JUMBO";
			if (hr != 0 || imageList == null)
			{
				hr = SHGetImageList(2, ref iidImageList, out imageList);
				listType = "EXTRALARGE(48)";
				if (hr != 0 || imageList == null)
				{
					LogIcon($"  FAIL: SHGetImageList hr={hr} (both JUMBO & EXTRALARGE)");
					return null;
				}
			}
			LogIcon("  ImageList=" + listType);
			hr = imageList.GetIcon(shinfo.iIcon, 1, out var hIcon);
			if (hr != 0 || hIcon == IntPtr.Zero)
			{
				LogIcon($"  FAIL: GetIcon hr={hr}");
				return null;
			}
			try
			{
				BitmapSource bs = HIconToFullResBitmapSource(hIcon);
				if (bs == null)
				{
					bs = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
					LogIcon($"  Fallback CreateBitmapSourceFromHIcon: {bs.PixelWidth}x{bs.PixelHeight}@{bs.DpiX}dpi");
				}
				if (IsBlankOrWhiteIcon(bs))
				{
					LogIcon("  " + listType + " icon is BLANK → trying IShellItemImageFactory(SCALEUP) then EXTRALARGE fallback");
					DestroyIcon(hIcon);
					BitmapSource scaled = GetIconViaShellItemImageFactory(filePath, targetSize, iconOnly: true, scaleUp: true);
					if (scaled != null && !IsBlankOrWhiteIcon(scaled))
					{
						LogIcon("  IShellItemImageFactory(SCALEUP) recovered non-blank icon " + Path.GetFileName(filePath));
						return scaled;
					}
					if (SHGetImageList(2, ref iidImageList, out var xlList) != 0 || xlList == null)
					{
						LogIcon("  EXTRALARGE image list not available, return null");
						return null;
					}
					hr = xlList.GetIcon(shinfo.iIcon, 1, out hIcon);
					if (hr != 0 || hIcon == IntPtr.Zero)
					{
						LogIcon($"  EXTRALARGE GetIcon FAILED hr={hr}");
						return null;
					}
					listType = "EXTRALARGE(48)";
					bs = HIconToFullResBitmapSource(hIcon);
					if (bs == null)
					{
						bs = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
						LogIcon($"  EXTRALARGE Fallback CreateBitmapSourceFromHIcon: {bs.PixelWidth}x{bs.PixelHeight}@{bs.DpiX}dpi");
					}
					if (IsBlankOrWhiteIcon(bs))
					{
						LogIcon("  EXTRALARGE also BLANK, return null");
						return null;
					}
				}
				bs = CropTransparentPadding(bs);
				if ((double)Math.Min(bs.PixelWidth, bs.PixelHeight) < (double)targetSize * 0.4)
				{
					LogIcon($"  ★ Source too small ({bs.PixelWidth}x{bs.PixelHeight} vs target {targetSize}) → pad to target");
					bs = PadIconToSize(bs, targetSize);
				}
				else if (bs.PixelWidth != targetSize || bs.PixelHeight != targetSize)
				{
					LogIcon($"  Resize {bs.PixelWidth}x{bs.PixelHeight} → {targetSize} DIP (phys at system DPI)");
					bs = ResizeToSize(bs, targetSize);
				}
				else
				{
					LogIcon($"  Already at targetSize={targetSize}, no resize needed");
				}
				bs.Freeze();
				return bs;
			}
			finally
			{
				DestroyIcon(hIcon);
			}
		}
		catch (Exception ex)
		{
			LogIcon("  EXCEPTION: " + ex.Message);
			return null;
		}
	}

	private static BitmapSource? HIconToFullResBitmapSource(nint hIcon)
	{
		if (!GetIconInfo(hIcon, out var iconInfo))
		{
			LogIcon("  GetIconInfo FAILED");
			return null;
		}
		try
		{
			if (iconInfo.hbmColor == IntPtr.Zero)
			{
				LogIcon("  hbmColor=null (monochrome icon?)");
				return null;
			}
			GetObject(iconInfo.hbmColor, Marshal.SizeOf<BITMAP>(), out var bmp);
			LogIcon($"  GetIconInfo OK: HBITMAP={bmp.bmWidth}x{bmp.bmHeight}, bpp={bmp.bmBitsPixel}");
			int width = bmp.bmWidth;
			int absHeight = Math.Abs(bmp.bmHeight);
			byte[] pixels = new byte[width * absHeight * 4];
			BITMAPINFO bi = new BITMAPINFO
			{
				bmiHeader = 
				{
					biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
					biWidth = width,
					biHeight = -absHeight,
					biPlanes = 1,
					biBitCount = 32,
					biCompression = 0u
				}
			};
			nint hdc = GetDC(IntPtr.Zero);
			try
			{
				if (GetDIBits(hdc, iconInfo.hbmColor, 0u, (uint)absHeight, pixels, ref bi, 0u) == 0)
				{
					LogIcon("  GetDIBits FAILED (result=0) → fallback to FromHbitmap");
					using Bitmap gdiBmp = Image.FromHbitmap(iconInfo.hbmColor);
					return ConvertGdiBitmapToWpfBitmapSource(gdiBmp);
				}
			}
			finally
			{
				ReleaseDC(IntPtr.Zero, hdc);
			}
			(double dpiX, double dpiY) systemDpi = GetSystemDpi();
			double sysDpiX = systemDpi.dpiX;
			double sysDpiY = systemDpi.dpiY;
			BitmapSource bs = BitmapSource.Create(width, absHeight, sysDpiX, sysDpiY, PixelFormats.Bgra32, null, pixels, width * 4);
			bs.Freeze();
			LogIcon($"  GetDIBits OK: {bs.PixelWidth}x{bs.PixelHeight}@{sysDpiX:F0}dpi BGRA (alpha preserved)");
			return bs;
		}
		finally
		{
			if (iconInfo.hbmColor != IntPtr.Zero)
			{
				DeleteObject(iconInfo.hbmColor);
			}
			if (iconInfo.hbmMask != IntPtr.Zero)
			{
				DeleteObject(iconInfo.hbmMask);
			}
		}
	}

	private static BitmapSource ConvertGdiBitmapToWpfBitmapSource(Bitmap gdiBmp, double? dpiXOverride = null, double? dpiYOverride = null)
	{
		Rectangle rect = new Rectangle(0, 0, gdiBmp.Width, gdiBmp.Height);
		BitmapData bmpData = gdiBmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		try
		{
			int stride = bmpData.Stride;
			int bufferSize = Math.Abs(stride) * gdiBmp.Height;
			byte[] pixels = new byte[bufferSize];
			Marshal.Copy(bmpData.Scan0, pixels, 0, bufferSize);
			double dpiX = dpiXOverride ?? ((double)gdiBmp.HorizontalResolution);
			double dpiY = dpiYOverride ?? ((double)gdiBmp.VerticalResolution);
			if (dpiX <= 0.0)
			{
				dpiX = 96.0;
			}
			if (dpiY <= 0.0)
			{
				dpiY = 96.0;
			}
			BitmapSource bitmapSource = BitmapSource.Create(gdiBmp.Width, gdiBmp.Height, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, Math.Abs(stride));
			bitmapSource.Freeze();
			return bitmapSource;
		}
		finally
		{
			gdiBmp.UnlockBits(bmpData);
		}
	}

	private static BitmapSource? GetIconViaSHGetFileInfo(string filePath, int targetSize)
	{
		try
		{
			SHFILEINFO shinfo = default(SHFILEINFO);
			uint flags = 256u;
			if (SHGetFileInfo(filePath, 0u, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags) != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
			{
				try
				{
					BitmapSource bs = HIconToFullResBitmapSource(shinfo.hIcon) ?? Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
					DestroyIcon(shinfo.hIcon);
					if (IsBlankOrWhiteIcon(bs))
					{
						return null;
					}
					if ((double)Math.Min(bs.PixelWidth, bs.PixelHeight) < (double)targetSize * 0.4)
					{
						LogIcon($"  SHGetFileInfo source too small ({bs.PixelWidth}x{bs.PixelHeight} vs {targetSize}) → pad");
						bs = PadIconToSize(bs, targetSize);
					}
					else if (bs.PixelWidth != targetSize || bs.PixelHeight != targetSize)
					{
						bs = ResizeToSize(bs, targetSize);
					}
					bs.Freeze();
					return bs;
				}
				catch
				{
					DestroyIcon(shinfo.hIcon);
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static bool IsEmbeddedIconBinary(string path)
	{
		string ext = Path.GetExtension(path);
		if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".msp", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".scr", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".sys", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".ocx", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".cpl", StringComparison.OrdinalIgnoreCase))
		{
			return ext.Equals(".com", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static BitmapSource? TryExtractEmbeddedIcon(string filePath, int size)
	{
		try
		{
			using Icon icon = Icon.ExtractAssociatedIcon(filePath);
			if (icon == null)
			{
				return null;
			}
			BitmapSource bs = HIconToFullResBitmapSource(icon.Handle) ?? Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			bs = SmartResizeOrPad(bs, size);
			bs.Freeze();
			LogIcon($"TryExtractEmbeddedIcon: OK {Path.GetFileName(filePath)} ({bs.PixelWidth}x{bs.PixelHeight})");
			return bs;
		}
		catch (Exception ex)
		{
			LogIcon("TryExtractEmbeddedIcon: exception " + Path.GetFileName(filePath) + ": " + ex.Message);
			return null;
		}
	}

	private static BitmapSource? TryExtractHighResIcoIcon(string icoPath, int desiredSize)
	{
		try
		{
			if (string.IsNullOrEmpty(icoPath) || !File.Exists(icoPath))
			{
				return null;
			}
			using FileStream fs = new FileStream(icoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
			IconBitmapDecoder decoder = new IconBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
			if (decoder.Frames.Count == 0)
			{
				return null;
			}
			LogIcon($"TryExtractHighResIcoIcon: {Path.GetFileName(icoPath)} frames={decoder.Frames.Count} " + string.Join(",", decoder.Frames.Select((BitmapFrame f) => $"{f.PixelWidth}x{f.PixelHeight}:{f.Format}")));
			BitmapFrame frame = (from f in decoder.Frames
				orderby (f.PixelWidth != 0) ? f.PixelWidth : 256 descending, (f.PixelHeight != 0) ? f.PixelHeight : 256 descending
				select f).First();
			BitmapSource bs = ((frame.Format == PixelFormats.Bgra32 || frame.Format == PixelFormats.Pbgra32) ? ((BitmapSource)frame) : ((BitmapSource)new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0.0)));
			bs = SmartResizeOrPad(bs, desiredSize);
			bs.Freeze();
			LogIcon($"TryExtractHighResIcoIcon: OK(ICO) {Path.GetFileName(icoPath)} ({bs.PixelWidth}x{bs.PixelHeight})");
			return bs;
		}
		catch (Exception ex)
		{
			LogIcon("TryExtractHighResIcoIcon: exception " + Path.GetFileName(icoPath) + ": " + ex.Message);
			return null;
		}
	}

	private static List<IcoFrame> ParseIcoFrames(string icoPath)
	{
		List<IcoFrame> list = new List<IcoFrame>();
		try
		{
			byte[] all = File.ReadAllBytes(icoPath);
			if (all.Length < 6)
			{
				return list;
			}
			if (all[0] != 0 || all[1] != 0 || all[2] != 1)
			{
				return list;
			}
			int count = all[4] | (all[5] << 8);
			int pos = 6;
			for (int i = 0; i < count; i++)
			{
				if (pos + 16 > all.Length)
				{
					break;
				}
				int w = all[pos];
				int h = all[pos + 1];
				int bytesInRes = all[pos + 8] | (all[pos + 9] << 8) | (all[pos + 10] << 16) | (all[pos + 11] << 24);
				int imageOffset = all[pos + 12] | (all[pos + 13] << 8) | (all[pos + 14] << 16) | (all[pos + 15] << 24);
				if (bytesInRes > 0 && imageOffset >= 0 && imageOffset + bytesInRes <= all.Length)
				{
					byte[] fb = new byte[bytesInRes];
					Array.Copy(all, imageOffset, fb, 0, bytesInRes);
					bool isPng = fb.Length > 4 && fb[0] == 137 && fb[1] == 80 && fb[2] == 78 && fb[3] == 71;
					list.Add(new IcoFrame
					{
						width = w,
						height = h,
						bytes = fb,
						isPng = isPng
					});
				}
				pos += 16;
			}
		}
		catch (Exception ex)
		{
			LogIcon("ParseIcoFrames: exception " + Path.GetFileName(icoPath) + ": " + ex.Message);
		}
		return list;
	}

	private static int GetMaxIcoFrameSize(string icoPath)
	{
		try
		{
			using FileStream fs = File.OpenRead(icoPath);
			using BinaryReader br = new BinaryReader(fs);
			br.ReadUInt16();
			if (br.ReadUInt16() != 1)
			{
				return 0;
			}
			ushort count = br.ReadUInt16();
			int max = 0;
			for (int i = 0; i < count; i++)
			{
				int w = br.ReadByte();
				int h = br.ReadByte();
				br.ReadByte();
				br.ReadByte();
				br.ReadUInt16();
				br.ReadUInt16();
				br.ReadUInt32();
				br.ReadUInt32();
				int dim = ((w == 0) ? 256 : w);
				if (dim > max)
				{
					max = dim;
				}
				int dimH = ((h == 0) ? 256 : h);
				if (dimH > max)
				{
					max = dimH;
				}
			}
			return max;
		}
		catch (Exception ex)
		{
			LogIcon("GetMaxIcoFrameSize: exception " + Path.GetFileName(icoPath) + ": " + ex.Message);
			return 0;
		}
	}

	private static BitmapSource? GetGenericFileTypeIcon(string filePath, int targetSize)
	{
		try
		{
			SHFILEINFO shinfo = default(SHFILEINFO);
			uint flags = 272u;
			if (SHGetFileInfo(filePath, 128u, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags) != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
			{
				try
				{
					BitmapSource bs = HIconToFullResBitmapSource(shinfo.hIcon) ?? Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
					DestroyIcon(shinfo.hIcon);
					bs = CropTransparentPadding(bs);
					LogIcon($"  GetGenericFileTypeIcon: OK {bs.PixelWidth}x{bs.PixelHeight}");
					if ((double)Math.Min(bs.PixelWidth, bs.PixelHeight) < (double)targetSize * 0.4)
					{
						bs = PadIconToSize(bs, targetSize);
					}
					else if (bs.PixelWidth != targetSize || bs.PixelHeight != targetSize)
					{
						bs = ResizeToSize(bs, targetSize);
					}
					bs.Freeze();
					return bs;
				}
				catch
				{
					DestroyIcon(shinfo.hIcon);
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static bool IsBlankOrWhiteIcon(BitmapSource source)
	{
		try
		{
			if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
			{
				return true;
			}
			int w = source.PixelWidth;
			int h = source.PixelHeight;
			int sampleSize = 8;
			if (w < 8 || h < 8)
			{
				sampleSize = Math.Min(w, h);
			}
			(int, int)[] obj = new(int, int)[5]
			{
				((w - sampleSize) / 2, (h - sampleSize) / 2),
				(sampleSize, sampleSize),
				(w - sampleSize * 2, sampleSize),
				(sampleSize, h - sampleSize * 2),
				(w - sampleSize * 2, h - sampleSize * 2)
			};
			System.Windows.Media.PixelFormat fmt = source.Format;
			int bOff;
			int gOff;
			int rOff;
			int aOff;
			if (fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Pbgra32)
			{
				bOff = 0;
				gOff = 1;
				rOff = 2;
				aOff = 3;
			}
			else if (fmt == PixelFormats.Bgr32)
			{
				bOff = 0;
				gOff = 1;
				rOff = 2;
				aOff = -1;
			}
			else
			{
				bOff = 0;
				gOff = 1;
				rOff = 2;
				aOff = 3;
			}
			int totalSamples = obj.Length;
			int blankRegions = 0;
			int whiteRegions = 0;
			(int, int)[] array = obj;
			for (int i = 0; i < array.Length; i++)
			{
				(int, int) tuple = array[i];
				int rx = tuple.Item1;
				int ry = tuple.Item2;
				int cx = Math.Max(0, Math.Min(rx, w - sampleSize));
				int cy = Math.Max(0, Math.Min(ry, h - sampleSize));
				int regionSize = sampleSize;
				if (cx + regionSize > w)
				{
					regionSize = w - cx;
				}
				if (cy + regionSize > h)
				{
					regionSize = h - cy;
				}
				CroppedBitmap croppedBitmap = new CroppedBitmap(source, new Int32Rect(cx, cy, regionSize, regionSize));
				int pixelCount = regionSize * regionSize;
				byte[] pixels = new byte[pixelCount * 4];
				croppedBitmap.CopyPixels(pixels, regionSize * 4, 0);
				int visibleCount = 0;
				int whiteCount = 0;
				for (int j = 0; j < pixelCount; j++)
				{
					byte r = pixels[j * 4 + rOff];
					byte g = pixels[j * 4 + gOff];
					byte b = pixels[j * 4 + bOff];
					if (((aOff >= 0) ? pixels[j * 4 + aOff] : byte.MaxValue) >= 30)
					{
						visibleCount++;
						if (r >= 240 && g >= 240 && b >= 240)
						{
							whiteCount++;
						}
					}
				}
				if (visibleCount < pixelCount * 10 / 100)
				{
					blankRegions++;
				}
				else if (visibleCount > 0 && whiteCount > visibleCount * 90 / 100)
				{
					whiteRegions++;
				}
				if (visibleCount >= pixelCount * 10 / 100 && (whiteCount <= visibleCount * 90 / 100 || visibleCount == 0))
				{
					LogIcon($"  IsBlankOrWhiteIcon: OK (region has {visibleCount}/{pixelCount} visible, {whiteCount} white)");
					return false;
				}
			}
			if (blankRegions == totalSamples)
			{
				LogIcon($"  IsBlankOrWhiteIcon: BLANK (all {totalSamples} regions blank)");
				return true;
			}
			if (whiteRegions + blankRegions >= totalSamples)
			{
				LogIcon($"  IsBlankOrWhiteIcon: WHITE (blank={blankRegions}, white={whiteRegions}, total={totalSamples})");
				return true;
			}
			LogIcon($"  IsBlankOrWhiteIcon: MIXED (blank={blankRegions}, white={whiteRegions}, total={totalSamples}) → NOT blank");
		}
		catch (Exception ex)
		{
			LogIcon("  IsBlankOrWhiteIcon: ERROR " + ex.Message);
		}
		return false;
	}

	private static BitmapSource CropTransparentPadding(BitmapSource source)
	{
		return CropTransparentPadding(source, 0.5);
	}

	private static BitmapSource CropTransparentPadding(BitmapSource source, double skipCoverage)
	{
		try
		{
			int w = source.PixelWidth;
			int h = source.PixelHeight;
			if (w < 16 || h < 16)
			{
				return source;
			}
			int stride = w * 4;
			byte[] pixels = new byte[h * stride];
			source.CopyPixels(pixels, stride, 0);
			System.Windows.Media.PixelFormat fmt = source.Format;
			int aOff = ((fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Pbgra32) ? 3 : ((!(fmt == PixelFormats.Bgr32)) ? 3 : (-1)));
			int step = Math.Max(1, Math.Max(w, h) / 64);
			int loMinX = w;
			int loMinY = h;
			int loMaxX = 0;
			int loMaxY = 0;
			int hiMinX = w;
			int hiMinY = h;
			int hiMaxX = 0;
			int hiMaxY = 0;
			bool loFound = false;
			bool hiFound = false;
			for (int y = 0; y < h; y += step)
			{
				for (int x = 0; x < w; x += step)
				{
					int idx = y * stride + x * 4;
					byte num = ((aOff >= 0) ? pixels[idx + aOff] : byte.MaxValue);
					if (num >= 30)
					{
						if (x < loMinX)
						{
							loMinX = x;
						}
						if (y < loMinY)
						{
							loMinY = y;
						}
						if (x > loMaxX)
						{
							loMaxX = x;
						}
						if (y > loMaxY)
						{
							loMaxY = y;
						}
						loFound = true;
					}
					if (num >= 128)
					{
						if (x < hiMinX)
						{
							hiMinX = x;
						}
						if (y < hiMinY)
						{
							hiMinY = y;
						}
						if (x > hiMaxX)
						{
							hiMaxX = x;
						}
						if (y > hiMaxY)
						{
							hiMaxY = y;
						}
						hiFound = true;
					}
				}
			}
			if (!loFound)
			{
				return source;
			}
			int minX;
			int minY;
			int maxX;
			int maxY;
			if (hiFound)
			{
				minX = hiMinX;
				minY = hiMinY;
				maxX = hiMaxX;
				maxY = hiMaxY;
			}
			else
			{
				minX = loMinX;
				minY = loMinY;
				maxX = loMaxX;
				maxY = loMaxY;
			}
			minX = Math.Max(0, minX - step);
			minY = Math.Max(0, minY - step);
			maxX = Math.Min(w - 1, maxX + step);
			maxY = Math.Min(h - 1, maxY + step);
			int cropW = maxX - minX + 1;
			int cropH = maxY - minY + 1;
			double coverageX = (double)cropW / (double)w;
			double coverageY = (double)cropH / (double)h;
			if (coverageX >= skipCoverage && coverageY >= skipCoverage)
			{
				LogIcon($"  CropTransparentPadding: skip (content {cropW}x{cropH} fills {(int)(coverageX * 100.0)}%/{(int)(coverageY * 100.0)}% of {w}x{h}, threshold {(int)(skipCoverage * 100.0)}%)");
				return source;
			}
			LogIcon($"  CropTransparentPadding: {w}x{h} → {cropW}x{cropH} ({(int)(coverageX * 100.0)}%/{(int)(coverageY * 100.0)}% coverage)");
			CroppedBitmap croppedBitmap = new CroppedBitmap(source, new Int32Rect(minX, minY, cropW, cropH));
			(double dpiX, double dpiY) systemDpi = GetSystemDpi();
			double dpiX = systemDpi.dpiX;
			double dpiY = systemDpi.dpiY;
			int cropStride = cropW * 4;
			byte[] cropPixels = new byte[cropH * cropStride];
			croppedBitmap.CopyPixels(cropPixels, cropStride, 0);
			return BitmapSource.Create(cropW, cropH, dpiX, dpiY, source.Format, null, cropPixels, cropStride);
		}
		catch (Exception ex)
		{
			LogIcon("  CropTransparentPadding: ERROR " + ex.Message);
			return source;
		}
	}

	private static BitmapSource GetDefaultFolderIcon(int targetSize)
	{
		(double dpiX, double dpiY) systemDpi = GetSystemDpi();
		double dpiX = systemDpi.dpiX;
		double dpiY = systemDpi.dpiY;
		DrawingVisual vis = new DrawingVisual();
		using (DrawingContext dc = vis.RenderOpen())
		{
			double s = (double)targetSize * 0.7;
			double x = ((double)targetSize - s) / 2.0;
			double y = ((double)targetSize - s) / 2.0 + 1.0;
			dc.DrawGeometry(new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 220, 180, 80)), new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 180, 140, 50)), 1.0), Geometry.Parse($"M {x + 1.0},{y + s * 0.55} L {x + 1.0},{y + s} L {x + s},{y + s} L {x + s},{y + s * 0.5} Z"));
			dc.DrawGeometry(new SolidColorBrush(System.Windows.Media.Color.FromArgb(230, byte.MaxValue, 210, 80)), new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 220, 170, 50)), 1.2), Geometry.Parse($"M {x},{y + s * 0.45} L {x},{y + s} L {x + s},{y + s} L {x + s},{y + s * 0.4} L {x + s * 0.55},{y + s * 0.4} L {x + s * 0.5},{y + s * 0.15} L {x + s * 0.12},{y + s * 0.15} Z"));
			dc.DrawGeometry(new SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 240, 190, 60)), new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 200, 160, 40)), 1.0), Geometry.Parse($"M {x},{y + s * 0.45} L {x + s * 0.12},{y + s * 0.15} L {x + s * 0.5},{y + s * 0.15} L {x + s * 0.55},{y + s * 0.4} Z"));
		}
		RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap((int)((double)targetSize * dpiX / 96.0), (int)((double)targetSize * dpiY / 96.0), dpiX, dpiY, PixelFormats.Pbgra32);
		renderTargetBitmap.Render(vis);
		renderTargetBitmap.Freeze();
		return renderTargetBitmap;
	}

	private static BitmapSource? GetImageThumbnail(string imagePath, int targetSize)
	{
		try
		{
			double dpiX = GetSystemDpi().dpiX;
			double dpiScale = dpiX / 96.0;
			int physTarget = (int)Math.Round((double)targetSize * dpiScale);
			if (physTarget <= 0)
			{
				physTarget = targetSize;
			}
			if (physTarget > 256)
			{
				physTarget = 256;
			}
			BitmapImage wpfBitmap = new BitmapImage();
			using FileStream fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			wpfBitmap.BeginInit();
			wpfBitmap.CacheOption = BitmapCacheOption.OnLoad;
			wpfBitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
			wpfBitmap.StreamSource = fs;
			wpfBitmap.DecodePixelWidth = physTarget;
			wpfBitmap.DecodePixelHeight = physTarget;
			wpfBitmap.EndInit();
			wpfBitmap.Freeze();
			LogIcon($"  GetImageThumbnail: decoded {wpfBitmap.PixelWidth}x{wpfBitmap.PixelHeight} @ {wpfBitmap.DpiX:F0}dpi, physTarget={physTarget}, DIP={targetSize}");
			if (wpfBitmap.PixelWidth != wpfBitmap.PixelHeight)
			{
				int pixelWidth = wpfBitmap.PixelWidth;
				int srcH = wpfBitmap.PixelHeight;
				int cropSize = Math.Min(pixelWidth, srcH);
				int cropX = (pixelWidth - cropSize) / 2;
				int cropY = (srcH - cropSize) / 2;
				CroppedBitmap croppedBitmap = new CroppedBitmap(wpfBitmap, new Int32Rect(cropX, cropY, cropSize, cropSize));
				croppedBitmap.Freeze();
				return ResizeToSize(croppedBitmap, targetSize);
			}
			return ResizeToSize(wpfBitmap, targetSize);
		}
		catch (Exception ex)
		{
			LogIcon("  GetImageThumbnail: exception: " + ex.Message);
			return GetDefaultIcon(targetSize);
		}
	}

	private static (double dpiX, double dpiY) GetSystemDpi()
	{
		try
		{
			if (Application.Current?.MainWindow != null)
			{
				PresentationSource src = PresentationSource.FromVisual(Application.Current.MainWindow);
				if (src?.CompositionTarget != null)
				{
					System.Windows.Media.Matrix m = src.CompositionTarget.TransformToDevice;
					double dx = 96.0 * m.M11;
					double dy = 96.0 * m.M22;
					if (Math.Abs(dx - 96.0) > 0.5 || Math.Abs(dy - 96.0) > 0.5)
					{
						return (dpiX: dx, dpiY: dy);
					}
				}
			}
			if (Application.Current != null)
			{
				foreach (Window window in Application.Current.Windows)
				{
					if (window == null)
					{
						continue;
					}
					PresentationSource src2 = PresentationSource.FromVisual(window);
					if (src2?.CompositionTarget != null)
					{
						System.Windows.Media.Matrix m2 = src2.CompositionTarget.TransformToDevice;
						double dx2 = 96.0 * m2.M11;
						double dy2 = 96.0 * m2.M22;
						if (Math.Abs(dx2 - 96.0) > 0.5 || Math.Abs(dy2 - 96.0) > 0.5)
						{
							return (dpiX: dx2, dpiY: dy2);
						}
					}
				}
			}
		}
		catch
		{
		}
		try
		{
			using Graphics g = Graphics.FromHwnd(IntPtr.Zero);
			double dx3 = g.DpiX;
			double dy3 = g.DpiY;
			if (dx3 > 0.0 && dy3 > 0.0)
			{
				return (dpiX: dx3, dpiY: dy3);
			}
		}
		catch
		{
		}
		return (dpiX: 96.0, dpiY: 96.0);
	}

	private static BitmapSource ResizeToSize(BitmapSource source, int targetDipSize)
	{
		(double dpiX, double dpiY) systemDpi = GetSystemDpi();
		double dpiX = systemDpi.dpiX;
		double dpiY = systemDpi.dpiY;
		double dpiScale = dpiX / 96.0;
		int physTarget = (int)Math.Round((double)targetDipSize * dpiScale);
		if (physTarget <= 0)
		{
			physTarget = targetDipSize;
		}
		double scale = Math.Min((double)physTarget / (double)source.PixelWidth, (double)physTarget / (double)source.PixelHeight);
		int w = (int)((double)source.PixelWidth * scale);
		int h = (int)((double)source.PixelHeight * scale);
		int offsetX = (physTarget - w) / 2;
		int offsetY = (physTarget - h) / 2;
		if (w == source.PixelWidth && h == source.PixelHeight && offsetX == 0 && offsetY == 0 && Math.Abs(source.DpiX - dpiX) < 0.5 && Math.Abs(source.DpiY - dpiY) < 0.5)
		{
			return source;
		}
		int pixelWidth = source.PixelWidth;
		int pixelHeight = source.PixelHeight;
		int srcStride = pixelWidth * 4;
		byte[] srcPixels = new byte[pixelHeight * srcStride];
		source.CopyPixels(srcPixels, srcStride, 0);
		using Bitmap srcBmp = new Bitmap(pixelWidth, pixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		BitmapData srcData = srcBmp.LockBits(new Rectangle(0, 0, pixelWidth, pixelHeight), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		Marshal.Copy(srcPixels, 0, srcData.Scan0, srcPixels.Length);
		srcBmp.UnlockBits(srcData);
		srcBmp.SetResolution((float)dpiX, (float)dpiY);
		using Bitmap targetBmp = new Bitmap(physTarget, physTarget, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		targetBmp.SetResolution((float)dpiX, (float)dpiY);
		using Graphics g = Graphics.FromImage(targetBmp);
		g.InterpolationMode = InterpolationMode.HighQualityBicubic;
		g.SmoothingMode = SmoothingMode.HighQuality;
		g.PixelOffsetMode = PixelOffsetMode.HighQuality;
		g.CompositingQuality = CompositingQuality.HighQuality;
		g.Clear(System.Drawing.Color.Transparent);
		g.DrawImage(srcBmp, new Rectangle(offsetX, offsetY, w, h));
		BitmapSource bitmapSource = ConvertGdiBitmapToWpfBitmapSource(targetBmp, dpiX, dpiY);
		bitmapSource.Freeze();
		LogIcon($"  ResizeToSize(GDI+Bicubic+SystemDPI): {pixelWidth}x{pixelHeight}@{source.DpiX:F0}dpi → {physTarget}x{physTarget}@{dpiX:F0}dpi, DIP={targetDipSize}, scale={scale:F3}");
		return bitmapSource;
	}

	private static BitmapSource SmartResizeOrPad(BitmapSource source, int targetDipSize)
	{
		if ((double)Math.Min(source.PixelWidth, source.PixelHeight) < (double)targetDipSize * 0.4)
		{
			LogIcon($"  SmartResizeOrPad: PAD {source.PixelWidth}x{source.PixelHeight} → {targetDipSize} DIP (<40% threshold)");
			return PadIconToSize(source, targetDipSize);
		}
		if (source.PixelWidth != targetDipSize || source.PixelHeight != targetDipSize)
		{
			return ResizeToSize(source, targetDipSize);
		}
		return source;
	}

	private static BitmapSource PadIconToSize(BitmapSource source, int targetDipSize)
	{
		try
		{
			BitmapSource result = ResizeToSize(CropTransparentPadding(source), targetDipSize);
			LogIcon($"  PadIconToSize(stretch-to-fill): {source.PixelWidth}x{source.PixelHeight} → {result.PixelWidth}x{result.PixelHeight}, DIP={targetDipSize}");
			return result;
		}
		catch (Exception ex)
		{
			LogIcon("  PadIconToSize: EXCEPTION " + ex.Message + ", fallback to original");
			return source;
		}
	}

	private static BitmapSource NormalizeAppIcon(BitmapSource bs, int targetSize)
	{
		if (bs == null)
		{
			return bs;
		}
		try
		{
			BitmapSource cropped = CropTransparentPadding(bs, 0.9);
			if (cropped.PixelWidth != targetSize || cropped.PixelHeight != targetSize)
			{
				cropped = ResizeToSize(cropped, targetSize);
			}
			return cropped;
		}
		catch
		{
			return bs;
		}
	}

	private static BitmapSource GetDefaultIcon(int targetSize)
	{
		(double dpiX, double dpiY) systemDpi = GetSystemDpi();
		double dpiX = systemDpi.dpiX;
		double dpiY = systemDpi.dpiY;
		double half = (double)targetSize / 2.0;
		double radius = half - 2.0;
		DrawingVisual vis = new DrawingVisual();
		using (DrawingContext dc = vis.RenderOpen())
		{
			double dpi = VisualTreeHelper.GetDpi(vis).PixelsPerDip;
			dc.DrawEllipse(new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, byte.MaxValue, byte.MaxValue, byte.MaxValue)), null, new System.Windows.Point(half, half), radius, radius);
			System.Windows.Media.Pen pen = new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, byte.MaxValue, byte.MaxValue, byte.MaxValue)), 1.0)
			{
				DashStyle = DashStyles.Dash
			};
			dc.DrawEllipse(null, pen, new System.Windows.Point(half, half), radius, radius);
			Typeface tf = new Typeface("Microsoft YaHei");
			FormattedText ft = new FormattedText("?", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, (double)targetSize * 0.45, new SolidColorBrush(System.Windows.Media.Color.FromArgb(100, byte.MaxValue, byte.MaxValue, byte.MaxValue)), dpi);
			dc.DrawText(ft, new System.Windows.Point(half - ft.Width / 2.0, half - ft.Height / 2.0));
		}
		RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap((int)((double)targetSize * dpiX / 96.0), (int)((double)targetSize * dpiY / 96.0), dpiX, dpiY, PixelFormats.Pbgra32);
		renderTargetBitmap.Render(vis);
		renderTargetBitmap.Freeze();
		return renderTargetBitmap;
	}

	private static string? ResolveShortcutTarget(string lnkPath)
	{
		try
		{
			IShellLinkW obj = (IShellLinkW)new ShellLink();
			((IPersistFile)obj).Load(lnkPath, 0u);
			StringBuilder sb = new StringBuilder(260);
			obj.GetPath(sb, sb.Capacity, IntPtr.Zero, 0u);
			return sb.ToString();
		}
		catch
		{
			return null;
		}
	}

	public static bool FixShortcutIconReference(string lnkPath)
	{
		if (string.IsNullOrEmpty(lnkPath) || !lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(lnkPath))
		{
			return false;
		}
		try
		{
			IShellLinkWFull lnk = (IShellLinkWFull)new ShellLinkFull();
			((IPersistFileFull)lnk).Load(lnkPath, 0u);
			StringBuilder iconPathSb = new StringBuilder(260);
			lnk.GetIconLocation(iconPathSb, iconPathSb.Capacity, out var iconIndex);
			string iconPath = iconPathSb.ToString();
			if (!string.IsNullOrWhiteSpace(iconPath) && Path.IsPathRooted(iconPath) && File.Exists(iconPath))
			{
				return false;
			}
			string target = ResolveShortcutTarget(lnkPath);
			if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
			{
				return false;
			}
			lnk.SetIconLocation(target, iconIndex);
			((IPersistFileFull)lnk).Save(lnkPath, fRemember: true);
			LogIcon($"FixShortcutIconReference: ✅ reset icon ref of {Path.GetFileName(lnkPath)} -> {target} (idx={iconIndex})");
			return true;
		}
		catch (Exception ex)
		{
			LogIcon("FixShortcutIconReference: ❌ failed for " + Path.GetFileName(lnkPath) + ": " + ex.Message);
			return false;
		}
	}

	public static void InitIconCacheMaintenance()
	{
		try
		{
			Task.Run(delegate
			{
				TriggerIconCacheCleanup();
			});
			_cacheCleanupTimer = new Timer(delegate
			{
				TriggerIconCacheCleanup();
			}, null, 1800000, 1800000);
		}
		catch (Exception ex)
		{
			LogIcon("InitIconCacheMaintenance failed: " + ex.Message);
		}
	}

	public static void TriggerIconCacheCleanup()
	{
		try
		{
			if (!Monitor.TryEnter(_cacheCleanupLock))
			{
				return;
			}
			try
			{
				EnforceIconCacheCapAndAge();
			}
			finally
			{
				Monitor.Exit(_cacheCleanupLock);
			}
		}
		catch (Exception ex)
		{
			LogIcon("TriggerIconCacheCleanup failed: " + ex.Message);
		}
	}

	private static void EnforceIconCacheCapAndAge()
	{
		try
		{
			string[] obj = new string[2] { IconCacheDir, LastGoodDir };
			List<FileInfo> files = new List<FileInfo>();
			string[] array = obj;
			foreach (string dir in array)
			{
				if (Directory.Exists(dir))
				{
					try
					{
						files.AddRange(new DirectoryInfo(dir).EnumerateFiles("*.png"));
					}
					catch
					{
					}
				}
			}
			if (files.Count == 0)
			{
				return;
			}
			DateTime cutoff = DateTime.UtcNow.AddDays(-15.0);
			int expiredDeleted = 0;
			files.RemoveAll(delegate(FileInfo f)
			{
				if (f.LastWriteTimeUtc < cutoff)
				{
					try
					{
						f.Delete();
						expiredDeleted++;
					}
					catch
					{
					}
					return true;
				}
				return false;
			});
			if (files.Count > 1000)
			{
				files.Sort((FileInfo a, FileInfo b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
				int toDelete = files.Count - 1000;
				for (int i2 = 0; i2 < toDelete; i2++)
				{
					try
					{
						files[i2].Delete();
					}
					catch
					{
					}
				}
				LogIcon($"IconCache cleanup: kept {1000}, deleted {toDelete} oldest + {expiredDeleted} expired (total was {files.Count + toDelete})");
			}
			else if (expiredDeleted > 0)
			{
				LogIcon($"IconCache cleanup: deleted {expiredDeleted} expired files, count now {files.Count}");
			}
		}
		catch (Exception ex)
		{
			LogIcon("EnforceIconCacheCapAndAge failed: " + ex.Message);
		}
	}

	private static void MaybeTriggerPeriodicCleanup()
	{
		if (Interlocked.Increment(ref _iconCacheSaveCounter) % 2000 == 0)
		{
			Task.Run(delegate
			{
				TriggerIconCacheCleanup();
			});
		}
	}

	private static string ComputeCacheKey(string filePath, int displaySize)
	{
		try
		{
			long timestamp = (File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath).Ticks : 0);
			if (timestamp == 0L && Directory.Exists(filePath))
			{
				timestamp = new DirectoryInfo(filePath).LastWriteTimeUtc.Ticks;
			}
			if (Directory.Exists(filePath))
			{
				string ini = Path.Combine(filePath, "desktop.ini");
				timestamp = ((!File.Exists(ini)) ? (timestamp * 31 + 1) : (timestamp * 31 + File.GetLastWriteTimeUtc(ini).Ticks));
			}
			timestamp -= timestamp % 600000000;
			string input = $"iconalgo_v13|{filePath.ToLowerInvariant()}|{timestamp}|{displaySize:D4}";
			return BitConverter.ToString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).Replace("-", "");
		}
		catch
		{
			return "";
		}
	}

	private static BitmapSource? LoadIconFromCache(string cacheKey)
	{
		try
		{
			string cachePath = Path.Combine(IconCacheDir, cacheKey + ".png");
			if (!File.Exists(cachePath))
			{
				return null;
			}
			BitmapImage bitmapImage = new BitmapImage();
			bitmapImage.BeginInit();
			bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
			bitmapImage.UriSource = new Uri(cachePath);
			bitmapImage.EndInit();
			bitmapImage.Freeze();
			return bitmapImage;
		}
		catch
		{
			return null;
		}
	}

	private static void SaveIconToCache(string cacheKey, BitmapSource source)
	{
		try
		{
			Directory.CreateDirectory(IconCacheDir);
			using FileStream stream = new FileStream(Path.Combine(IconCacheDir, cacheKey + ".png"), FileMode.Create, FileAccess.Write);
			PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
			pngBitmapEncoder.Frames.Add(BitmapFrame.Create(source));
			pngBitmapEncoder.Save(stream);
			MaybeTriggerPeriodicCleanup();
		}
		catch
		{
		}
	}

	private static string LastGoodKey(string filePath, int size = 0)
	{
		try
		{
			string input = ((size > 0) ? $"lastgood|{filePath.ToLowerInvariant()}|{size}" : ("lastgood|" + filePath.ToLowerInvariant()));
			return BitConverter.ToString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).Replace("-", "");
		}
		catch
		{
			return "";
		}
	}

	private static void SaveLastGoodIcon(string filePath, BitmapSource source, int size)
	{
		if (source == null || IsBlankOrWhiteIcon(source))
		{
			return;
		}
		try
		{
			string key = LastGoodKey(filePath, size);
			if (string.IsNullOrEmpty(key))
			{
				return;
			}
			Directory.CreateDirectory(LastGoodDir);
			using FileStream stream = new FileStream(Path.Combine(LastGoodDir, key + ".png"), FileMode.Create, FileAccess.Write);
			PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
			pngBitmapEncoder.Frames.Add(BitmapFrame.Create(source));
			pngBitmapEncoder.Save(stream);
		}
		catch
		{
		}
	}

	private static BitmapSource? LoadLastGoodIcon(string filePath, int size)
	{
		BitmapSource sized = LoadLastGoodFile(LastGoodKey(filePath, size));
		if (sized != null)
		{
			return sized;
		}
		BitmapSource legacy = LoadLastGoodFile(LastGoodKey(filePath));
		if (legacy != null && size > 0 && legacy.PixelWidth >= size)
		{
			return legacy;
		}
		return null;
	}

	private static BitmapSource? LoadLastGoodFile(string key)
	{
		try
		{
			if (string.IsNullOrEmpty(key))
			{
				return null;
			}
			string p = Path.Combine(LastGoodDir, key + ".png");
			if (!File.Exists(p))
			{
				return null;
			}
			BitmapImage bmp = new BitmapImage();
			bmp.BeginInit();
			bmp.CacheOption = BitmapCacheOption.OnLoad;
			bmp.UriSource = new Uri(p);
			bmp.EndInit();
			bmp.Freeze();
			if (IsBlankOrWhiteIcon(bmp))
			{
				return null;
			}
			return bmp;
		}
		catch
		{
			return null;
		}
	}

	private static BitmapSource? RecoverNonWhiteIcon(string filePath, string? cacheKey, int size)
	{
		if (!string.IsNullOrEmpty(cacheKey))
		{
			BitmapSource c = LoadIconFromCache(cacheKey);
			if (c != null && !IsBlankOrWhiteIcon(c))
			{
				return c;
			}
		}
		BitmapSource lg = LoadLastGoodIcon(filePath, size);
		if (lg != null)
		{
			return lg;
		}
		return null;
	}

	private static bool IsGenericTypeExtensionIcon(string filePath)
	{
		try
		{
			if (Directory.Exists(filePath))
			{
				return false;
			}
		}
		catch
		{
			return false;
		}
		string ext = Path.GetExtension(filePath);
		if (string.IsNullOrEmpty(ext))
		{
			return false;
		}
		if (ImageExtensions.Contains(ext))
		{
			return false;
		}
		if (PerFileThumbnailExtensions.Contains(ext))
		{
			return false;
		}
		if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (ext.Equals(".website", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (IsEmbeddedIconBinary(filePath))
		{
			return false;
		}
		return true;
	}

	private static BitmapSource? TryGetFromMemoryCache(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return null;
		}
		lock (_memCacheLock)
		{
			if (_memCache.TryGetValue(key, out var node))
			{
				_memLru.Remove(node);
				_memLru.AddFirst(node);
				return node.Value.Image;
			}
		}
		return null;
	}

	private static void AddToMemoryCache(string key, BitmapSource? image)
	{
		if (string.IsNullOrEmpty(key) || image == null)
		{
			return;
		}
		lock (_memCacheLock)
		{
			if (_memCache.TryGetValue(key, out var existing))
			{
				existing.Value.Image = image;
				_memLru.Remove(existing);
				_memLru.AddFirst(existing);
				return;
			}
			LinkedListNode<MemCacheEntry> node = new LinkedListNode<MemCacheEntry>(new MemCacheEntry
			{
				Key = key,
				Image = image
			});
			_memLru.AddFirst(node);
			_memCache[key] = node;
			while (_memCache.Count > 512)
			{
				LinkedListNode<MemCacheEntry> tail = _memLru.Last;
				if (tail == null)
				{
					break;
				}
				_memLru.RemoveLast();
				_memCache.Remove(tail.Value.Key);
			}
		}
	}

	private static void EvictMemoryCache(string filePath)
	{
		string ext = Path.GetExtension(filePath);
		lock (_memCacheLock)
		{
			for (int s = 16; s <= 1024; s++)
			{
				RemoveKeyNoLock(ComputeCacheKey(filePath, s));
				if (!string.IsNullOrEmpty(ext))
				{
					RemoveKeyNoLock($"EXT|{ext.ToLowerInvariant()}|{s:D4}");
				}
			}
		}
		static void RemoveKeyNoLock(string k)
		{
			if (!string.IsNullOrEmpty(k) && _memCache.TryGetValue(k, out var node))
			{
				_memLru.Remove(node);
				_memCache.Remove(k);
			}
		}
	}

	public static void InvalidateIconCache(string filePath)
	{
		EvictMemoryCache(filePath);
		try
		{
			if (!Directory.Exists(IconCacheDir))
			{
				return;
			}
			for (int s = 16; s <= 1024; s++)
			{
				string key = ComputeCacheKey(filePath, s);
				string p = Path.Combine(IconCacheDir, key + ".png");
				if (File.Exists(p))
				{
					File.Delete(p);
				}
			}
			LogIcon("InvalidateIconCache: " + Path.GetFileName(filePath));
		}
		catch
		{
		}
	}

	private static bool IsInStartupGracePeriod()
	{
		return (DateTime.UtcNow - _appStartTime).TotalSeconds < 60.0;
	}

	public static void ClearSystemIconCache()
	{
		lock (_cacheClearLock)
		{
			if ((DateTime.UtcNow - _lastCacheClear).TotalSeconds < 3.0)
			{
				LogIcon($"ClearSystemIconCache: SKIPPED (debounced, last was {(DateTime.UtcNow - _lastCacheClear).TotalSeconds:F1}s ago)");
				return;
			}
			_lastCacheClear = DateTime.UtcNow;
		}
		bool inGracePeriod = IsInStartupGracePeriod();
		LogIcon($"ClearSystemIconCache: START (gracePeriod={inGracePeriod}, elapsed={(DateTime.UtcNow - _appStartTime).TotalSeconds:F0}s)");
		if (inGracePeriod)
		{
			LogIcon($"ClearSystemIconCache: ALL SKIPPED (startup grace period, {60.0 - (DateTime.UtcNow - _appStartTime).TotalSeconds:F0}s remaining)");
			return;
		}
		string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string explorerCacheDir = Path.Combine(localAppData, "Microsoft", "Windows", "Explorer");
		if (Directory.Exists(explorerCacheDir))
		{
			try
			{
				string[] files = Directory.GetFiles(explorerCacheDir, "iconcache_*.db");
				int deleted = 0;
				string[] array = files;
				foreach (string db in array)
				{
					try
					{
						File.SetAttributes(db, FileAttributes.Normal);
						File.Delete(db);
						deleted++;
					}
					catch
					{
					}
				}
				LogIcon($"ClearSystemIconCache: deleted {deleted} iconcache db files");
				string legacyDb = Path.Combine(localAppData, "IconCache.db");
				if (File.Exists(legacyDb))
				{
					try
					{
						File.SetAttributes(legacyDb, FileAttributes.Normal);
						File.Delete(legacyDb);
					}
					catch
					{
					}
				}
			}
			catch (Exception ex)
			{
				LogIcon("ClearSystemIconCache: db cleanup error: " + ex.Message);
			}
		}
		try
		{
			string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
			string ie4uinit = Path.Combine(sysDir, "ie4uinit.exe");
			if (File.Exists(ie4uinit))
			{
				using (Process p = Process.Start(new ProcessStartInfo
				{
					FileName = ie4uinit,
					Arguments = "-ClearIconCache",
					UseShellExecute = false,
					CreateNoWindow = true,
					WindowStyle = ProcessWindowStyle.Hidden
				}))
				{
					p?.WaitForExit(3000);
					LogIcon($"ClearSystemIconCache: ie4uinit -ClearIconCache exit={p?.ExitCode}");
					return;
				}
			}
			LogIcon("ClearSystemIconCache: ie4uinit.exe not found at " + sysDir);
		}
		catch (Exception ex2)
		{
			LogIcon("ClearSystemIconCache: ie4uinit failed: " + ex2.Message);
		}
	}

	public static BitmapSource? RepairWhiteIcon(string filePath, double displaySize = 0.0)
	{
		LogIcon("RepairWhiteIcon: " + Path.GetFileName(filePath));
		EvictMemoryCache(filePath);
		try
		{
			if (File.Exists(filePath))
			{
				File.SetLastWriteTime(filePath, DateTime.Now);
				if (filePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
				{
					if (IsInStartupGracePeriod())
					{
						LogIcon("  RepairWhiteIcon: SHCNE_CREATE SKIPPED (startup grace period)");
					}
					else
					{
						nint ptr = Marshal.StringToHGlobalUni(filePath);
						NativeMethods.SHChangeNotify(2u, 5u, ptr, IntPtr.Zero);
						Marshal.FreeHGlobal(ptr);
					}
				}
			}
		}
		catch (Exception)
		{
		}
		ClearSystemIconCache();
		Thread.Sleep(100);
		BitmapSource icon = GetFileIcon(filePath, large: true, displaySize);
		if (icon != null)
		{
			int lgSize = ((displaySize > 0.0) ? ((int)Math.Ceiling(Math.Max(displaySize, 16.0))) : 64);
			SaveLastGoodIcon(filePath, icon, lgSize);
			LogIcon($"  RepairWhiteIcon: ✅ repaired ({icon.PixelWidth}x{icon.PixelHeight})");
		}
		else
		{
			LogIcon("  RepairWhiteIcon: ❌ still blank after repair");
		}
		return icon;
	}

	public static bool IsWhiteIcon(BitmapSource? source)
	{
		if (source != null)
		{
			return IsBlankOrWhiteIcon(source);
		}
		return true;
	}

	public static int RepairWhiteIcons(IEnumerable<string> filePaths, double displaySize = 0.0)
	{
		int fixedCount = 0;
		foreach (string p in filePaths)
		{
			if (string.IsNullOrEmpty(p))
			{
				continue;
			}
			try
			{
				BitmapSource r = RepairWhiteIcon(p, displaySize);
				if (r != null && !IsBlankOrWhiteIcon(r))
				{
					fixedCount++;
				}
			}
			catch
			{
			}
		}
		return fixedCount;
	}
}
