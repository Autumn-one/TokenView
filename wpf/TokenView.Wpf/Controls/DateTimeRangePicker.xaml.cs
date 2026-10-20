using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TokenView.Wpf.Controls;

public partial class DateTimeRangePicker : UserControl
{
    private const long MinuteMs = 60_000;
    private const int CellCount = 42;

    private DateTime _leftMonth;
    private DateTime? _startDate;
    private DateTime? _endDate;
    private DateTime? _hoverDate;
    private CalendarDay[] _leftDays = [];
    private CalendarDay[] _rightDays = [];
    private bool _syncing;

    public DateTimeRangePicker()
    {
        InitializeComponent();

        for (var hour = 0; hour < 24; hour++)
        {
            var text = hour.ToString("00", CultureInfo.InvariantCulture);
            StartHour.Items.Add(text);
            EndHour.Items.Add(text);
        }
        for (var minute = 0; minute < 60; minute++)
        {
            var text = minute.ToString("00", CultureInfo.InvariantCulture);
            StartMinute.Items.Add(text);
            EndMinute.Items.Add(text);
        }

        _leftMonth = MonthStart(DateTime.Today);
        _syncing = true;
        StartHour.SelectedIndex = 0;
        StartMinute.SelectedIndex = 0;
        EndHour.SelectedIndex = 23;
        EndMinute.SelectedIndex = 59;
        _syncing = false;

        BuildCalendars();
        UpdateFooter();
    }

    public event EventHandler<RangeConfirmedEventArgs>? RangeConfirmed;

    public event EventHandler? CancelRequested;

    public void SetRange(long fromMs, long toMs)
    {
        var endBoundaryMs = toMs > 0 ? toMs : DateTimeOffset.Now.ToUnixTimeMilliseconds() + MinuteMs;
        var end = FromUnixMs(endBoundaryMs - MinuteMs);
        var start = fromMs > 0 ? FromUnixMs(fromMs) : end.AddDays(-30);
        if (start > end)
        {
            start = end;
        }

        _startDate = start.Date;
        _endDate = end.Date;
        _hoverDate = null;
        _leftMonth = MonthStart(start);

        _syncing = true;
        SetTime(StartHour, StartMinute, start);
        SetTime(EndHour, EndMinute, end);
        _syncing = false;

        HideError();
        BuildCalendars();
        UpdateFooter();
    }

    private void BuildCalendars()
    {
        LeftMonthTitle.Text = _leftMonth.ToString("yyyy 年 M 月", CultureInfo.CurrentCulture);
        RightMonthTitle.Text = _leftMonth.AddMonths(1).ToString("yyyy 年 M 月", CultureInfo.CurrentCulture);

        _leftDays = BuildMonth(_leftMonth);
        _rightDays = BuildMonth(_leftMonth.AddMonths(1));
        LeftDays.ItemsSource = _leftDays;
        RightDays.ItemsSource = _rightDays;
        UpdateHighlights();
    }

