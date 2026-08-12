using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace DesktopTool.Models;

public class ZoneIcon : INotifyPropertyChanged
{
	private string _displayName = string.Empty;

	private BitmapSource? _iconImage;

	public string FilePath { get; set; } = string.Empty;

	public string DisplayName
	{
		get
		{
			return _displayName;
		}
		set
		{
			if (_displayName != value)
			{
				_displayName = value;
				OnPropertyChanged("DisplayName");
			}
		}
	}

	public string? HiddenPath { get; set; }

	public string? HiddenMethod { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? ListViewIndex { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? OriginalPositionX { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? OriginalPositionY { get; set; }

	[JsonIgnore]
	public string ResolvedPath
	{
		get
		{
			if (HiddenMethod == "Physical" && HiddenPath != null && (File.Exists(HiddenPath) || Directory.Exists(HiddenPath)))
			{
				return HiddenPath;
			}
			_ = HiddenMethod == "Attributes";
			return FilePath;
		}
	}

	[JsonIgnore]
	public bool IsHidden
	{
		get
		{
			if (!(HiddenMethod == "Physical") || HiddenPath == null)
			{
				return HiddenMethod == "Attributes";
			}
			return true;
		}
	}

	[JsonIgnore]
	public bool IsDirectory
	{
		get
		{
			string path = ResolvedPath;
			if (!string.IsNullOrEmpty(path))
			{
				return Directory.Exists(path);
			}
			return false;
		}
	}

	[JsonIgnore]
	public BitmapSource? IconImage
	{
		get
		{
			return _iconImage;
		}
		set
		{
			if (_iconImage != value)
			{
				_iconImage = value;
				OnPropertyChanged("IconImage");
			}
		}
	}

	[JsonIgnore]
	public string? FileSizeDisplay
	{
		get
		{
			try
			{
				string path = ResolvedPath;
				if (string.IsNullOrEmpty(path))
				{
					return null;
				}
				if (Directory.Exists(path))
				{
					try
					{
						int fileCount = Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly).Length;
						int dirCount = Directory.GetDirectories(path).Length;
						return $"{fileCount + dirCount} 个项目";
					}
					catch
					{
						return "文件夹";
					}
				}
				if (File.Exists(path))
				{
					long size = new FileInfo(path).Length;
					string[] units = new string[5] { "B", "KB", "MB", "GB", "TB" };
					int unitIndex = 0;
					double s = size;
					while (s >= 1024.0 && unitIndex < units.Length - 1)
					{
						s /= 1024.0;
						unitIndex++;
					}
					return $"{s:F1} {units[unitIndex]}";
				}
				return null;
			}
			catch
			{
				return null;
			}
		}
	}

	[JsonIgnore]
	public bool HasFileSize => !string.IsNullOrEmpty(FileSizeDisplay);

	[JsonIgnore]
	public DateTime? LastWriteTime
	{
		get
		{
			try
			{
				string path = ResolvedPath;
				if (string.IsNullOrEmpty(path))
				{
					return null;
				}
				if (File.Exists(path) || Directory.Exists(path))
				{
					return new FileInfo(path).LastWriteTime;
				}
				return null;
			}
			catch
			{
				return null;
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
