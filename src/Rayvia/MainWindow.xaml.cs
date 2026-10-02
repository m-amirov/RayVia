using System.Collections.ObjectModel;
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
    private readonly LatencyService _latency = new();
    private readonly RoutingInspectorService _inspector = new();
    private readonly LiveConnectionsService _liveConnections = new();
    private readonly ObservableCollection<ConnectionEntry> _connections = [];

    private readonly bool _autoConnectOnStartup;
    private AppSettings _settings = new();
    private UpdateInfo? _pendingUpdate;
    private bool _loaded;
    private bool _refreshingUi;

    public MainWindow(bool autoConnectOnStartup = false)
    {
        InitializeComponent();

        _autoConnectOnStartup = autoConnectOnStartup;
        _xray = new XrayCoreService(_log);

        ConnectionsList.ItemsSource = _connections;

        _log.EntryAdded += line =>
        {
            Dispatcher.Invoke(() =>
            {
                LogList.Items.Add(line);
                if (LogList.Items.Count > 600)
                    LogList.Items.RemoveAt(0);
                LogList.ScrollIntoView(line);
            });
        };

        _liveConnections.EntryAdded += entry =>
        {
            Dispatcher.Invoke(() =>
            {
                _connections.Insert(0, entry);
                while (_connections.Count > 400)
                    _connections.RemoveAt(_connections.Count - 1);
            });
        };

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsService.LoadAsync();
        CleanGroupMembership();
        _loaded = true;

        VersionText.Text = "v" + CurrentVersion().ToString(3);
        AutoUpdateCheck.IsChecked = _settings.AutoUpdate;
        SocksPortText.Text = _settings.SocksPort.ToString();
        HttpPortText.Text = _settings.HttpPort.ToString();
        HomePortText.Text = $"127.0.0.1:{_settings.HttpPort}";

        RefreshBindings();
        ApplyRoutingSelection();
        ShowHome();
        UpdateConnectionUi(false);

        _log.Write("Rayvia запущен.");

        if (_settings.AutoUpdate)
            _ = CheckForUpdatesAsync(false);

        if (_autoConnectOnStartup)
        {
            await Task.Delay(300);
            await ConnectAsync(false);
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try { _systemProxy.Disable(); } catch { }
        _liveConnections.Dispose();
        _xray.Dispose();
    }

    private void CleanGroupMembership()
    {
        var validIds = _settings.Nodes.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var group in _settings.Groups)
            group.NodeIds.RemoveAll(id => !validIds.Contains(id));

        if (_settings.SelectedGroupId is not null &&
            _settings.Groups.All(x => x.Id != _settings.SelectedGroupId))
            _settings.SelectedGroupId = null;
    }

    private void RefreshBindings()
    {
        _refreshingUi = true;
        try
        {
            SubscriptionsList.ItemsSource = null;
            SubscriptionsList.ItemsSource = _settings.Subscriptions;

            RulesList.ItemsSource = null;
            RulesList.ItemsSource = _settings.Rules;

            ServersList.ItemsSource = null;
            ServersList.ItemsSource = _settings.Nodes;

            GroupMemberServerCombo.ItemsSource = null;
            GroupMemberServerCombo.ItemsSource = _settings.Nodes;

            var groupChoices = BuildGroupChoices();

            HomeGroupCombo.ItemsSource = groupChoices;
            ServerGroupCombo.ItemsSource = groupChoices;

            var selectedGroup = groupChoices.FirstOrDefault(
                                    x => x.Id == (_settings.SelectedGroupId ?? ""))
                                ?? groupChoices[0];

            HomeGroupCombo.SelectedItem = selectedGroup;
            ServerGroupCombo.SelectedItem = selectedGroup;

            ConnectionModeCombo.SelectedIndex =
                _settings.ConnectionMode == ConnectionMode.Tun ? 1 : 0;

            AutoSelectCheck.IsChecked = _settings.AutoSelectBestServer;
            AutoSelectServersCheck.IsChecked = _settings.AutoSelectBestServer;

            RefreshServerChoices();
            RefreshGroupMembers();

            HomePortText.Text = $"127.0.0.1:{_settings.HttpPort}";
        }
        finally
        {
            _refreshingUi = false;
        }
    }

    private List<ServerGroup> BuildGroupChoices()
    {
        var all = new ServerGroup
        {
            Id = "",
            Name = "Все серверы",
            NodeIds = _settings.Nodes.Select(x => x.Id).ToList()
        };

        return [all, .. _settings.Groups];
    }

    private List<ProxyNode> GetCandidateNodes()
    {
        if (string.IsNullOrWhiteSpace(_settings.SelectedGroupId))
            return _settings.Nodes.ToList();

        var group = _settings.Groups.FirstOrDefault(x => x.Id == _settings.SelectedGroupId);
        if (group is null)
            return _settings.Nodes.ToList();

        var ids = group.NodeIds.ToHashSet(StringComparer.Ordinal);
        return _settings.Nodes.Where(x => ids.Contains(x.Id)).ToList();
    }

    private void RefreshServerChoices()
    {
        var candidates = GetCandidateNodes();

        ServerCombo.ItemsSource = null;
        ServerCombo.ItemsSource = candidates;

        var selected = candidates.FirstOrDefault(x => x.Id == _settings.SelectedNodeId)
                       ?? candidates.FirstOrDefault();

        ServerCombo.SelectedItem = selected;

        if (selected is not null && candidates.All(x => x.Id != _settings.SelectedNodeId))
            _settings.SelectedNodeId = selected.Id;
    }

    private void RefreshGroupMembers()
    {
        if (string.IsNullOrWhiteSpace(_settings.SelectedGroupId))
        {
            GroupMembersTitle.Text = "Все серверы";
            GroupMembersList.ItemsSource = _settings.Nodes;
            return;
        }

        var group = _settings.Groups.FirstOrDefault(x => x.Id == _settings.SelectedGroupId);
        if (group is null)
        {
            GroupMembersTitle.Text = "Состав группы";
            GroupMembersList.ItemsSource = Array.Empty<ProxyNode>();
            return;
        }

        var ids = group.NodeIds.ToHashSet(StringComparer.Ordinal);
        GroupMembersTitle.Text = group.Name;
        GroupMembersList.ItemsSource = _settings.Nodes.Where(x => ids.Contains(x.Id)).ToList();
    }

    private async Task SaveAsync()
    {
        if (_loaded)
            await _settingsService.SaveAsync(_settings);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
        => await ConnectAsync(true);

    private async Task ConnectAsync(bool toggleDisconnect)
    {
        if (_xray.IsRunning)
        {
            if (toggleDisconnect)
                Disconnect();
            return;
        }

        var candidates = GetCandidateNodes();
        if (candidates.Count == 0)
        {
            MessageBox.Show(
                this,
                "В выбранной группе нет серверов.",
                "Rayvia",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_settings.ConnectionMode == ConnectionMode.Tun &&
            !ElevationService.IsAdministrator())
        {
            try
            {
                await SaveAsync();
                _log.Write("Для TUN запрашиваются права администратора.");
                ElevationService.RestartElevated(true);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                _log.Write("TUN: " + ex.Message);
                MessageBox.Show(
                    this,
                    "TUN требует права администратора.",
                    "Rayvia",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return;
        }

        ProxyNode? node;

        try
        {
            ConnectButton.IsEnabled = false;
            ConnectButton.Content = _settings.AutoSelectBestServer
                ? "Проверка серверов…"
                : "Подключение…";

            if (_settings.AutoSelectBestServer)
            {
                await _latency.MeasureAllAsync(candidates);
                node = candidates
                           .Where(x => x.LatencyMs is not null)
                           .OrderBy(x => x.LatencyMs)
                           .FirstOrDefault()
                       ?? candidates.FirstOrDefault(x => x.Id == _settings.SelectedNodeId)
                       ?? candidates.FirstOrDefault();

                RefreshBindings();

                if (node is not null)
                    _log.Write($"Автовыбор: {node.Name} · {node.LatencyDisplay}.");
            }
            else
            {
                node = ServerCombo.SelectedItem as ProxyNode
                       ?? candidates.FirstOrDefault(x => x.Id == _settings.SelectedNodeId)
                       ?? candidates.FirstOrDefault();
            }

            if (node is null)
                throw new InvalidOperationException("Сервер не выбран.");

            _settings.SelectedNodeId = node.Id;
            await SaveAsync();

            ConnectButton.Content = "Подключение…";
            await _xray.ConnectAsync(node, _settings);

            if (_settings.ConnectionMode == ConnectionMode.SystemProxy)
                _systemProxy.Enable(_settings.HttpPort);
            else
                _systemProxy.Disable();

            _liveConnections.Start(_xray.AccessLogPath);
            UpdateConnectionUi(true);
        }
        catch (Exception ex)
        {
            try { _systemProxy.Disable(); } catch { }
            _liveConnections.Stop();
            _xray.Disconnect();
            UpdateConnectionUi(false);
            _log.Write("Ошибка подключения: " + ex.Message);

            MessageBox.Show(
                this,
                ex.Message,
                "Не удалось подключиться",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
        _liveConnections.Stop();
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

        HomeModeText.Text = _settings.ConnectionMode == ConnectionMode.Tun
            ? "TUN"
            : "System Proxy";

        HomeProxyText.Text = connected && _settings.ConnectionMode == ConnectionMode.SystemProxy
            ? "Включён"
            : _settings.ConnectionMode == ConnectionMode.Tun
                ? "Не используется"
                : "Выключен";

        var active = _settings.Nodes.FirstOrDefault(x => x.Id == _settings.SelectedNodeId);
        ActiveServerText.Text = active is null
            ? "Сервер не выбран"
            : $"{active.Name} · {active.Endpoint}";
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
            MessageBox.Show(
                this,
                "Укажите URL подписки или ссылку VLESS, VMess, Trojan или Shadowsocks.",
                "Rayvia",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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

            MessageBox.Show(
                this,
                ex.Message,
                "Не удалось добавить",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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

            MessageBox.Show(
                this,
                ex.Message,
                "Не удалось обновить",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task RefreshSubscriptionAsync(SubscriptionDefinition subscription)
    {
        var oldNodes = _settings.Nodes
            .Where(x => x.SourceSubscriptionId == subscription.Id)
            .ToDictionary(x => x.Id, StringComparer.Ordinal);

        var nodes = await _subscriptionService.RefreshAsync(subscription);
        if (nodes.Count == 0)
            throw new InvalidOperationException("Поддерживаемые серверы в подписке не найдены.");

        foreach (var node in nodes)
        {
            if (!oldNodes.TryGetValue(node.Id, out var old))
                continue;

            node.LatencyMs = old.LatencyMs;
            node.LatencyCheckedAt = old.LatencyCheckedAt;
        }

        _settings.Nodes.RemoveAll(x => x.SourceSubscriptionId == subscription.Id);
        _settings.Nodes.AddRange(nodes);

        CleanGroupMembership();

        if (_settings.SelectedNodeId is null ||
            _settings.Nodes.All(x => x.Id != _settings.SelectedNodeId))
            _settings.SelectedNodeId = nodes[0].Id;
    }

    private async void DeleteSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;

        var removedIds = _settings.Nodes
            .Where(x => x.SourceSubscriptionId == id)
            .Select(x => x.Id)
            .ToHashSet(StringComparer.Ordinal);

        _settings.Subscriptions.RemoveAll(x => x.Id == id);
        _settings.Nodes.RemoveAll(x => x.SourceSubscriptionId == id);

        foreach (var group in _settings.Groups)
            group.NodeIds.RemoveAll(removedIds.Contains);

        if (_settings.Nodes.All(x => x.Id != _settings.SelectedNodeId))
            _settings.SelectedNodeId = _settings.Nodes.FirstOrDefault()?.Id;

        RefreshBindings();
        await SaveAsync();
    }

    private async void PingAll_Click(object sender, RoutedEventArgs e)
    {
        var candidates = GetCandidateNodes();
        if (candidates.Count == 0)
            return;

        PingAllButton.IsEnabled = false;
        PingHomeButton.IsEnabled = false;

        try
        {
            _log.Write($"Проверка задержки: {candidates.Count} серверов.");
            await _latency.MeasureAllAsync(candidates);
            RefreshBindings();
            await SaveAsync();

            var available = candidates.Count(x => x.LatencyMs is not null);
            _log.Write($"Проверка завершена: отвечают {available} из {candidates.Count}.");
        }
        finally
        {
            PingAllButton.IsEnabled = true;
            PingHomeButton.IsEnabled = true;
        }
    }

    private async void SelectServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;

        var node = _settings.Nodes.FirstOrDefault(x => x.Id == id);
        if (node is null)
            return;

        _settings.SelectedNodeId = node.Id;
        RefreshBindings();
        await SaveAsync();
    }

    private async void CreateGroup_Click(object sender, RoutedEventArgs e)
    {
        var name = NewGroupText.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (_settings.Groups.Any(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                this,
                "Группа с таким названием уже существует.",
                "Rayvia",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var group = new ServerGroup { Name = name };
        _settings.Groups.Add(group);
        _settings.SelectedGroupId = group.Id;
        NewGroupText.Clear();

        RefreshBindings();
        await SaveAsync();
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_settings.SelectedGroupId))
            return;

        _settings.Groups.RemoveAll(x => x.Id == _settings.SelectedGroupId);
        _settings.SelectedGroupId = null;

        RefreshBindings();
        await SaveAsync();
    }

    private async void AddServerToGroup_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_settings.SelectedGroupId) ||
            GroupMemberServerCombo.SelectedItem is not ProxyNode node)
            return;

        var group = _settings.Groups.FirstOrDefault(x => x.Id == _settings.SelectedGroupId);
        if (group is null)
            return;

        if (!group.NodeIds.Contains(node.Id, StringComparer.Ordinal))
            group.NodeIds.Add(node.Id);

        RefreshBindings();
        await SaveAsync();
    }

    private async void RemoveServerFromGroup_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_settings.SelectedGroupId) ||
            sender is not Button { Tag: string nodeId })
            return;

        var group = _settings.Groups.FirstOrDefault(x => x.Id == _settings.SelectedGroupId);
        if (group is null)
            return;

        group.NodeIds.RemoveAll(x => x == nodeId);

        if (_settings.SelectedNodeId == nodeId && GetCandidateNodes().All(x => x.Id != nodeId))
            _settings.SelectedNodeId = GetCandidateNodes().FirstOrDefault()?.Id;

        RefreshBindings();
        await SaveAsync();
    }

    private async void HomeGroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _refreshingUi || HomeGroupCombo.SelectedItem is not ServerGroup group)
            return;

        await SelectGroupAsync(group.Id);
    }

    private async void ServerGroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _refreshingUi || ServerGroupCombo.SelectedItem is not ServerGroup group)
            return;

        await SelectGroupAsync(group.Id);
    }

    private async Task SelectGroupAsync(string id)
    {
        _settings.SelectedGroupId = string.IsNullOrWhiteSpace(id) ? null : id;
        RefreshBindings();
        await SaveAsync();
    }

    private async void AutoSelectCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _refreshingUi)
            return;

        var enabled = sender switch
        {
            CheckBox checkBox => checkBox.IsChecked == true,
            _ => false
        };

        _settings.AutoSelectBestServer = enabled;

        _refreshingUi = true;
        AutoSelectCheck.IsChecked = enabled;
        AutoSelectServersCheck.IsChecked = enabled;
        _refreshingUi = false;

        await SaveAsync();
    }

    private async void ConnectionModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _refreshingUi ||
            ConnectionModeCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is null)
            return;

        if (!Enum.TryParse<ConnectionMode>(item.Tag.ToString(), out var mode) ||
            mode == _settings.ConnectionMode)
            return;

        if (_xray.IsRunning)
            Disconnect();

        _settings.ConnectionMode = mode;
        UpdateConnectionUi(false);
        await SaveAsync();
    }

    private async void ServerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _refreshingUi || ServerCombo.SelectedItem is not ProxyNode node)
            return;

        _settings.SelectedNodeId = node.Id;
        UpdateConnectionUi(_xray.IsRunning);
        await SaveAsync();
    }

    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var pattern = RulePatternText.Text.Trim();
        if (string.IsNullOrWhiteSpace(pattern))
            return;

        var action = (RuleActionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "proxy";

        _settings.Rules.Add(new RoutingRule
        {
            Pattern = pattern,
            Action = action
        });

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
        if (!_loaded || _refreshingUi ||
            sender is not RadioButton radio ||
            radio.Tag is null)
            return;

        if (Enum.TryParse<RoutingMode>(radio.Tag.ToString(), out var mode))
        {
            _settings.RoutingMode = mode;
            await SaveAsync();
        }
    }

    private void ApplyRoutingSelection()
    {
        _refreshingUi = true;
        SmartRadio.IsChecked = _settings.RoutingMode == RoutingMode.Smart;
        ProxyAllRadio.IsChecked = _settings.RoutingMode == RoutingMode.ProxyAll;
        DirectAllRadio.IsChecked = _settings.RoutingMode == RoutingMode.DirectAll;
        _refreshingUi = false;
    }

    private async void InspectRoute_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _inspector.InspectAsync(
                InspectorTargetText.Text,
                _settings);

            InspectorRouteText.Text = result.Route;
            InspectorRuleText.Text = result.Rule;
            InspectorResolvedText.Text = result.Resolved;
            InspectorNoteText.Text = result.Note;
        }
        catch (Exception ex)
        {
            InspectorRouteText.Text = "—";
            InspectorRuleText.Text = "—";
            InspectorResolvedText.Text = "—";
            InspectorNoteText.Text = ex.Message;
        }
    }

    private void ClearConnections_Click(object sender, RoutedEventArgs e)
        => _connections.Clear();

    private async void AutoUpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _refreshingUi)
            return;

        _settings.AutoUpdate = AutoUpdateCheck.IsChecked == true;
        await SaveAsync();
    }

    private async void SavePorts_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SocksPortText.Text, out var socks) ||
            !int.TryParse(HttpPortText.Text, out var http) ||
            socks is < 1 or > 65535 ||
            http is < 1 or > 65535 ||
            socks == http)
        {
            MessageBox.Show(
                this,
                "Укажите разные порты от 1 до 65535.",
                "Rayvia",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_xray.IsRunning)
            Disconnect();

        _settings.SocksPort = socks;
        _settings.HttpPort = http;
        HomePortText.Text = $"127.0.0.1:{http}";

        await SaveAsync();
        _log.Write("Локальные порты сохранены.");
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
                {
                    MessageBox.Show(
                        this,
                        "Установлена актуальная версия.",
                        "Rayvia",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

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
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "Проверка обновлений",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null)
            return;

        try
        {
            try { _systemProxy.Disable(); } catch { }
            _liveConnections.Stop();
            _xray.Disconnect();
            UpdateService.StartInstaller(_pendingUpdate);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Не удалось запустить установщик",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static Version CurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version
                      ?? new Version(0, 2, 1);

        return new Version(
            version.Major,
            version.Minor,
            Math.Max(0, version.Build));
    }

    private void Home_Click(object sender, RoutedEventArgs e)
        => ShowHome();

    private void Subscriptions_Click(object sender, RoutedEventArgs e)
        => ShowPage(SubscriptionsPage, "Подписки", "Источники серверов");

    private void Servers_Click(object sender, RoutedEventArgs e)
        => ShowPage(ServersPage, "Серверы", "Задержка, группы и автовыбор");

    private void Routing_Click(object sender, RoutedEventArgs e)
        => ShowPage(RoutingPage, "Маршрутизация", "Правила и проверка маршрута");

    private void Connections_Click(object sender, RoutedEventArgs e)
        => ShowPage(ConnectionsPage, "Соединения", "Трафик, который видит Xray");

    private void Logs_Click(object sender, RoutedEventArgs e)
        => ShowPage(LogsPage, "Журнал", "События Rayvia и Xray");

    private void Settings_Click(object sender, RoutedEventArgs e)
        => ShowPage(SettingsPage, "Настройки", "Обновления и локальные порты");

    private void ShowHome()
        => ShowPage(HomePage, "Главная", "Подключение и состояние");

    private void ShowPage(FrameworkElement page, string title, string subtitle)
    {
        foreach (var item in new FrameworkElement[]
                 {
                     HomePage,
                     SubscriptionsPage,
                     ServersPage,
                     RoutingPage,
                     ConnectionsPage,
                     LogsPage,
                     SettingsPage
                 })
        {
            item.Visibility = item == page
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
        SetActiveNavigation(page);
    }

    private void SetActiveNavigation(FrameworkElement page)
    {
        var buttons = new[]
        {
            NavHomeButton,
            NavSubscriptionsButton,
            NavServersButton,
            NavRoutingButton,
            NavConnectionsButton,
            NavLogsButton,
            NavSettingsButton
        };

        foreach (var button in buttons)
        {
            button.Background = Brushes.Transparent;
            button.Foreground = (Brush)FindResource("SidebarMutedBrush");
        }

        var active = NavHomeButton;

        if (page == SubscriptionsPage)
            active = NavSubscriptionsButton;
        else if (page == ServersPage)
            active = NavServersButton;
        else if (page == RoutingPage)
            active = NavRoutingButton;
        else if (page == ConnectionsPage)
            active = NavConnectionsButton;
        else if (page == LogsPage)
            active = NavLogsButton;
        else if (page == SettingsPage)
            active = NavSettingsButton;

        active.Background = (Brush)FindResource("SidebarSelectedBrush");
        active.Foreground = (Brush)FindResource("SidebarTextBrush");
    }
}