    private static CalendarDay[] BuildMonth(DateTime month)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var start = first.AddDays(-offset);
        var cells = new CalendarDay[CellCount];
        for (var index = 0; index < cells.Length; index++)
        {
            var date = start.AddDays(index);
            cells[index] = new CalendarDay(date, date.Month == first.Month && date.Year == first.Year);
        }
        return cells;
    }

    private void UpdateHighlights()
    {
        DateTime? rangeEnd = _endDate;
        if (rangeEnd is null && _startDate is { } start && _hoverDate is { } hover && hover >= start)
        {
            rangeEnd = hover;
        }

        var hasRange = _startDate is not null && rangeEnd is not null;
        foreach (var cell in _leftDays)
        {
            UpdateCell(cell, hasRange, rangeEnd);
        }
        foreach (var cell in _rightDays)
        {
            UpdateCell(cell, hasRange, rangeEnd);
        }
    }

    private void UpdateCell(CalendarDay cell, bool hasRange, DateTime? rangeEnd)
    {
        cell.IsEdge =
            (_startDate is { } startDate && cell.Date == startDate) ||
            (_endDate is { } endDate && cell.Date == endDate);
        cell.IsInRange =
            hasRange &&
            _startDate is { } start &&
            rangeEnd is { } end &&
            cell.Date >= start &&
            cell.Date <= end;
    }

    private void SelectDate(DateTime date)
    {
        date = date.Date;
        if (_startDate is null || _endDate is not null)
        {
            _startDate = date;
            _endDate = null;
        }
        else if (date < _startDate.Value)
        {
            _startDate = date;
        }
        else
        {
            _endDate = date;
        }

        _hoverDate = null;
        HideError();
        UpdateHighlights();
        UpdateFooter();
    }

    private void PreviousMonth_Click(object sender, RoutedEventArgs e) => ShiftMonth(-1);

    private void NextMonth_Click(object sender, RoutedEventArgs e) => ShiftMonth(1);

    private void ShiftMonth(int delta)
    {
        _leftMonth = _leftMonth.AddMonths(delta);
        _hoverDate = null;
        BuildCalendars();
    }

    private void Day_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalendarDay cell })
        {
            SelectDate(cell.Date);
        }
    }

    private void Day_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_startDate is null || _endDate is not null)
        {
            return;
        }
        if (sender is FrameworkElement { DataContext: CalendarDay cell } && _hoverDate != cell.Date)
        {
            _hoverDate = cell.Date;
            UpdateHighlights();
        }
    }

    private void CalendarArea_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_hoverDate is null)
        {
            return;
        }
        _hoverDate = null;
        UpdateHighlights();
    }

    private void Time_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing)
        {
            HideError();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _startDate = null;
        _endDate = null;
        _hoverDate = null;

        _syncing = true;
        StartHour.SelectedIndex = 0;
        StartMinute.SelectedIndex = 0;
        EndHour.SelectedIndex = 23;
        EndMinute.SelectedIndex = 59;
        _syncing = false;

        HideError();
        UpdateHighlights();
        UpdateFooter();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_startDate is not { } startDate || _endDate is not { } endDate)
        {
            ShowError("请选择开始和结束日期");
            return;
        }
        if (StartHour.SelectedIndex < 0 ||
            StartMinute.SelectedIndex < 0 ||
            EndHour.SelectedIndex < 0 ||
            EndMinute.SelectedIndex < 0)
        {
            ShowError("请选择完整的时间");
            return;
        }

        var start = startDate.Date
            .AddHours(StartHour.SelectedIndex)
            .AddMinutes(StartMinute.SelectedIndex);
        var end = endDate.Date
            .AddHours(EndHour.SelectedIndex)
            .AddMinutes(EndMinute.SelectedIndex);
        if (end < start)
        {
            ShowError("结束时间不能早于开始时间");
            return;
        }

        var fromMs = new DateTimeOffset(start).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(end).ToUnixTimeMilliseconds() + MinuteMs;
        RangeConfirmed?.Invoke(this, new RangeConfirmedEventArgs(fromMs, toMs));
    }

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }
        e.Handled = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateFooter()
    {
        StartDateText.Text = _startDate?.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) ?? "未选择";
        EndDateText.Text = _endDate?.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) ?? "未选择";
    }

    private void HideError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static void SetTime(ComboBox hours, ComboBox minutes, DateTime value)
    {
        hours.SelectedIndex = value.Hour;
        minutes.SelectedIndex = value.Minute;
    }

    private static DateTime MonthStart(DateTime value) => new(value.Year, value.Month, 1);

    private static DateTime FromUnixMs(long milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).LocalDateTime;
}

public sealed class RangeConfirmedEventArgs : EventArgs
{
    public RangeConfirmedEventArgs(long fromMs, long toMs)
    {
        FromMs = fromMs;
        ToMs = toMs;
    }

    public long FromMs { get; }

    public long ToMs { get; }
}

internal sealed class CalendarDay : INotifyPropertyChanged
{
    private bool _isInRange;
    private bool _isEdge;

    public CalendarDay(DateTime date, bool isCurrentMonth)
    {
        Date = date.Date;
        IsCurrentMonth = isCurrentMonth;
        IsToday = Date == DateTime.Today;
    }

    public DateTime Date { get; }

    public string Day => Date.Day.ToString(CultureInfo.InvariantCulture);

    public bool IsCurrentMonth { get; }

    public bool IsToday { get; }

    public bool IsInRange
    {
        get => _isInRange;
        set => Set(ref _isInRange, value);
    }

    public bool IsEdge
    {
        get => _isEdge;
        set => Set(ref _isEdge, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
