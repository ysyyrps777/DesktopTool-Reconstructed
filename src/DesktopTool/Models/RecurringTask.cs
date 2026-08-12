using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DesktopTool.Models;

public class RecurringTask : INotifyPropertyChanged
{
	private string _id = Guid.NewGuid().ToString("N");

	private string _name = string.Empty;

	private TodoRecurrenceType _recurrenceType = TodoRecurrenceType.Daily;

	private DayOfWeek _recurrenceWeekday = DayOfWeek.Monday;

	private int _recurrenceMonthDay = 1;

	private TimeSpan _recurrenceTime = new TimeSpan(9, 0, 0);

	private DateTime _recurrenceDate = DateTime.Now.Date;

	private bool _enabled = true;

	private DateTime _createdAt = DateTime.Now;

	private DateTime? _lastGeneratedDate;

	public string Id
	{
		get
		{
			return _id;
		}
		set
		{
			if (_id != value)
			{
				_id = value;
				OnPropertyChanged("Id");
			}
		}
	}

	public string Name
	{
		get
		{
			return _name;
		}
		set
		{
			if (_name != value)
			{
				_name = value;
				OnPropertyChanged("Name");
			}
		}
	}

	public TodoRecurrenceType RecurrenceType
	{
		get
		{
			return _recurrenceType;
		}
		set
		{
			if (_recurrenceType != value)
			{
				_recurrenceType = value;
				OnPropertyChanged("RecurrenceType");
				OnPropertyChanged("RecurrenceSummary");
			}
		}
	}

	public DayOfWeek RecurrenceWeekday
	{
		get
		{
			return _recurrenceWeekday;
		}
		set
		{
			if (_recurrenceWeekday != value)
			{
				_recurrenceWeekday = value;
				OnPropertyChanged("RecurrenceWeekday");
				OnPropertyChanged("RecurrenceSummary");
			}
		}
	}

	public int RecurrenceMonthDay
	{
		get
		{
			return _recurrenceMonthDay;
		}
		set
		{
			if (_recurrenceMonthDay != value)
			{
				_recurrenceMonthDay = value;
				OnPropertyChanged("RecurrenceMonthDay");
				OnPropertyChanged("RecurrenceSummary");
			}
		}
	}

	public TimeSpan RecurrenceTime
	{
		get
		{
			return _recurrenceTime;
		}
		set
		{
			if (_recurrenceTime != value)
			{
				_recurrenceTime = value;
				OnPropertyChanged("RecurrenceTime");
				OnPropertyChanged("RecurrenceSummary");
			}
		}
	}

	public DateTime RecurrenceDate
	{
		get
		{
			return _recurrenceDate;
		}
		set
		{
			if (_recurrenceDate != value)
			{
				_recurrenceDate = value;
				OnPropertyChanged("RecurrenceDate");
				OnPropertyChanged("RecurrenceSummary");
			}
		}
	}

	public bool Enabled
	{
		get
		{
			return _enabled;
		}
		set
		{
			if (_enabled != value)
			{
				_enabled = value;
				OnPropertyChanged("Enabled");
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
			}
		}
	}

	public DateTime? LastGeneratedDate
	{
		get
		{
			return _lastGeneratedDate;
		}
		set
		{
			if (_lastGeneratedDate != value)
			{
				_lastGeneratedDate = value;
				OnPropertyChanged("LastGeneratedDate");
			}
		}
	}

	public string RecurrenceSummary
	{
		get
		{
			switch (RecurrenceType)
			{
			case TodoRecurrenceType.Daily:
				return $"每天 {RecurrenceTime:hh\\:mm}";
			case TodoRecurrenceType.Weekly:
			{
				string[] names = new string[7] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
				return "每周 " + names[(int)RecurrenceWeekday];
			}
			case TodoRecurrenceType.Monthly:
				return $"每月 {RecurrenceMonthDay} 号";
			case TodoRecurrenceType.Yearly:
				return $"每年 {RecurrenceDate:MM-dd}";
			default:
				return "未知";
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
