using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Timers;
using System.Windows;
using DesktopTool.Models;
using DesktopTool.ViewModels;

namespace DesktopTool.Services;

public class TodoSchedulerService : IDisposable
{
	private readonly ObservableCollection<TodoItem> _todos;

	private readonly MainViewModel _mainVm;

	private readonly TrayService _tray;

	private Timer? _timer;

	private static readonly TimeSpan ReminderGrace = TimeSpan.FromHours(24.0);

	private static readonly TimeSpan OccurrenceGrace = TimeSpan.FromHours(24.0);

	public TodoSchedulerService(ObservableCollection<TodoItem> todos, MainViewModel mainVm, TrayService tray)
	{
		_todos = todos;
		_mainVm = mainVm;
		_tray = tray;
	}

	public void Start()
	{
		if (_timer == null)
		{
			_timer = new Timer
			{
				AutoReset = false
			};
			_timer.Elapsed += delegate
			{
				SafeTick();
			};
			SafeTick();
		}
	}

	public void Stop()
	{
		_timer?.Stop();
	}

	public void Dispose()
	{
		_timer?.Stop();
		_timer?.Dispose();
		_timer = null;
		GC.SuppressFinalize(this);
	}

	private void SafeTick()
	{
		Application app = Application.Current;
		if (app == null)
		{
			return;
		}
		try
		{
			if (app.Dispatcher.CheckAccess())
			{
				Tick();
			}
			else
			{
				app.Dispatcher.Invoke(Tick);
			}
		}
		catch (Exception)
		{
		}
		finally
		{
			ScheduleNext();
		}
	}

	private void ScheduleNext()
	{
		if (_timer != null)
		{
			DateTime next = DateTime.Now.AddMinutes(1.0);
			long delay = (long)(new DateTime(next.Year, next.Month, next.Day, next.Hour, next.Minute, 0, DateTimeKind.Local) - DateTime.Now).TotalMilliseconds;
			if (delay < 200)
			{
				delay = 200L;
			}
			_timer.Interval = delay;
			_timer.Start();
		}
	}

	private void Tick()
	{
		DateTime now = DateTime.Now;
		bool changed = false;
		List<(string, string)> reminders = new List<(string, string)>();
		bool reminderDismissed = false;
		foreach (TodoItem t in _todos)
		{
			if (!t.ReminderEnabled || !t.ReminderTime.HasValue)
			{
				continue;
			}
			DateTime rt = t.ReminderTime.Value;
			if (!(rt > now) && (!t.LastRemindedAt.HasValue || !(t.LastRemindedAt.Value >= rt)) && !(now - rt > ReminderGrace))
			{
				reminders.Add((t.Content ?? "(空待办)", rt.ToString("MM-dd HH:mm")));
				t.LastRemindedAt = now;
				if (t.ReminderEnabled)
				{
					t.ReminderEnabled = false;
					t.ReminderTime = null;
					reminderDismissed = true;
				}
				changed = true;
			}
		}
		bool recurrenceChanged = false;
		List<string> recCreatedNames = new List<string>();
		if (_mainVm.RecurringTasks != null)
		{
			foreach (RecurringTask def in _mainVm.RecurringTasks)
			{
				if (!def.Enabled || !GenerateFromDefinition(def, now, out var created) || created.Count <= 0)
				{
					continue;
				}
				int order = ((_todos.Count != 0) ? _todos.Max((TodoItem todoItem) => todoItem.SortOrder) : 0);
				foreach (TodoItem inst in created)
				{
					order = (inst.SortOrder = order + 1);
					_todos.Add(inst);
					recCreatedNames.Add(inst.Content ?? "(空待办)");
				}
				recurrenceChanged = true;
			}
		}
		if (recurrenceChanged)
		{
			_mainVm.ResortTodosByDate(ascending: false);
		}
		if (changed || recurrenceChanged)
		{
			_mainVm.SaveTodos();
		}
		if (recurrenceChanged)
		{
			_mainVm.SaveRecurringTasks();
		}
		foreach (var (msg, time) in reminders)
		{
			_tray.ShowNotification("⏰ 待办提醒", msg, time);
		}
		foreach (string name in recCreatedNames)
		{
			_tray.ShowNotification("\ud83d\udd04 循环任务", "已创建循环任务：" + name);
		}
		if (reminderDismissed)
		{
			_mainVm.NotifyTodoViewRefreshed();
		}
	}

	private bool GenerateFromDefinition(RecurringTask def, DateTime now, out List<TodoItem> created)
	{
		created = new List<TodoItem>();
		DateTime anchor = def.LastGeneratedDate ?? def.CreatedAt.Date;
		int guard = 0;
		switch (def.RecurrenceType)
		{
		case TodoRecurrenceType.Daily:
		{
			DateTime cur3 = anchor;
			while (guard++ < 400)
			{
				DateTime occ3 = cur3 + def.RecurrenceTime;
				if (!(occ3 <= now))
				{
					break;
				}
				created.Add(MakeNormalTodo(def, occ3));
				cur3 = cur3.AddDays(1.0);
			}
			def.LastGeneratedDate = cur3;
			break;
		}
		case TodoRecurrenceType.Weekly:
		{
			DateTime today = now.Date;
			DateTime cur2 = anchor;
			while (cur2 <= today && guard++ < 400)
			{
				if (cur2.DayOfWeek == def.RecurrenceWeekday)
				{
					DateTime occ2 = cur2;
					if (occ2 <= now)
					{
						created.Add(MakeNormalTodo(def, occ2));
					}
				}
				cur2 = cur2.AddDays(1.0);
			}
			def.LastGeneratedDate = cur2;
			break;
		}
		case TodoRecurrenceType.Monthly:
		{
			DateTime today2 = now.Date;
			DateTime cur4 = anchor;
			while (cur4 <= today2 && guard++ < 400)
			{
				int dom = cur4.Day;
				int target = Math.Clamp(def.RecurrenceMonthDay, 1, 31);
				if (dom == target || (target > DateTime.DaysInMonth(cur4.Year, cur4.Month) && dom == DateTime.DaysInMonth(cur4.Year, cur4.Month)))
				{
					DateTime occ4 = cur4;
					if (occ4 <= now)
					{
						created.Add(MakeNormalTodo(def, occ4));
					}
				}
				cur4 = cur4.AddDays(1.0);
			}
			def.LastGeneratedDate = cur4;
			break;
		}
		case TodoRecurrenceType.Yearly:
		{
			DateTime cur = anchor.Date;
			int guardY = 0;
			while (guardY++ < 400)
			{
				int y = cur.Year;
				int targetMonth = def.RecurrenceDate.Month;
				int targetDay = Math.Min(def.RecurrenceDate.Day, DateTime.DaysInMonth(y, targetMonth));
				DateTime occ = new DateTime(y, targetMonth, targetDay);
				if (occ > now)
				{
					break;
				}
				if (now - occ <= OccurrenceGrace)
				{
					created.Add(MakeNormalTodo(def, occ));
					cur = new DateTime(y + 1, 1, 1);
				}
				else
				{
					cur = new DateTime(y + 1, 1, 1);
				}
			}
			def.LastGeneratedDate = cur;
			break;
		}
		}
		return created.Count > 0;
	}

	private static TodoItem MakeNormalTodo(RecurringTask def, DateTime occurrence)
	{
		return new TodoItem
		{
			Content = def.Name,
			CreatedAt = occurrence,
			DueDate = null,
			IsCompleted = false,
			ReminderEnabled = false
		};
	}
}
