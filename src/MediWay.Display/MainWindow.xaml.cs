using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using MediWay.Display.Services;
using MediWay.Display.ViewModels;

namespace MediWay.Display;

public partial class MainWindow : Window
{
    private readonly HttpClient http;
    private readonly DisplayViewModel viewModel;
    private bool cleaned;
    private bool closing;
    public MainWindow()
    {
        InitializeComponent();
        var address = Environment.GetEnvironmentVariable("MEDIWAY_API_BASE_URL") ?? "http://localhost:5080/";
        var apiBase = new Uri(address.TrimEnd('/') + "/");
        http = new HttpClient { BaseAddress = apiBase, Timeout = TimeSpan.FromSeconds(10) };
        viewModel = new DisplayViewModel(new DisplayApi(http), new QueueConnection(apiBase), action => Dispatcher.BeginInvoke(action),
            announcer: new CallAnnouncer(new WindowsAnnouncementVoice()));
        DataContext = viewModel; Loaded += OnLoaded;
    }
    private void OnLoaded(object sender, RoutedEventArgs e) => viewModel.Start();
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (cleaned) { base.OnClosing(e); return; }
        e.Cancel = true; base.OnClosing(e);
        if (closing) return;
        closing = true; Loaded -= OnLoaded;
        try { await viewModel.DisposeAsync(); }
        finally { http.Dispose(); DataContext = null; cleaned = true; Close(); }
    }
}
