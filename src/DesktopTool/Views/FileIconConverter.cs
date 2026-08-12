using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopTool.Services;

namespace DesktopTool.Views;

public class FileIconConverter : IValueConverter
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
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

	private const uint SHGFI_ICON = 256u;

	private const uint SHGFI_SMALLICON = 1u;

	private const uint SHGFI_USEFILEATTRIBUTES = 16u;

	private const uint FILE_ATTRIBUTE_DIRECTORY = 16u;

	private const uint FILE_ATTRIBUTE_NORMAL = 128u;

	private static readonly Dictionary<string, ImageSource> _cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

	private static readonly object _cacheLock = new object();

	[DllImport("shell32.dll", CharSet = CharSet.Auto)]
	private static extern nint SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

	[DllImport("user32.dll")]
	private static extern bool DestroyIcon(nint hIcon);

	public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is EverythingSearchService.SearchResult r)
		{
			string key;
			string pszPath;
			uint attrs;
			if (r.IsFolder)
			{
				key = "FOLDER";
				pszPath = "folder";
				attrs = 16u;
			}
			else
			{
				string ext = Path.GetExtension(r.FullPath);
				if (string.IsNullOrEmpty(ext))
				{
					ext = ".file";
				}
				key = ext;
				pszPath = "*" + ext;
				attrs = 128u;
			}
			lock (_cacheLock)
			{
				if (_cache.TryGetValue(key, out var cached))
				{
					return cached;
				}
			}
			ImageSource src = null;
			try
			{
				SHFILEINFO info = default(SHFILEINFO);
				if (SHGetFileInfo(pszPath, attrs, ref info, (uint)Marshal.SizeOf(info), 273u) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
				{
					BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
					bitmapSource.Freeze();
					src = bitmapSource;
					DestroyIcon(info.hIcon);
				}
			}
			catch
			{
				src = null;
			}
			lock (_cacheLock)
			{
				if (src != null && !_cache.ContainsKey(key))
				{
					_cache[key] = src;
				}
			}
			return src;
		}
		return null;
	}

	public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return null;
	}
}
