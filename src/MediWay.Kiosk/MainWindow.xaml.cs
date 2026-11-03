using System.Net.Http;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using MediWay.Kiosk.Services;
using MediWay.Kiosk.ViewModels;
using MediWay.Kiosk.Controls;

namespace MediWay.Kiosk;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty AccessibleModeProperty = DependencyProperty.Register(
        nameof(AccessibleMode), typeof(bool), typeof(MainWindow), new PropertyMetadata(false,
            (owner, args) => KioskTheme.Apply(((MainWindow)owner).Resources, (bool)args.NewValue)));
    public bool AccessibleMode { get => (bool)GetValue(AccessibleModeProperty); set => SetValue(AccessibleModeProperty, value); }
    private readonly HttpClient http;
    private readonly KioskViewModel viewModel;
    private readonly DispatcherTimer timer;
    private readonly ServerConnectionMonitor connection;
    private bool closing, cleaned;

    public MainWindow()
    {
        InitializeComponent();
        KioskTheme.Apply(Resources, false);
        var address = Environment.GetEnvironmentVariable("MEDIWAY_API_BASE_URL") ?? "http://localhost:5080/";
        http = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
        connection = new ServerConnectionMonitor(http, action => Dispatcher.BeginInvoke(action));
        var nodeSetting = Environment.GetEnvironmentVariable("MEDIWAY_KIOSK_NODE_ID");
        var ticketDirectory = Environment.GetEnvironmentVariable("MEDIWAY_TICKET_DIRECTORY") ?? WaitingTicketWriter.DefaultDirectory;
        var offlineDirectory = Environment.GetEnvironmentVariable("MEDIWAY_OFFLINE_DIRECTORY") ?? OfflineRegistrationStore.DefaultDirectory;
        viewModel = new KioskViewModel(new KioskApiClient(http, nodeSetting is null ? null : Guid.Parse(nodeSetting)),
            ticketWriter: new WaitingTicketWriter(ticketDirectory), offlineStore: new OfflineRegistrationStore(offlineDirectory, address)) { Connection = connection };
        DataContext = viewModel;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += OnTick;
        timer.Start();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) { if (ReferenceEquals(DataContext, viewModel)) { connection.Start(); await viewModel.InitializeCommand.ExecuteAsync(); } }
    private void OnTick(object? sender, EventArgs e) { viewModel.Tick(); viewModel.ReconcileCommand.Execute(null); }
    private void OnActivity(object sender, RoutedEventArgs e) => viewModel?.Touch();
    private void OnScannerExpanded(object sender, RoutedEventArgs e) => ScannerInput.Focus();

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (cleaned) { base.OnClosing(e); return; }
        e.Cancel = true; base.OnClosing(e);
        if (closing) return;
        closing = true;
        timer.Stop();
        timer.Tick -= OnTick;
        Loaded -= OnLoaded;
        viewModel.Dispose();
        try { await connection.DisposeAsync(); }
        finally { http.Dispose(); DataContext = null; cleaned = true; Close(); }
    }
}
