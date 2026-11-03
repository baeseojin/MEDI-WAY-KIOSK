using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediWay.Kiosk;
using MediWay.Kiosk.Controls;
using MediWay.Kiosk.ViewModels;

internal static class AccessibilityChecks
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                var api = new FakeApi();
                using var vm = new KioskViewModel(api);
                window.DataContext = vm;
                window.Width = 440; window.Height = 720;
                window.ShowActivated = false; window.ShowInTaskbar = false; window.Show();
                Layout();
                var input = (TextBox)window.FindName("PatientNumberInput");
                var normalSize = input.FontSize;
                window.AccessibleMode = true; Layout();
                Check(input.FontSize > normalSize && IsWhite(input.Foreground) && input.Background.ToString() == "#FF000000", "Input switches font and contrast at minimum window size");
                Check(Descendants(window).OfType<Button>().Where(b => b.Content?.ToString() == "1").Single().MinHeight == 72, "Keypad has enlarged touch target");
                var scanner = (TextBox)window.FindName("ScannerInput");
                var expander = Descendants(window).OfType<Expander>().Single();
                expander.IsExpanded = true; Layout();
                var scanBinding = scanner.InputBindings.OfType<System.Windows.Input.KeyBinding>().Single();
                Check(scanBinding.Key == System.Windows.Input.Key.Enter && ReferenceEquals(scanBinding.Command, vm.ScanCommand), "Scanner Enter binding targets scan command in rendered WPF view");
                scanner.Text = "MEDIWAY:P0001"; Layout();
                Check(vm.BarcodeInput == "MEDIWAY:P0001" && IsWhite(scanner.Foreground), "Scanner text binding and high contrast work together");
                ((AsyncUiCommand)scanBinding.Command).ExecuteAsync().GetAwaiter().GetResult(); Layout();
                var combo = Descendants(window).OfType<ComboBox>().Single();
                combo.IsDropDownOpen = true; Layout();
                Check(vm.IsConfirm && IsWhite(combo.Foreground) && combo.Background.ToString() == "#FF000000", "Confirmation selector keeps high contrast");
                var roomItem=(ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(0);
                var roomPeer=System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(roomItem);
                Check(roomPeer!.GetName()==api.Rooms[0].Name,"Room selector accessibility exposes name without internal DTO identifiers");
                combo.IsDropDownOpen = false;
                Layout();
                var roomPresenter = Descendants(combo).OfType<ContentPresenter>().Single(p => p.Name == "RoomSelectionPresenter");
                Check(Descendants(roomPresenter).OfType<TextBlock>().Any(t => t.Text == api.Rooms[0].Name)
                    && !Descendants(roomPresenter).OfType<TextBlock>().Any(t => t.Text.Contains("RoomResponse")), "Closed selector renders room name rather than record dump in high contrast");
                vm.SelectedRoom = api.Rooms[1]; window.AccessibleMode = false; Layout();
                Check(Descendants(roomPresenter).OfType<TextBlock>().Any(t => t.Text == api.Rooms[1].Name), "Room name updates after selection and standard-theme change");
                vm.SelectedRoom = api.Rooms[0]; window.AccessibleMode = true; Layout();
                vm.RegisterCommand.ExecuteAsync().GetAwaiter().GetResult(); Layout();
                var id = vm.Result!.Visit.Id;
                window.AccessibleMode = false; Layout(); window.AccessibleMode = true; Layout();
                Check(vm.IsResult && vm.Result.Visit.Id == id && api.Requests.Count == 1, "Theme toggles preserve receipt and never register twice");
                vm.GuideCommand.ExecuteAsync().GetAwaiter().GetResult(); Layout();
                var map = Descendants(window).OfType<NavigationMap>().Single();
                Check(map.AccessibleMode && Descendants(map).OfType<System.Windows.Shapes.Path>().Any(p => p.Stroke.ToString() == "#FFFFFF00"), "Guide binds accessible mode and renders yellow route");
                var firstLeg = vm.Route!;
                var secondLeg = firstLeg with { Map = firstLeg.Map with { Code = "F2", Name = "2층" } };
                map.Route = firstLeg with { Legs = [firstLeg, secondLeg], TransferNotice = "엘리베이터로 2층 이동" }; Layout();
                var floorButton = Descendants(map).OfType<Button>().Single(b => b.Content?.ToString() == "2층 보기");
                floorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
                Check(Descendants(map).OfType<TextBlock>().Any(t => t.Text.StartsWith("2층 ·")), "WPF guide switches between floor legs using next-floor button");
                var screenScroll=(ScrollViewer)window.FindName("ScreenScrollViewer");
                screenScroll.ScrollToBottom(); Layout();
                Check(screenScroll.VerticalOffset>0,"Large guide can scroll to lower actions before changing screen");
                var previousOffset=screenScroll.VerticalOffset;
                vm.Touch(); vm.Tick(); Layout();
                Check(screenScroll.VerticalOffset==previousOffset,"Same-screen updates preserve patient scroll position");
                vm.MobileCommand.ExecuteAsync().GetAwaiter().GetResult(); Layout();
                Check(screenScroll.VerticalOffset==0,"New QR screen resets previous guide scroll and reveals heading");
                Check(vm.IsQr && window.AccessibleMode && vm.Result.Visit.Id == id, "QR navigation preserves accessible mode and visit");
                vm.HomeCommand.Execute(null); Layout();
                Check(vm.IsInput && window.AccessibleMode && vm.Result is null, "Home clears patient state and retains terminal display preference");
                window.AccessibleMode = false; Layout();
                Check(input.FontSize == normalSize, "Standard font size restores without accumulating scale");
                void Layout() { window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("WPF accessibility verification failed", failure);
    }
    private static bool IsWhite(Brush brush) => brush.ToString() == "#FFFFFFFF";
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        Console.WriteLine("PASS: " + label);
    }
}
