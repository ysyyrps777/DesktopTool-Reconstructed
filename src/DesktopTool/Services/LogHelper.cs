using System;
using System.IO;
using System.Text;

namespace DesktopTool.Services;

public static class LogHelper
{
	public const long MaxLogSize = 5242880L;

	public const int KeepTailBytes = 1048576;

	private static readonly object _gate = new object();

	public static void Append(string path, string text)
	{
		try
		{
			lock (_gate)
			{
				string dir = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
				{
					Directory.CreateDirectory(dir);
				}
				File.AppendAllText(path, text, Encoding.UTF8);
				TruncateIfNeeded(path);
			}
		}
		catch
		{
		}
	}

	private static void TruncateIfNeeded(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return;
			}
			FileInfo fi = new FileInfo(path);
			if (fi.Length <= 5242880)
			{
				return;
			}
			long keep = Math.Min(1048576L, fi.Length);
			using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
			fs.Seek(-keep, SeekOrigin.End);
			byte[] buf = new byte[keep];
			int read = fs.Read(buf, 0, (int)keep);
			fs.Seek(0L, SeekOrigin.Begin);
			fs.Write(buf, 0, read);
			fs.SetLength(read);
		}
		catch
		{
		}
	}
}
