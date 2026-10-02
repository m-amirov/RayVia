using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rayvia.Models;
using Rayvia.Services;

namespace Rayvia;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly SubscriptionService _subscriptionService = new();
    private readonly SystemProxyService _systemProxy = new();
    private readonly UpdateService _updateService = new();
    private readonly LogService _log = new();
    private readonly XrayCoreService _xray;

    private AppSettings _settings = new();
    private UpdateInfo? _pendingUpdate;
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();

        _xray = new XrayCoreService(_log);
        _log.EntryAdded += line =>
        {
            Dispatcher.Invoke(() =>
            {
                LogList.Items.Add(line);
                if (LogList.Items.Count > 500)
                    LogList.Items.RemoveAt(0);
                LogList.ScrollIntoView(line);
            });
        };

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsService.LoadAsync();
        _loaded = true;

        VersionText.Text = "v" + CurrentVersion().ToString(3);
        AutoUpdateCheck.IsChecked = _settings.AutoUpdate;
        HomePortText.Text = $"127.0.0.1:{_settings.HttpPort}";

        RefreshBindings();
        ApplyRoutingSelection();
        ShowHome();
        UpdateConnectionUi(false);

        _log.Write("Rayvia запущен.");

        if (_settings.AutoUpdate)
            _ = CheckForUpdatesAsync(false);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try { _systemProxy.Disable(); } catch { }
        _xray.Dispose();
    }

    private void RefreshBindings()
    {
        SubscriptionsList.ItemsSource = null;
        SubscriptionsList.ItemsSource = _settings.Subscriptions;

        RulesList.ItemsSource = null;
        RulesList.ItemsSource = _settings.Rules;

        ServerCombo.ItemsSource = null;
        ServerCombo.ItemsSource = _settings.Nodes;

        var selected = _settings.Nodes.FirstOrDefault(x => x.Id == _settings.SelectedNodeId)
                       ?? _settings.Nodes.FirstOrDefault();

        ServerCombo.SelectedItem = selected;

        HomeRoutingCombo.SelectedIndex = _settings.RoutingMode switch
        {
            RoutingMode.ProxyAll => 1,
            RoutingMode.DirectAll => 2,
            _ => 0
        };
    }

    private async Task SaveAsync()
    {
        if (_loaded)
            await _settingsService.SaveAsync(_settings);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_xray.IsRunning)
        {
            Disconnect();
            return;
        }

        if (ServerCombo.SelectedItem is not ProxyNode node)
        {
            MessageBox.Show(this, "Сначала добавьте подписку и выберите сервер.", "Rayvia", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            ConnectButton.IsEnabled = false;
            ConnectButton.Content = "Подключение…";

            _settings.SelectedNodeId = node.Id;
            await SaveAsync();

            await _xray.ConnectAsync(node, _settings);
            _systemProxy.Enable(_settings.HttpPort);
            UpdateConnectionUi(true);
        }
        catch (Exception ex)
        {
            try { _systemProxy.Disable(); } catch { }
            _xray.Disconnect();
            UpdateConnectionUi(false);
            _log.Write("Ошибка подключения: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Не удалось подключиться", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = _xray.IsRunning ? "Отключить" : "Подключить";
        }
    }

    private void Disconnect()
    {
        try { _systemProxy.Disable(); } catch { }
        _xray.Disconnect();
        UpdateConnectionUi(false);
        _log.Write("Отключено.");
    }

    private void UpdateConnectionUi(bool connected)
    {
        StatusText.Text = connected ? "Подключено" : "Не подключено";
        StatusDot.Fill = (Brush)FindResource(connected ? "SuccessBrush" : "MutedBrush");
        ConnectButton.Content = connected ? "Отключить" : "Подключить";
        HomeCoreText.Text = connected ? "Запущен" : "Не запущен";
        HomeProxyText.Text = connected ? "Включён" : "Выключен";
    }

    private async void AddSubscription_Click(object sender, RoutedEventArgs e)
        => await AddSubscriptionAsync();

    private async void SubscriptionUrlText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await AddSubscriptionAsync();
    }

    private async Task AddSubscriptionAsync()
    {
        var input = SubscriptionUrlText.Text.Trim();
        if (string.IsNullOrWhiteSpace(input))
            return;

        var isHttp = Uri.TryCreate(input, UriKind.Absolute, out var uri)
                     && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        var isNode = input.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
                     || input.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
                     || input.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)
                     || input.StartsWith("ss://", StringComparison.OrdinalIgnoreCase);

        if (!isHttp && !isNode)
        {
            MessageBox.Show(this, "Укажите URL подписки или ссылку VLESS, VMess, Trojan или Shadowsocks.", "Rayvia", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var subscription = new SubscriptionDefinition
        {
            Name = isHttp ? uri!.Host : "Импортированный сервер",
            Url = input
        };

        _settings.Subscriptions.Add(subscription);
        SubscriptionUrlText.Clear();

        try
        {
            await RefreshSubscriptionAsync(subscription);
            _log.Write($"Добавлено: {subscription.Name}.");
        }
        catch (Exception ex)
        {
            _settings.Subscriptions.Remove(subscription);
            _log.Write("Ошибка импорта: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Не удалось добавить", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        RefreshBindings();
        await SaveAsync();
    }

    private async void RefreshSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;

        var subscription = _settings.Subscriptions.FirstOrDefault(x => x.Id == id);
        if (subscription is null)
            return;

        try
        {
            await RefreshSubscriptionAsync(subscription);
            RefreshBindings();
            await SaveAsync();
            _log.Write($"Обновлено: {subscription.Name}.");
        }
        catch (Exception ex)
        {
            _log.Write("Ошибка обновления подписки: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Не удалось обновить", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshSubscriptionAsync(SubscriptionDefinition subscription)
    {
        var nodes = await _subscriptionService.RefreshAsync(subscription);
        if (nodes.Count == 0)
            throw new InvalidOperationException("Поддерживаемые серверы в подписке не найдены.");

        _settings.Nodes.RemoveAll(x => x.SourceSubscriptionId == subscription.Id);
        _settings.Nodes.AddRange(nodes);

        if (_settings.SelectedNodeId is null)
            _settings.SelectedNodeId = nodes[0].Id;
    }

    private async void DeleteSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;

        _settings.Subscriptions.RemoveAll(x => x.Id == id);
        _settings.Nodes.RemoveAll(x => x.SourceSubscriptionId == id);

        if (_settings.Nodes.All(x => x.Id != _settings.SelectedNodeId))
            _settings.SelectedNodeId = _settings.Nodes.FirstOrDefault()?.Id;

        RefreshBindings();
        await SaveAsync();
    }

    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var pattern = RulePatternText.Text.Trim();
        if (string.IsNullOrWhiteSpace(pattern))
            return;

        var action = (RuleActionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "proxy";
        _settings.Rules.Add(new RoutingRule { Pattern = pattern, Action = action });
        RulePatternText.Clear();

        RefreshBindings();
        await SaveAsync();
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;

        _settings.Rules.RemoveAll(x => x.Id == id);
        RefreshBindings();
        await SaveAsync();
    }

    private async void RoutingMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || sender is not RadioButton radio || radio.Tag is null)
            return;

        if (Enum.TryParse<RoutingMode>(radio.Tag.ToString(), out var mode))
        {
            _settings.RoutingMode = mode;
            HomeRoutingCombo.SelectedIndex = mode switch
            {
                RoutingMode.ProxyAll => 1,
                RoutingMode.DirectAll => 2,
                _ => 0
            };
            await SaveAsync();
        }
    }

    private async void HomeRoutingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || HomeRoutingCombo.SelectedItem is not ComboBoxItem item || item.Tag is null)
            return;

        if (Enum.TryParse<RoutingMode>(item.Tag.ToString(), out var mode) && mode != _settings.RoutingMode)
        {
            _settings.RoutingMode = mode;
            ApplyRoutingSelection();
            await SaveAsync();
        }
    }

    private async void ServerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || ServerCombo.SelectedItem is not ProxyNode node)
            return;

        _settings.SelectedNodeId = node.Id;
        await SaveAsync();
    }

    private void ApplyRoutingSelection()
    {
        SmartRadio.IsChecked = _settings.RoutingMode == RoutingMode.Smart;
        ProxyAllRadio.IsChecked = _settings.RoutingMode == RoutingMode.ProxyAll;
        DirectAllRadio.IsChecked = _settings.RoutingMode == RoutingMode.DirectAll;
    }

    private async void AutoUpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;

        _settings.AutoUpdate = AutoUpdateCheck.IsChecked == true;
        await SaveAsync();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        => await CheckForUpdatesAsync(true);

    private async Task CheckForUpdatesAsync(bool showNoUpdate)
    {
        try
        {
            _log.Write("Проверка обновлений…");
            var update = await _updateService.CheckAndDownloadAsync(CurrentVersion());

            if (update is null)
            {
                _log.Write("Установлена актуальная версия.");
                if (showNoUpdate)
                    MessageBox.Show(this, "Установлена актуальная версия.", "Rayvia", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _pendingUpdate = update;
            UpdateTitle.Text = $"Доступна Rayvia {update.Version}";
            UpdateBanner.Visibility = Visibility.Visible;
            _log.Write($"Обновление {update.Version} скачано.");
        }
        catch (Exception ex)
        {
            _log.Write("Не удалось проверить обновления: " + ex.Message);
            if (showNoUpdate)
                MessageBox.Show(this, ex.Message, "Проверка обновлений", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null)
            return;

        try
        {
            try { _systemProxy.Disable(); } catch { }
            _xray.Disconnect();
            UpdateService.StartInstaller(_pendingUpdate);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось запустить установщик", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static Version CurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowHome();
    private void Subscriptions_Click(object sender, RoutedEventArgs e) => ShowPage(SubscriptionsPage, "Подписки", "URL подписок и отдельные серверы");
    private void Routing_Click(object sender, RoutedEventArgs e) => ShowPage(RoutingPage, "Маршрутизация", "Режим и пользовательские правила");
    private void Logs_Click(object sender, RoutedEventArgs e) => ShowPage(LogsPage, "Журнал", "События клиента и Xray");
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage, "Настройки", "Обновления и локальные порты");

    private void ShowHome()
    {
        ShowPage(HomePage, "Главная", "Подключение и состояние клиента");
    }

    private void ShowPage(FrameworkElement page, string title, string subtitle)
    {
        foreach (var item in new FrameworkElement[] { HomePage, SubscriptionsPage, RoutingPage, LogsPage, SettingsPage })
            item.Visibility = item == page ? Visibility.Visible : Visibility.Collapsed;

        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
    }
}
