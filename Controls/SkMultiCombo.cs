using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Apathy.Models;

namespace Apathy.Controls;

/// <summary>
/// Dropdown where several options can be toggled at once. The box shows the selected
/// names joined with commas ("-" when nothing is selected).
/// Press on an option and drag over others to "paint" them: every row the mouse passes gets
/// the state the first row switched to (start on an unselected row = select, selected = deselect).
/// </summary>
[TemplatePart(Name = "PART_Items", Type = typeof(ItemsControl))]
public class SkMultiCombo : Control
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SkMultiCombo));

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(SkMultiCombo),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty IsDropDownOpenProperty =
        DependencyProperty.Register(nameof(IsDropDownOpen), typeof(bool), typeof(SkMultiCombo),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private static readonly DependencyPropertyKey DisplayTextPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(DisplayText), typeof(string), typeof(SkMultiCombo),
            new PropertyMetadata("-"));

    public static readonly DependencyProperty DisplayTextProperty = DisplayTextPropertyKey.DependencyProperty;

    private ItemsControl? _list;
    private bool _painting;
    private bool _paintValue;
    private int _lastIndex = -1;

    static SkMultiCombo()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SkMultiCombo), new FrameworkPropertyMetadata(typeof(SkMultiCombo)));
    }

    public string? Header
    {
        get => (string?)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>A collection of <see cref="SelectableOption"/>.</summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public bool IsDropDownOpen
    {
        get => (bool)GetValue(IsDropDownOpenProperty);
        set => SetValue(IsDropDownOpenProperty, value);
    }

    public string DisplayText => (string)GetValue(DisplayTextProperty);

    public override void OnApplyTemplate()
    {
        if (_list != null)
        {
            _list.MouseLeftButtonDown -= List_MouseLeftButtonDown;
            _list.MouseMove -= List_MouseMove;
            _list.MouseLeftButtonUp -= List_MouseLeftButtonUp;
            _list.LostMouseCapture -= List_LostMouseCapture;
        }

        base.OnApplyTemplate();

        _list = GetTemplateChild("PART_Items") as ItemsControl;
        if (_list != null)
        {
            _list.MouseLeftButtonDown += List_MouseLeftButtonDown;
            _list.MouseMove += List_MouseMove;
            _list.MouseLeftButtonUp += List_MouseLeftButtonUp;
            _list.LostMouseCapture += List_LostMouseCapture;
        }
    }

    private void List_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        int index = RowAt(e.GetPosition(_list), clampToEnds: false);
        if (index < 0 || OptionAt(index) is not { } option) return;

        _paintValue = !option.IsSelected;
        option.IsSelected = _paintValue;
        _lastIndex = index;
        _painting = true;

        // SubTree capture keeps hover highlighting working on the rows while dragging.
        Mouse.Capture(_list, CaptureMode.SubTree);
        e.Handled = true;
    }

    private void List_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_painting) return;

        int index = RowAt(e.GetPosition(_list), clampToEnds: true);
        if (index < 0 || index == _lastIndex) return;

        // Fill every row between the last one and this one, so a fast drag can't skip rows.
        int step = index > _lastIndex ? 1 : -1;
        for (int i = _lastIndex + step; i != index + step; i += step)
        {
            if (OptionAt(i) is { } option) option.IsSelected = _paintValue;
        }
        _lastIndex = index;
    }

    private void List_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_painting) return;

        _painting = false;
        if (Mouse.Captured == _list) _list!.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void List_LostMouseCapture(object sender, MouseEventArgs e) => _painting = false;

    private SelectableOption? OptionAt(int index) =>
        _list != null && index >= 0 && index < _list.Items.Count ? _list.Items[index] as SelectableOption : null;

    /// <summary>
    /// Index of the row under <paramref name="point"/> (relative to the list), or -1.
    /// With <paramref name="clampToEnds"/>, dragging above/below the list counts as the first/last row.
    /// </summary>
    private int RowAt(Point point, bool clampToEnds)
    {
        if (_list == null || _list.Items.Count == 0) return -1;
        if (point.X < 0 || point.X > _list.ActualWidth) return -1;

        double y = clampToEnds ? Math.Clamp(point.Y, 0, _list.ActualHeight - 1) : point.Y;
        for (int i = 0; i < _list.Items.Count; i++)
        {
            if (_list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row) continue;

            double top = row.TranslatePoint(new Point(), _list).Y;
            if (y >= top && y < top + row.ActualHeight) return i;
        }
        return -1;
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var combo = (SkMultiCombo)d;

        if (e.OldValue is IEnumerable oldItems)
        {
            if (oldItems is INotifyCollectionChanged oldCollection)
                oldCollection.CollectionChanged -= combo.Items_CollectionChanged;
            combo.Unsubscribe(oldItems);
        }

        if (e.NewValue is IEnumerable newItems)
        {
            if (newItems is INotifyCollectionChanged newCollection)
                newCollection.CollectionChanged += combo.Items_CollectionChanged;
            combo.Subscribe(newItems);
        }

        combo.UpdateDisplayText();
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) Unsubscribe(e.OldItems);
        if (e.NewItems != null) Subscribe(e.NewItems);
        UpdateDisplayText();
    }

    private void Subscribe(IEnumerable items)
    {
        foreach (var item in items.OfType<INotifyPropertyChanged>())
            item.PropertyChanged += Item_PropertyChanged;
    }

    private void Unsubscribe(IEnumerable items)
    {
        foreach (var item in items.OfType<INotifyPropertyChanged>())
            item.PropertyChanged -= Item_PropertyChanged;
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateDisplayText();

    private void UpdateDisplayText()
    {
        var selected = ItemsSource?.OfType<SelectableOption>().Where(o => o.IsSelected).Select(o => o.Name).ToList();
        SetValue(DisplayTextPropertyKey, selected is { Count: > 0 } ? string.Join(", ", selected) : "-");
    }
}
