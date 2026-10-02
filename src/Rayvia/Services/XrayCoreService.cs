using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Rayvia.Models;

namespace Rayvia.Services;

public sealed class XrayCoreService : IDisposable
{
    private const string LatestXrayApi = "https://api.github.com/repos/XTLS/Xray-core/releases/latest";
    private const string XrayAssetName = "Xray-windows-64.zip";

    private readonly HttpClient _http = new();
    private readonly LogService _log;
    private Process? _process;

    private readonly string _coreDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Rayvia",
        "Core");

    private readonly string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Rayvia",
        "Logs");

    public bool IsRunning => _process is { HasExited: false };
    public string AccessLogPath => Path.Combine(_logDirectory, "access.log");
    public string ErrorLogPath => Path.Combine(_logDirectory, "xray-error.log");

    public XrayCoreService(LogService log)
    {
        _log = log;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Rayvia/0.3");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task ConnectAsync(ProxyNode node, AppSettings settings)
    {
        await EnsureCoreAsync(settings.ConnectionMode == ConnectionMode.Tun);

        Disconnect();

        Directory.CreateDirectory(_coreDirectory);
        Directory.CreateDirectory(_logDirectory);

        TryDelete(AccessLogPath);
        TryDelete(ErrorLogPath);

        var configPath = Path.Combine(_coreDirectory, "rayvia-config.json");
        var executable = Path.Combine(_coreDirectory, "xray.exe");

        var includeCommunityRuList = settings.RoutingMode == RoutingMode.Smart;
        var includeRuGeoIp = settings.RoutingMode == RoutingMode.Smart;

        ConfigValidationResult validation;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var config = BuildConfig(
                node,
                settings,
                AccessLogPath,
                ErrorLogPath,
                includeCommunityRuList,
                includeRuGeoIp);

            await File.WriteAllTextAsync(configPath, config);
            validation = await ValidateConfigAsync(executable, configPath);

            if (validation.Success)
                goto ConfigValidated;

            if (includeCommunityRuList && IsGeositeLoadError(validation.Detail))
            {
                includeCommunityRuList = false;
                _log.Write("Smart Routing: geosite:category-ru недоступен, используется встроенный доменный fallback.");
                continue;
            }

            if (includeRuGeoIp && IsGeoIpLoadError(validation.Detail))
            {
                includeRuGeoIp = false;
                _log.Write("Smart Routing: geoip:ru недоступен, маршрут продолжит работать по доменным правилам.");
                continue;
            }

            _log.Write("Xray config validation: " + validation.Detail);
            throw new InvalidOperationException(
                "Xray не принял конфигурацию. Подробности записаны в «Активность → Журнал».");
        }

        throw new InvalidOperationException(
            "Не удалось подготовить маршрутизацию Xray. Подробности записаны в «Активность → Журнал».");

ConfigValidated:

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"run -c \"{configPath}\"",
                WorkingDirectory = _coreDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };

        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _log.Write("Xray: " + e.Data);
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _log.Write("Xray: " + e.Data);
        };

        _process.Exited += (_, _) => _log.Write("Xray остановлен.");

        if (!_process.Start())
            throw new InvalidOperationException("Не удалось запустить Xray.");

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await Task.Delay(settings.ConnectionMode == ConnectionMode.Tun ? 1200 : 500);

        if (_process.HasExited)
        {
            var error = ReadLastError();
            if (!string.IsNullOrWhiteSpace(error))
                _log.Write("Xray startup error: " + error);

            throw new InvalidOperationException(
                "Xray завершился при запуске. Подробности записаны в «Активность → Журнал».");
        }

        _log.Write(
            $"Подключение: {node.Name} · {node.Protocol.ToUpperInvariant()} · " +
            $"{(settings.ConnectionMode == ConnectionMode.Tun ? "TUN" : "System Proxy")}.");
    }

    public void Disconnect()
    {
        if (_process is null)
            return;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                _process.WaitForExit(3000);
            }
        }
        catch
        {
            // Best-effort shutdown. Wintun and routes are released with the process.
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private async Task EnsureCoreAsync(bool requireWintun)
    {
        Directory.CreateDirectory(_coreDirectory);

        var executable = Path.Combine(_coreDirectory, "xray.exe");
        var wintun = Path.Combine(_coreDirectory, "wintun.dll");

        if (File.Exists(executable) && (!requireWintun || File.Exists(wintun)))
            return;

        _log.Write("Скачивание Xray core…");

        using var releaseResponse = await _http.GetAsync(LatestXrayApi);
        releaseResponse.EnsureSuccessStatusCode();

        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(releaseStream);

        string? downloadUrl = null;
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(
                    asset.GetProperty("name").GetString(),
                    XrayAssetName,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            downloadUrl = asset.GetProperty("browser_download_url").GetString();
            break;
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("В релизе Xray не найден Xray-windows-64.zip.");

        var zipPath = Path.Combine(Path.GetTempPath(), $"rayvia-xray-{Guid.NewGuid():N}.zip");
        var extractDirectory = Path.Combine(Path.GetTempPath(), $"rayvia-xray-{Guid.NewGuid():N}");

        try
        {
            using (var download = await _http.GetAsync(
                       downloadUrl,
                       HttpCompletionOption.ResponseHeadersRead))
            {
                download.EnsureSuccessStatusCode();

                await using var input = await download.Content.ReadAsStreamAsync();
                await using var output = File.Create(zipPath);
                await input.CopyToAsync(output);
            }

            ZipFile.ExtractToDirectory(zipPath, extractDirectory, true);

            foreach (var fileName in new[]
                     {
                         "xray.exe",
                         "geoip.dat",
                         "geosite.dat",
                         "wintun.dll",
                         "LICENSE-Wintun"
                     })
            {
                var source = Directory
                    .EnumerateFiles(extractDirectory, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (source is not null)
                    File.Copy(source, Path.Combine(_coreDirectory, fileName), true);
            }

            if (!File.Exists(executable))
                throw new InvalidOperationException("Архив Xray не содержит xray.exe.");

            if (requireWintun && !File.Exists(wintun))
                throw new InvalidOperationException("Архив Xray не содержит wintun.dll, необходимый для TUN.");

            _log.Write("Xray core установлен.");
        }
        finally
        {
            TryDelete(zipPath);
            try { Directory.Delete(extractDirectory, true); } catch { }
        }
    }

    private static async Task<ConfigValidationResult> ValidateConfigAsync(
        string executable,
        string configPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"run -test -c \"{configPath}\"",
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            return new ConfigValidationResult(
                false,
                "Проверка конфигурации Xray не завершилась за отведённое время.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var detail = string.Join(
            " ",
            new[] { stderr, stdout }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim()));

        return new ConfigValidationResult(process.ExitCode == 0, detail);
    }

    private static bool IsGeositeLoadError(string detail)
        => detail.Contains("geosite", StringComparison.OrdinalIgnoreCase)
           && (detail.Contains("failed to load", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("code not found", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("category-ru", StringComparison.OrdinalIgnoreCase));

    private static bool IsGeoIpLoadError(string detail)
        => detail.Contains("geoip", StringComparison.OrdinalIgnoreCase)
           && (detail.Contains("failed to load", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("code not found", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("geoip:ru", StringComparison.OrdinalIgnoreCase));

    private sealed record ConfigValidationResult(bool Success, string Detail);

    private string ReadLastError()
    {
        try
        {
            if (!File.Exists(ErrorLogPath))
                return "";

            return string.Join(
                " ",
                File.ReadLines(ErrorLogPath).TakeLast(3)).Trim();
        }
        catch
        {
            return "";
        }
    }

    private static string BuildConfig(
        ProxyNode node,
        AppSettings settings,
        string accessLogPath,
        string errorLogPath,
        bool includeCommunityRuList,
        bool includeRuGeoIp)
    {
        var rules = new List<object>();

        foreach (var rule in settings.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern))
                continue;

            var outbound = rule.Action.ToLowerInvariant() switch
            {
                "direct" => "direct",
                "block" => "block",
                _ => "proxy"
            };

            if (LooksLikeIpRule(rule.Pattern))
            {
                rules.Add(new Dictionary<string, object?>
                {
                    ["type"] = "field",
                    ["ip"] = new[] { rule.Pattern },
                    ["outboundTag"] = outbound
                });
            }
            else
            {
                var domain = HasDomainPrefix(rule.Pattern)
                    ? rule.Pattern
                    : "domain:" + rule.Pattern;

                rules.Add(new Dictionary<string, object?>
                {
                    ["type"] = "field",
                    ["domain"] = new[] { domain },
                    ["outboundTag"] = outbound
                });
            }
        }

        if (settings.RoutingMode == RoutingMode.Smart)
        {
            // Private networks never depend on external geodata.
            rules.Add(new Dictionary<string, object?>
            {
                ["type"] = "field",
                ["ip"] = new[]
                {
                    "10.0.0.0/8",
                    "172.16.0.0/12",
                    "192.168.0.0/16",
                    "127.0.0.0/8",
                    "169.254.0.0/16",
                    "::1/128",
                    "fc00::/7",
                    "fe80::/10"
                },
                ["outboundTag"] = "direct"
            });

            if (includeRuGeoIp)
            {
                rules.Add(new Dictionary<string, object?>
                {
                    ["type"] = "field",
                    ["ip"] = new[] { "geoip:ru" },
                    ["outboundTag"] = "direct"
                });
            }

            var russianDomains = new List<string>
            {
                "domain:ru",
                "domain:su",
                "domain:xn--p1ai"
            };

            if (includeCommunityRuList)
                russianDomains.Add("geosite:category-ru");

            rules.Add(new Dictionary<string, object?>
            {
                ["type"] = "field",
                ["domain"] = russianDomains,
                ["outboundTag"] = "direct"
            });
        }
        else if (settings.RoutingMode == RoutingMode.DirectAll)
        {
            rules.Add(new Dictionary<string, object?>
            {
                ["type"] = "field",
                ["network"] = "tcp,udp",
                ["outboundTag"] = "direct"
            });
        }

        var inbounds = new List<object>();

        if (settings.ConnectionMode == ConnectionMode.Tun)
        {
            inbounds.Add(new Dictionary<string, object?>
            {
                ["tag"] = "tun-in",
                ["protocol"] = "tun",
                ["settings"] = new Dictionary<string, object?>
                {
                    ["name"] = "Rayvia",
                    ["desc"] = "Rayvia",
                    ["mtu"] = 1500,
                    ["gateway"] = new[] { "10.66.0.1/30", "fd00:66::1/126" },
                    ["dns"] = new[]
                    {
                        "1.1.1.1",
                        "8.8.8.8",
                        "2606:4700:4700::1111",
                        "2001:4860:4860::8888"
                    },
                    ["autoSystemRoutingTable"] = new[] { "0.0.0.0/0", "::/0" },
                    ["autoOutboundsInterface"] = "auto"
                },
                ["sniffing"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["destOverride"] = new[] { "http", "tls", "quic" },
                    ["routeOnly"] = true
                }
            });
        }

        inbounds.Add(new Dictionary<string, object?>
        {
            ["tag"] = "socks-in",
            ["listen"] = "127.0.0.1",
            ["port"] = settings.SocksPort,
            ["protocol"] = "socks",
            ["settings"] = new Dictionary<string, object?> { ["udp"] = true }
        });

        inbounds.Add(new Dictionary<string, object?>
        {
            ["tag"] = "http-in",
            ["listen"] = "127.0.0.1",
            ["port"] = settings.HttpPort,
            ["protocol"] = "http"
        });

        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?>
            {
                ["loglevel"] = "warning",
                ["access"] = accessLogPath,
                ["error"] = errorLogPath
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = new object[]
            {
                BuildProxyOutbound(node),
                new Dictionary<string, object?>
                {
                    ["tag"] = "direct",
                    ["protocol"] = "freedom"
                },
                new Dictionary<string, object?>
                {
                    ["tag"] = "block",
                    ["protocol"] = "blackhole"
                }
            },
            ["routing"] = new Dictionary<string, object?>
            {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = rules
            }
        };

        return JsonSerializer.Serialize(
            config,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
    }

    private static Dictionary<string, object?> BuildProxyOutbound(ProxyNode node)
    {
        var outbound = new Dictionary<string, object?>
        {
            ["tag"] = "proxy",
            ["protocol"] = node.Protocol
        };

        switch (node.Protocol.ToLowerInvariant())
        {
            case "vless":
            {
                var user = new Dictionary<string, object?>
                {
                    ["id"] = node.UserId,
                    ["encryption"] = "none"
                };

                if (!string.IsNullOrWhiteSpace(node.Flow))
                    user["flow"] = node.Flow;

                outbound["settings"] = new Dictionary<string, object?>
                {
                    ["vnext"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["address"] = node.Host,
                            ["port"] = node.Port,
                            ["users"] = new object[] { user }
                        }
                    }
                };
                outbound["streamSettings"] = BuildStreamSettings(node);
                break;
            }

            case "vmess":
                outbound["settings"] = new Dictionary<string, object?>
                {
                    ["vnext"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["address"] = node.Host,
                            ["port"] = node.Port,
                            ["users"] = new object[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["id"] = node.UserId,
                                    ["alterId"] = node.AlterId,
                                    ["security"] = "auto"
                                }
                            }
                        }
                    }
                };
                outbound["streamSettings"] = BuildStreamSettings(node);
                break;

            case "trojan":
                outbound["settings"] = new Dictionary<string, object?>
                {
                    ["servers"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["address"] = node.Host,
                            ["port"] = node.Port,
                            ["password"] = node.Password
                        }
                    }
                };
                outbound["streamSettings"] = BuildStreamSettings(node);
                break;

            case "shadowsocks":
                outbound["settings"] = new Dictionary<string, object?>
                {
                    ["servers"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["address"] = node.Host,
                            ["port"] = node.Port,
                            ["method"] = node.Cipher,
                            ["password"] = node.Password
                        }
                    }
                };
                break;

            default:
                throw new NotSupportedException($"Протокол {node.Protocol} пока не поддерживается.");
        }

        return outbound;
    }

    private static Dictionary<string, object?> BuildStreamSettings(ProxyNode node)
    {
        var stream = new Dictionary<string, object?>
        {
            ["network"] = string.IsNullOrWhiteSpace(node.Network) ? "tcp" : node.Network,
            ["security"] = string.IsNullOrWhiteSpace(node.Security) ? "none" : node.Security
        };

        if (string.Equals(node.Security, "reality", StringComparison.OrdinalIgnoreCase))
        {
            stream["realitySettings"] = new Dictionary<string, object?>
            {
                ["serverName"] = node.Sni,
                ["fingerprint"] = node.Fingerprint ?? "chrome",
                ["publicKey"] = node.PublicKey,
                ["shortId"] = node.ShortId,
                ["spiderX"] = "/"
            };
        }
        else if (string.Equals(node.Security, "tls", StringComparison.OrdinalIgnoreCase))
        {
            var tls = new Dictionary<string, object?>
            {
                ["serverName"] = node.Sni ?? node.Host
            };

            if (!string.IsNullOrWhiteSpace(node.Fingerprint))
                tls["fingerprint"] = node.Fingerprint;

            stream["tlsSettings"] = tls;
        }

        if (string.Equals(node.Network, "ws", StringComparison.OrdinalIgnoreCase))
        {
            var ws = new Dictionary<string, object?> { ["path"] = node.Path ?? "/" };

            if (!string.IsNullOrWhiteSpace(node.HostHeader))
            {
                ws["headers"] = new Dictionary<string, string>
                {
                    ["Host"] = node.HostHeader
                };
            }

            stream["wsSettings"] = ws;
        }
        else if (string.Equals(node.Network, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            stream["grpcSettings"] = new Dictionary<string, object?>
            {
                ["serviceName"] = node.ServiceName ?? ""
            };
        }

        return stream;
    }

    private static bool HasDomainPrefix(string value)
        => value.StartsWith("domain:", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("full:", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("ext:", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeIpRule(string value)
    {
        if (value.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase))
            return true;

        var candidate = value.Split('/', 2)[0];
        return IPAddress.TryParse(candidate, out _);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        Disconnect();
        _http.Dispose();
    }
}
