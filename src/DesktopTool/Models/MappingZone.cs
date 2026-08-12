using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DesktopTool.Models;

public class MappingZone : INotifyPropertyChanged
{
	private string _name = "新映射区";

	public string Id { get; set; } = Guid.NewGuid().ToString("N");

	public string Name
	{
		get
		{
			return _name;
		}
		set
		{
			_name = value;
			OnPropertyChanged("Name");
		}
	}

	public string FolderPath { get; set; } = "";

	public ObservableCollection<MappingGroup> Groups { get; set; } = new ObservableCollection<MappingGroup>();

	public double X { get; set; }

	public double Y { get; set; }

	public double Width { get; set; } = 360.0;

	public double Height { get; set; } = 420.0;

	public bool IsLocked { get; set; }

	public bool IsCollapsed { get; set; }

	public int ActiveGroupIndex { get; set; }

	public int IconSizeLevel { get; set; } = 1;

	public int IconSortMode { get; set; }

	public bool IconSortAscending { get; set; } = true;

	public bool IconSortUseFolder { get; set; }

	[JsonIgnore]
	public int TotalFileCount => Groups?.Sum((MappingGroup g) => g.Files?.Count ?? 0) ?? 0;

	public event PropertyChangedEventHandler? PropertyChanged;

	public void RefreshFileCounts()
	{
		if (Groups == null)
		{
			return;
		}
		foreach (MappingGroup group in Groups)
		{
			string path = group.FolderPath;
			if (string.IsNullOrEmpty(path))
			{
				path = FolderPath;
			}
			if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
			{
				if (group.Files.Count > 0)
				{
					group.Files.Clear();
					OnPropertyChanged("TotalFileCount");
				}
				continue;
			}
			try
			{
				List<FileSystemInfo> infos = (from f in new DirectoryInfo(path).GetFileSystemInfos()
					where (f.Attributes & FileAttributes.Hidden) == 0
					select f).ToList();
				Dictionary<string, MappingFileItem> existingByPath = group.Files.ToDictionary<MappingFileItem, string>((MappingFileItem it) => it.FilePath, StringComparer.OrdinalIgnoreCase);
				Dictionary<string, FileSystemInfo> infoByPath = infos.ToDictionary<FileSystemInfo, string>((FileSystemInfo f) => f.FullName, StringComparer.OrdinalIgnoreCase);
				List<MappingFileItem> desired = new List<MappingFileItem>(infos.Count);
				foreach (MappingFileItem existing in group.Files)
				{
					if (infoByPath.TryGetValue(existing.FilePath, out var fi))
					{
						existing.FileName = fi.Name;
						existing.DisplayName = fi.Name;
						existing.IsDirectory = (fi.Attributes & FileAttributes.Directory) != 0;
						existing.Extension = fi.Extension;
						existing.LastWriteTime = fi.LastWriteTime;
						existing.FileSize = ((fi is FileInfo fiInfo) ? fiInfo.Length : 0);
						desired.Add(existing);
					}
				}
				foreach (FileSystemInfo fi2 in infos)
				{
					if (!existingByPath.ContainsKey(fi2.FullName))
					{
						desired.Add(new MappingFileItem
						{
							FilePath = fi2.FullName,
							FileName = fi2.Name,
							DisplayName = fi2.Name,
							IsDirectory = ((fi2.Attributes & FileAttributes.Directory) != 0),
							Extension = fi2.Extension,
							LastWriteTime = fi2.LastWriteTime,
							FileSize = ((fi2 is FileInfo fiInfo2) ? fiInfo2.Length : 0)
						});
					}
				}
				int i = 0;
				foreach (MappingFileItem want in desired)
				{
					if (i < group.Files.Count && group.Files[i] == want)
					{
						i++;
						continue;
					}
					int idx = group.Files.IndexOf(want);
					if (idx == -1)
					{
						group.Files.Insert(i, want);
					}
					else if (idx != i)
					{
						group.Files.Move(idx, i);
					}
					i++;
				}
				while (group.Files.Count > desired.Count)
				{
					group.Files.RemoveAt(desired.Count);
				}
			}
			catch (Exception)
			{
			}
			OnPropertyChanged("TotalFileCount");
		}
	}

	protected void OnPropertyChanged([CallerMemberName] string? name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
