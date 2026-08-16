using RomForge.Core.Models;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace RomForge.Core.UI.Helpers;

public static class LogAutoScrollBehavior
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(LogAutoScrollBehavior), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox listBox || e.NewValue is not true)
            return;

        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListBox));

        descriptor?.AddValueChanged(listBox, (_, _) => OnItemsSourceChanged(listBox));

        OnItemsSourceChanged(listBox);
    }

    private static void OnItemsSourceChanged(ListBox listBox)
    {
        if (listBox.ItemsSource is not ObservableCollection<LogEntry> entries)
            return;

        if (LogSubscriptions.Get(listBox) is { } previous && !ReferenceEquals(previous.Entries, entries))
            previous.Entries.CollectionChanged -= previous.Handler;

        void handler(object? s, NotifyCollectionChangedEventArgs args)
        {
            if (args.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
                ScrollToEnd(listBox);
        }

        entries.CollectionChanged += handler;
        LogSubscriptions.Set(listBox, entries, handler);

        ScrollToEnd(listBox);
    }

    private static void ScrollToEnd(ListBox listBox)
    {
        listBox.Dispatcher.InvokeAsync(() =>
        {
            if (listBox.Items.Count > 0)
                listBox.ScrollIntoView(listBox.Items[^1]);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static class LogSubscriptions
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ListBox, Subscription> _table = new();

        public static Subscription? Get(ListBox listBox) => _table.TryGetValue(listBox, out var s) ? s : null;

        public static void Set(ListBox listBox, ObservableCollection<LogEntry> entries, NotifyCollectionChangedEventHandler handler)
        {
            _table.Remove(listBox);
            _table.Add(listBox, new Subscription(entries, handler));
        }

        public class Subscription(ObservableCollection<LogEntry> entries, NotifyCollectionChangedEventHandler handler)
        {
            public ObservableCollection<LogEntry> Entries { get; } = entries;
            public NotifyCollectionChangedEventHandler Handler { get; } = handler;
        }
    }
}