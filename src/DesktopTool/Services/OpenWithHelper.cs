using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopTool.Services;

public static class OpenWithHelper
{
	public static List<OpenWithApp> GetOpenWithApps(string filePath)
	{
		List<OpenWithApp> apps = new List<OpenWithApp>();
		string ext = Path.GetExtension(filePath).ToLowerInvariant();
		if (string.IsNullOrEmpty(ext))
		{
			return apps;
		}
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			if (NativeMethods.SHAssocEnumHandlers(ext, NativeMethods.ASSOC_FILTER.ASSOC_FILTER_RECOMMENDED, out var enumHandlers) == 0 && enumHandlers != null)
			{
				NativeMethods.IAssocHandler handler;
				uint fetched;
				while (enumHandlers.Next(1u, out handler, out fetched) == 0 && fetched != 0 && handler != null)
				{
					try
					{
						if (handler.GetUIName(out var uiName) == 0 && !string.IsNullOrEmpty(uiName) && handler.GetName(out var name) == 0 && !string.IsNullOrEmpty(name))
						{
							OpenWithApp app = GetAppFromExeName(name);
							if (app != null && seen.Add(app.ExePath))
							{
								app.Name = uiName;
								apps.Add(app);
							}
						}
					}
					finally
					{
						Marshal.ReleaseComObject(handler);
					}
				}
				Marshal.ReleaseComObject(enumHandlers);
			}
		}
		catch
		{
		}
		if (apps.Count == 0)
		{
			return GetOpenWithAppsFallback(filePath);
		}
		string selfExe = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName ?? "");
		apps.RemoveAll((OpenWithApp a) => string.Equals(Path.GetFileName(a.ExePath), selfExe, StringComparison.OrdinalIgnoreCase));
		return apps;
	}

	private static List<OpenWithApp> GetOpenWithAppsFallback(string filePath)
	{
		List<OpenWithApp> apps = new List<OpenWithApp>();
		string ext = Path.GetExtension(filePath).ToLowerInvariant();
		if (string.IsNullOrEmpty(ext))
		{
			return apps;
		}
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		OpenWithApp defaultApp = null;
		string progId = Registry.GetValue("HKEY_CLASSES_ROOT\\" + ext, "", null) as string;
		if (!string.IsNullOrEmpty(progId))
		{
			defaultApp = GetAppFromProgId(progId);
			if (defaultApp != null)
			{
				seen.Add(defaultApp.ExePath);
			}
		}
		string openWithKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\" + ext;
		using (RegistryKey mruKey = Registry.CurrentUser.OpenSubKey(openWithKey + "\\OpenWithList"))
		{
			if (mruKey != null)
			{
				string mru = mruKey.GetValue("MRUList") as string;
				if (!string.IsNullOrEmpty(mru))
				{
					string text = mru;
					for (int i = 0; i < text.Length; i++)
					{
						string exeName = mruKey.GetValue(text[i].ToString()) as string;
						if (!string.IsNullOrEmpty(exeName))
						{
							OpenWithApp app = GetAppFromExeName(exeName);
							if (app != null && seen.Add(app.ExePath))
							{
								apps.Add(app);
							}
						}
					}
				}
			}
		}
		using (RegistryKey progIdsKey = Registry.CurrentUser.OpenSubKey(openWithKey + "\\OpenWithProgids"))
		{
			if (progIdsKey != null)
			{
				string[] valueNames = progIdsKey.GetValueNames();
				foreach (string name in valueNames)
				{
					if (!string.IsNullOrEmpty(name))
					{
						OpenWithApp app2 = GetAppFromProgId(name);
						if (app2 != null && seen.Add(app2.ExePath))
						{
							apps.Add(app2);
						}
					}
				}
			}
		}
		if (defaultApp != null)
		{
			apps.Insert(0, defaultApp);
		}
		string selfExe = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName ?? "");
		apps.RemoveAll((OpenWithApp a) => string.Equals(Path.GetFileName(a.ExePath), selfExe, StringComparison.OrdinalIgnoreCase));
		return apps;
	}

	private static OpenWithApp? GetAppFromProgId(string progId)
	{
		try
		{
			string friendlyName = Registry.GetValue("HKEY_CLASSES_ROOT\\" + progId, "", null) as string;
			string command = Registry.GetValue("HKEY_CLASSES_ROOT\\" + progId + "\\shell\\open\\command", "", null) as string;
			if (string.IsNullOrEmpty(command))
			{
				command = Registry.GetValue("HKEY_CLASSES_ROOT\\" + progId + "\\shell\\open\\command", "", null) as string;
			}
			if (string.IsNullOrEmpty(command))
			{
				return null;
			}
			string exePath = ExtractExePath(command);
			if (string.IsNullOrEmpty(exePath))
			{
				return null;
			}
			string name = GetDisplayName(friendlyName, exePath);
			return new OpenWithApp
			{
				Name = name,
				ExePath = exePath,
				Command = command
			};
		}
		catch
		{
			return null;
		}
	}

	private static OpenWithApp? GetAppFromExeName(string exeName)
	{
		try
		{
			if (string.IsNullOrEmpty(exeName))
			{
				return null;
			}
			using (RegistryKey appKey = Registry.ClassesRoot.OpenSubKey("Applications\\" + exeName))
			{
				if (appKey != null)
				{
					string friendlyName = appKey.GetValue("FriendlyAppName") as string;
					using RegistryKey cmdKey = appKey.OpenSubKey("shell\\open\\command");
					string command = cmdKey?.GetValue("") as string;
					if (!string.IsNullOrEmpty(command))
					{
						string exePath = ExtractExePath(command);
						if (!string.IsNullOrEmpty(exePath))
						{
							string name = GetDisplayName(friendlyName, exePath);
							return new OpenWithApp
							{
								Name = name,
								ExePath = exePath,
								Command = command
							};
						}
					}
				}
			}
			string resolvedPath = ResolveExePath(exeName);
			if (!string.IsNullOrEmpty(resolvedPath))
			{
				string name2 = Path.GetFileNameWithoutExtension(exeName);
				return new OpenWithApp
				{
					Name = name2,
					ExePath = resolvedPath,
					Command = "\"" + resolvedPath + "\" \"%1\""
				};
			}
		}
		catch
		{
		}
		return null;
	}

	private static string ExtractExePath(string command)
	{
		if (string.IsNullOrEmpty(command))
		{
			return "";
		}
		string cmd = command.Trim();
		if (cmd.StartsWith("rundll32", StringComparison.OrdinalIgnoreCase) || cmd.StartsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
		{
			return "";
		}
		string exe;
		if (cmd.StartsWith("\""))
		{
			int endQuote = cmd.IndexOf('"', 1);
			exe = ((endQuote <= 1) ? cmd : cmd.Substring(1, endQuote - 1));
		}
		else
		{
			int space = cmd.IndexOf(' ');
			exe = ((space > 0) ? cmd.Substring(0, space) : cmd);
		}
		exe = Environment.ExpandEnvironmentVariables(exe);
		if (File.Exists(exe))
		{
			return exe;
		}
		if (!exe.Contains('\\'))
		{
			string sysPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);
			if (File.Exists(sysPath))
			{
				return sysPath;
			}
		}
		if (!File.Exists(exe))
		{
			return "";
		}
		return exe;
	}

	private static string ResolveExePath(string exeName)
	{
		if (string.IsNullOrEmpty(exeName))
		{
			return "";
		}
		if (Path.IsPathRooted(exeName) && File.Exists(exeName))
		{
			return exeName;
		}
		string command = Registry.GetValue("HKEY_CLASSES_ROOT\\Applications\\" + exeName + "\\shell\\open\\command", "", null) as string;
		if (!string.IsNullOrEmpty(command))
		{
			string exePath = ExtractExePath(command);
			if (!string.IsNullOrEmpty(exePath))
			{
				return exePath;
			}
		}
		string sysPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exeName);
		if (File.Exists(sysPath))
		{
			return sysPath;
		}
		string found = Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), exeName, SearchOption.AllDirectories).FirstOrDefault();
		if (found != null && File.Exists(found))
		{
			return found;
		}
		string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
		if (!string.IsNullOrEmpty(progFilesX86))
		{
			found = Directory.GetFiles(progFilesX86, exeName, SearchOption.AllDirectories).FirstOrDefault();
			if (found != null && File.Exists(found))
			{
				return found;
			}
		}
		return "";
	}

	private static string GetDisplayName(string? friendlyName, string exePath)
	{
		if (!string.IsNullOrEmpty(friendlyName))
		{
			if (!friendlyName.StartsWith("@"))
			{
				return friendlyName;
			}
			string resolved = NativeMethods.LoadIndirectString(friendlyName);
			if (!string.IsNullOrEmpty(resolved))
			{
				return resolved;
			}
		}
		return Path.GetFileNameWithoutExtension(exePath);
	}
}
