using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DesktopTool.Models;

public class TodoItem : INotifyPropertyChanged
{
	private string _content = string.Empty;

	private bool _isCompleted;

	private DateTime _createdAt = DateTime.Now;

	private DateTime? _completedAt;

	private string? _color;

	private DateTime? _dueDate;

	public string Id { get; set; } = Guid.NewGuid().ToString("N");

	public string Content
	{
		get
		{
			return _content;
		}
		set
		{
			if (_content != value)
			{
				_content = value;
				OnPropertyChanged("Content");
			}
		}
	}

	public bool IsCompleted
	{
		get
		{
			return _isCompleted;
		}
		set
		{
			if (_isCompleted != value)
			{
				_isCompleted = value;
				OnPropertyChanged("IsCompleted");
			}
		}
	}

	public int SortOrder { get; set; }

	public string? Color
	{
		get
		{
			return _color;
		}
		set
		{
			if (_color != value)
			{
				_color = value;
				OnPropertyChanged("Color");
			}
		}
	}

	public DateTime CreatedAt
	{
		get
		{
			return _createdAt;
		}
		set
		{
			if (_createdAt != value)
			{
				_createdAt = value;
				OnPropertyChanged("CreatedAt");
				OnPropertyChanged("FormattedDate");
				OnPropertyChanged("FormattedDueDate");
				OnPropertyChanged("DateKey");
			}
		}
	}

	public DateTime? CompletedAt
	{
		get
		{
			return _completedAt;
		}
		set
		{
			if (_completedAt != value)
			{
				_completedAt = value;
				OnPropertyChanged("CompletedAt");
			}
		}
	}

	public DateTime? DueDate
	{
		get
		{
			return _dueDate;
		}
		set
		{
			if (_dueDate != value)
			{
				_dueDate = value;
				OnPropertyChanged("DueDate");
				OnPropertyChanged("FormattedDueDate");
				OnPropertyChanged("HasDueDate");
				OnPropertyChanged("DateKey");
			}
		}
	}

	public bool HasDueDate => _dueDate.HasValue;

	public string FormattedDate => CreatedAt.ToString("yyyy/MM/dd  ddd");

	public string FormattedDueDate
	{
		get
		{
			if (!_dueDate.HasValue)
			{
				return CreatedAt.ToString("yyyy-MM-dd");
			}
			return _dueDate.Value.ToString("yyyy-MM-dd");
		}
	}

	public DateTime DateRef => _dueDate ?? _createdAt;

	public DateTime DateKey => DateRef.Date;

	public bool ReminderEnabled { get; set; }

	public DateTime? ReminderTime { get; set; }

	public DateTime? LastRemindedAt { get; set; }

	public TodoRecurrenceType RecurrenceType { get; set; }

	public DayOfWeek RecurrenceWeekday { get; set; }

	public int RecurrenceMonthDay { get; set; }

	public TimeSpan RecurrenceTime { get; set; } = new TimeSpan(9, 0, 0);

	public Guid RecurrenceId { get; set; }

	public DateTime? OccurrenceDate { get; set; }

	public bool IsRecurring
	{
		get
		{
			if (RecurrenceType != TodoRecurrenceType.Daily && RecurrenceType != TodoRecurrenceType.Weekly && RecurrenceType != TodoRecurrenceType.Monthly)
			{
				return RecurrenceType == TodoRecurrenceType.Yearly;
			}
			return true;
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
