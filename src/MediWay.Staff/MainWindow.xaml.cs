using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using MediWay.Contracts;
using MediWay.Staff.Services;
using MediWay.Staff.ViewModels;

namespace MediWay.Staff;

public partial class MainWindow : Window
{
    private readonly HttpClient http;
    private readonly StaffViewModel viewModel;
    private readonly DispatcherTimer timer;
    private bool closed;
    private bool dragging;
    private Point dragStart;
    private Guid? dragVisit;
    public MainWindow()
    {
        InitializeComponent();
        var address = Environment.GetEnvironmentVariable("MEDIWAY_API_BASE_URL") ?? "http://localhost:5080/";
        http = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
        viewModel = new StaffViewModel(new StaffApiClient(http)); DataContext = viewModel;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; timer.Tick += OnTick;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e) { await viewModel.RefreshCommand.ExecuteAsync(); if (!closed) timer.Start(); }
    private void OnTick(object? sender, EventArgs e) { if (!dragging && !RoomSelector.IsDropDownOpen) viewModel.RefreshCommand.Execute(null); }
    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(WaitingGrid);
        dragVisit = (ItemsControl.ContainerFromElement(WaitingGrid, e.OriginalSource as DependencyObject) as DataGridRow)?.Item is QueueItem item ? item.VisitId : null;
    }
    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragVisit is not { } id || !viewModel.CanOperate || dragging) return;
        var point = e.GetPosition(WaitingGrid);
        if (Math.Abs(point.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragging = true;
        try { DragDrop.DoDragDrop(WaitingGrid, new DataObject("MediWay.QueueVisit", id), DragDropEffects.Move); }
        finally { dragging = false; dragVisit = null; }
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = dragging && viewModel.CanOperate && e.Data.GetDataPresent("MediWay.QueueVisit") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!dragging || !viewModel.CanOperate || e.Data.GetData("MediWay.QueueVisit") is not Guid source || dragVisit != source) return;
        if ((ItemsControl.ContainerFromElement(WaitingGrid, e.OriginalSource as DependencyObject) as DataGridRow)?.Item is QueueItem target)
            await viewModel.MoveBeforeAsync(source, target.VisitId);
    }
    protected override void OnClosed(EventArgs e)
    {
        closed = true; timer.Stop(); timer.Tick -= OnTick; Loaded -= OnLoaded; viewModel.Dispose(); http.Dispose(); DataContext = null; base.OnClosed(e);
    }
}
