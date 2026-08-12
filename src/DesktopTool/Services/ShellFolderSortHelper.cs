using System;
using System.IO;

namespace DesktopTool.Services;

public static class ShellFolderSortHelper
{
	public readonly struct FolderSortInfo
	{
		public static readonly FolderSortInfo Default = new FolderSortInfo(0, ascending: true);

		public int Mode { get; init; }

		public bool Ascending { get; init; }

		public FolderSortInfo(int mode, bool ascending)
		{
			Mode = mode;
			Ascending = ascending;
		}
	}

	public static FolderSortInfo GetFolderSortInfo(string folderPath)
	{
		try
		{
			if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
			{
				return FolderSortInfo.Default;
			}
			string iniPath = Path.Combine(folderPath, "desktop.ini");
			if (!File.Exists(iniPath))
			{
				return FolderSortInfo.Default;
			}
			bool inShell = false;
			string[] array = File.ReadAllLines(iniPath);
			for (int i = 0; i < array.Length; i++)
			{
				string line = array[i].Trim();
				if (line.StartsWith("[", StringComparison.OrdinalIgnoreCase))
				{
					inShell = line.Equals("[.ShellClassInfo]", StringComparison.OrdinalIgnoreCase);
				}
				else if (inShell && line.StartsWith("SortColumn=", StringComparison.OrdinalIgnoreCase))
				{
					FolderSortInfo? info = ParseSortColumn(line.Substring("SortColumn=".Length).Trim());
					if (info.HasValue)
					{
						return info.Value;
					}
				}
			}
		}
		catch
		{
		}
		return FolderSortInfo.Default;
	}

	private static FolderSortInfo? ParseSortColumn(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return null;
		}
		string body = value;
		if (body.StartsWith("prop:", StringComparison.OrdinalIgnoreCase))
		{
			body = body.Substring("prop:".Length);
		}
		string first = body.Split(';')[0].Trim();
		if (string.IsNullOrEmpty(first))
		{
			return null;
		}
		bool descending = first.StartsWith("-", StringComparison.OrdinalIgnoreCase);
		if (descending)
		{
			first = first.Substring(1).Trim();
		}
		int mode = MapCanonicalNameToMode(first);
		if (mode < 0)
		{
			return null;
		}
		return new FolderSortInfo(mode, !descending);
	}

	private static int MapCanonicalNameToMode(string canonicalName)
	{
		string n = canonicalName.Trim();
		if (n.Equals("System.ItemNameDisplay", StringComparison.OrdinalIgnoreCase) || n.Equals("System.FileName", StringComparison.OrdinalIgnoreCase) || n.Equals("name", StringComparison.OrdinalIgnoreCase))
		{
			return 0;
		}
		if (n.Equals("System.DateModified", StringComparison.OrdinalIgnoreCase) || n.Equals("System.DateCreated", StringComparison.OrdinalIgnoreCase) || n.Equals("date", StringComparison.OrdinalIgnoreCase) || n.Equals("datemodified", StringComparison.OrdinalIgnoreCase))
		{
			return 1;
		}
		if (n.Equals("System.ItemTypeText", StringComparison.OrdinalIgnoreCase) || n.Equals("System.FileExtension", StringComparison.OrdinalIgnoreCase) || n.Equals("type", StringComparison.OrdinalIgnoreCase))
		{
			return 2;
		}
		if (n.Equals("System.Size", StringComparison.OrdinalIgnoreCase) || n.Equals("size", StringComparison.OrdinalIgnoreCase))
		{
			return 3;
		}
		return -1;
	}
}
