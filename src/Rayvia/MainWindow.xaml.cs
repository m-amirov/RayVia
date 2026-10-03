using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly RuntimeStateService _runtimeState = new();
    private readonly ObservableCollection<ConnectionEntry> _connections = [];

    private readonly bool _autoConnectOnStartup;
    private AppSettings _settings = new();
    private UpdateInfo? _pendingUpdate;
    private bool _loaded;
    private bool _refreshingUi;
    private bool _closing;
    private readonly ConnectionCoordinator _connectionCoordinator;

    public MainWindow(bool autoConnectOnStartup = false)
    {
        InitializeComponent();

        _autoConnectOnStartup = autoConnectOnStartup;
        _xray = new XrayCoreService(_log);
        _connectionCoordinator = new ConnectionCoordinator(_xray, _systemProxy, _runtimeState, _log);
        _connectionCoordinator.StateChanged += (_, state) => Dispatcher.Invoke(() => UpdateConnectionUi(state));

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
        try
        {
            _settings = await _settingsService.LoadAsync();
        }
        catch (SettingsRecoveryRequiredException ex)
        {
            _log.Write("Настройки требуют восстановления: " + ex.Message);
            var result = MessageBox.Show(
                this,
                ex.Message + "\n\nСоздать новые настройки?",
                "Восстановление настроек",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                Application.Current.Shutdown(1);
                return;
            }

            _settings = new AppSettings();
            await _settingsService.SaveAsync(_settings);
        }
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
        ShowActivityTab(true);
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

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing)
            return;

        e.Cancel = true;
        _closing = true;
        try { await _connectionCoordinator.DisconnectAsync(); } catch (Exception ex) { _log.Write("Ошибка shutdown: " + ex.Message); }
        _liveConnections.Dispose();
        _connectionCoordinator.Dispose();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Close));
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

            GroupMemberServerCombo.ItemsSource = null;
            GroupMemberServerCombo.ItemsSource = _settings.Nodes;

            var groupChoices = BuildGroupChoices();
            ServerGroupCombo.ItemsSource = groupChoices;

            var selectedGroup = groupChoices.FirstOrDefault(
                                    x => x.Id == (_settings.SelectedGroupId ?? ""))
                                ?? groupChoices[0];

            ServerGroupCombo.SelectedItem = selectedGroup;

            AutoSelectCheck.IsChecked = _settings.AutoSelectBestServer;
            AutoSelectServersCheck.IsChecked = _settings.AutoSelectBestServer;

            RefreshServerChoices();
            RefreshGroupMembers();

            SubscriptionCountText.Text = $"{_settings.Subscriptions.Count} источников";
            ServerCountText.Text = $"{_settings.Nodes.Count} серверов";
            HomePortText.Text = $"127.0.0.1:{_settings.HttpPort}";

            UpdateConnectionModeButtons();
            UpdateDashboardSummary();
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
        var selected = candidates.FirstOrDefault(x => x.Id == _settings.SelectedNodeId)
                       ?? candidates.FirstOrDefault();

        if (selected is not null)
            _settings.SelectedNodeId = selected.Id;
        else if (_settings.Nodes.Count == 0)
            _settings.SelectedNodeId = null;

        ServersList.ItemsSource = null;
        ServersList.ItemsSource = candidates;
        UpdateDashboardSummary();
    }

    private void RefreshGroupMembers()
    {
        if (string.IsNullOrWhiteSpace(_settings.SelectedGroupId))
        {
            GroupMembersTitle.Text = "Выберите группу";
            GroupMembersList.ItemsSource = Array.Empty<ProxyNode>();
            GroupMembersFooter.Visibility = Visibility.Collapsed;
            return;
        }

        var group = _settings.Groups.FirstOrDefault(x => x.Id == _settings.SelectedGroupId);
        if (group is null)
        {
            GroupMembersTitle.Text = "Выберите группу";
            GroupMembersList.ItemsSource = Array.Empty<ProxyNode>();
            GroupMembersFooter.Visibility = Visibility.Collapsed;
            return;
        }

        var ids = group.NodeIds.ToHashSet(StringComparer.Ordinal);
        GroupMembersTitle.Text = group.Name;
        GroupMembersList.ItemsSource = _settings.Nodes.Where(x => ids.Contains(x.Id)).ToList();
        GroupMembersFooter.Visibility = Visibility.Visible;
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
        if (_connectionCoordinator.State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            if (toggleDisconnect)
                await DisconnectAsync();
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
                if (Application.Current is not App app || !app.TryRestartElevatedAndShutdown(autoConnect: true))
                    throw new InvalidOperationException("Не удалось передать запуск повышенному Rayvia.");
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
                node = candidates.FirstOrDefault(x => x.Id == _settings.SelectedNodeId)
                       ?? candidates.FirstOrDefault();
            }

            if (node is null)
                throw new InvalidOperationException("Сервер не выбран.");

            _settings.SelectedNodeId = node.Id;
            await SaveAsync();

            ConnectButton.Content = "Подключение…";
            if (!await _connectionCoordinator.ConnectAsync(node, _settings))
                return;

            _liveConnections.Start(_xray.AccessLogPath);
            UpdateConnectionUi(_connectionCoordinator.State);
        }
        catch (Exception ex)
        {
            _liveConnections.Stop();
            try { await _connectionCoordinator.DisconnectAsync(); } catch { }
            UpdateConnectionUi(_connectionCoordinator.State);
            _log.Write("Ошибка подключения: " + ex);

            MessageBox.Show(
                this,
                GetFriendlyConnectionError(ex),
                "Не удалось подключиться",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = _connectionCoordinator.State == ConnectionState.Connected ? "Отключить" : "Подключить";
        }
    }

    private async Task DisconnectAsync()
    {
        _liveConnections.Stop();
        await _connectionCoordinator.DisconnectAsync();
        UpdateConnectionUi(_connectionCoordinator.State);
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

        UpdateConnectionModeButtons();
        UpdateDashboardSummary();
    }

    private void UpdateConnectionUi(ConnectionState state)
    {
        if (state == ConnectionState.Connected)
        {
            UpdateConnectionUi(true);
            return;
        }

        UpdateConnectionUi(false);
        if (state == ConnectionState.Connecting) StatusText.Text = "Подключение…";
        else if (state == ConnectionState.Stopping) StatusText.Text = "Отключение…";
        else if (state == ConnectionState.Failed) StatusText.Text = "Ошибка";
    }

    private void UpdateDashboardSummary()
    {
        if (_connectionCoordinator.State == ConnectionState.Connected && _connectionCoordinator.ActiveNode is ActiveNodeSnapshot connected)
        {
            ActiveServerText.Text = connected.Name;
            var security = string.IsNullOrWhiteSpace(connected.Security) || connected.Security == "none"
                ? ""
                : $" · {connected.Security.ToUpperInvariant()}";
            ActiveServerMetaText.Text =
                $"{connected.Protocol.ToUpperInvariant()}{security} · {connected.Endpoint} · {connected.LatencyDisplay}";
        }
        else
        {
            var selected = _settings.Nodes.FirstOrDefault(x => x.Id == _settings.SelectedNodeId);
            if (selected is null)
            {
                ActiveServerText.Text = "Сервер не выбран";
                ActiveServerMetaText.Text = _settings.Subscriptions.Count == 0
                    ? "Добавьте профиль, чтобы начать"
                    : "Выберите сервер в разделе «Профили»";
            }
            else
            {
                ActiveServerText.Text = selected.Name;
                var security = string.IsNullOrWhiteSpace(selected.Security) || selected.Security == "none"
                    ? ""
                    : $" · {selected.Security.ToUpperInvariant()}";
                ActiveServerMetaText.Text =
                    $"{selected.Protocol.ToUpperInvariant()}{security} · {selected.Endpoint} · {selected.LatencyDisplay}";
            }
        }

        var group = string.IsNullOrWhiteSpace(_settings.SelectedGroupId)
            ? null
            : _settings.Groups.FirstOrDefault(x => x.Id == _settings.SelectedGroupId);
        DashboardGroupText.Text = group?.Name ?? "Все серверы";

        DashboardRoutingText.Text = _settings.RoutingMode switch
        {
            RoutingMode.ProxyAll => "Всё через прокси",
            RoutingMode.DirectAll => "Всё напрямую",
            _ => "Умная"
        };
    }

    private void UpdateConnectionModeButtons()
    {
        SetSegmentState(ModeSystemProxyButton, _settings.ConnectionMode == ConnectionMode.SystemProxy);
        SetSegmentState(ModeTunButton, _settings.ConnectionMode == ConnectionMode.Tun);
    }

    private void SetSegmentState(Button button, bool active)
    {
        button.Background = (Brush)FindResource(active ? "AccentSoftBrush" : "SurfaceBrush");
        button.Foreground = (Brush)FindResource(active ? "AccentBrush" : "TextBrush");
        button.BorderBrush = (Brush)FindResource(active ? "AccentBrush" : "BorderBrush");
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
            AddProfilePanel.Visibility = Visibility.Collapsed;
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
        await _connectionCoordinator.RunExclusiveAsync(async () =>
        {
            var nodes = await _subscriptionService.RefreshAsync(subscription);
            if (nodes.Count == 0)
                throw new InvalidOperationException("Поддерживаемые серверы в подписке не найдены.");
            SubscriptionNodeReconciler.ApplyRefresh(_settings, subscription.Id, nodes);
            CleanGroupMembership();

            if (_settings.SelectedNodeId is null || _settings.Nodes.All(x => x.Id != _settings.SelectedNodeId))
                _settings.SelectedNodeId = nodes[0].Id;
        });
    }

    private async void DeleteSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;

        _settings.Subscriptions.RemoveAll(x => x.Id == id);
        SubscriptionNodeReconciler.RemoveSubscription(_settings, id);

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
        UpdateDashboardSummary();
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

    private async void ConnectionModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || sender is not Button { Tag: string tag } ||
            !Enum.TryParse<ConnectionMode>(tag, out var mode) ||
            mode == _settings.ConnectionMode)
            return;

        if (_connectionCoordinator.State != ConnectionState.Disconnected)
            await DisconnectAsync();

        _settings.ConnectionMode = mode;
        UpdateConnectionUi(false);
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
            UpdateDashboardSummary();
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

        if (_connectionCoordinator.State != ConnectionState.Disconnected)
            await DisconnectAsync();

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
            UpdateInfo? update = null;
            await _connectionCoordinator.RunExclusiveAsync(async () =>
            {
                update = await _updateService.CheckAndDownloadAsync(CurrentVersion());
            });

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

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null)
            return;

        try
        {
            _liveConnections.Stop();
            await DisconnectAsync();
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
                      ?? new Version(0, 3, 1);

        return new Version(
            version.Major,
            version.Minor,
            Math.Max(0, version.Build));
    }

    private void Home_Click(object sender, RoutedEventArgs e)
        => ShowHome();

    private void Profiles_Click(object sender, RoutedEventArgs e)
        => ShowProfiles();

    private void Routing_Click(object sender, RoutedEventArgs e)
        => ShowPage(RoutingPage, "Маршрутизация", "Правила и проверка маршрута");

    private void Activity_Click(object sender, RoutedEventArgs e)
        => ShowPage(ActivityPage, "Активность", "Соединения и журнал Xray");

    private void Settings_Click(object sender, RoutedEventArgs e)
        => ShowPage(SettingsPage, "Настройки", "Обновления и локальные порты");

    private void OpenProfiles_Click(object sender, RoutedEventArgs e)
        => ShowProfiles();

    private void OpenRouting_Click(object sender, RoutedEventArgs e)
        => ShowPage(RoutingPage, "Маршрутизация", "Правила и проверка маршрута");

    private void ShowHome()
        => ShowPage(HomePage, "Главная", "Подключение");

    private void ShowProfiles()
        => ShowPage(ProfilesPage, "Профили", "Источники, серверы и группы");

    private void ShowPage(FrameworkElement page, string title, string subtitle)
    {
        foreach (var item in new FrameworkElement[]
                 {
                     HomePage,
                     ProfilesPage,
                     RoutingPage,
                     ActivityPage,
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
            NavProfilesButton,
            NavRoutingButton,
            NavActivityButton,
            NavSettingsButton
        };

        foreach (var button in buttons)
        {
            button.Background = Brushes.Transparent;
            button.Foreground = (Brush)FindResource("SidebarMutedBrush");
        }

        var active = NavHomeButton;

        if (page == ProfilesPage)
            active = NavProfilesButton;
        else if (page == RoutingPage)
            active = NavRoutingButton;
        else if (page == ActivityPage)
            active = NavActivityButton;
        else if (page == SettingsPage)
            active = NavSettingsButton;

        active.Background = (Brush)FindResource("SidebarSelectedBrush");
        active.Foreground = (Brush)FindResource("SidebarTextBrush");
    }

    private void ToggleAddProfile_Click(object sender, RoutedEventArgs e)
    {
        GroupToolsPanel.Visibility = Visibility.Collapsed;
        AddProfilePanel.Visibility = AddProfilePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (AddProfilePanel.Visibility == Visibility.Visible)
        {
            SubscriptionUrlText.Focus();
            SubscriptionUrlText.SelectAll();
        }
    }

    private void CancelAddProfile_Click(object sender, RoutedEventArgs e)
    {
        AddProfilePanel.Visibility = Visibility.Collapsed;
        SubscriptionUrlText.Clear();
    }

    private void ToggleGroupTools_Click(object sender, RoutedEventArgs e)
    {
        AddProfilePanel.Visibility = Visibility.Collapsed;
        GroupToolsPanel.Visibility = GroupToolsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ActivityConnections_Click(object sender, RoutedEventArgs e)
        => ShowActivityTab(true);

    private void ActivityLogs_Click(object sender, RoutedEventArgs e)
        => ShowActivityTab(false);

    private void ShowActivityTab(bool connections)
    {
        ConnectionsPanel.Visibility = connections ? Visibility.Visible : Visibility.Collapsed;
        LogsPanel.Visibility = connections ? Visibility.Collapsed : Visibility.Visible;
        SetSegmentState(ActivityConnectionsButton, connections);
        SetSegmentState(ActivityLogsButton, !connections);
    }

    private static string GetFriendlyConnectionError(Exception exception)
    {
        var message = exception.Message;

        if (message.Contains("Сервер не выбран", StringComparison.OrdinalIgnoreCase))
            return message;

        if (message.Contains("права администратора", StringComparison.OrdinalIgnoreCase))
            return message;

        if (message.Contains("Xray core", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("скач", StringComparison.OrdinalIgnoreCase))
            return message;

        if (message.Contains("конфигурац", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("infra/conf", StringComparison.OrdinalIgnoreCase))
            return message;

        return "Не удалось установить соединение. Подробности записаны в «Активность → Журнал».";
    }
}
