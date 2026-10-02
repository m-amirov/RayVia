using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
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

    public bool IsRunning => _process is { HasExited: false };

    public XrayCoreService(LogService log)
    {
        _log = log;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Rayvia/0.1");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task ConnectAsync(ProxyNode node, AppSettings settings)
    {
        await EnsureCoreAsync();

        Disconnect();

        var configPath = Path.Combine(_coreDirectory, "rayvia-config.json");
        var config = BuildConfig(node, settings);
        await File.WriteAllTextAsync(configPath, config);

        var executable = Path.Combine(_coreDirectory, "xray.exe");
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

        await Task.Delay(500);
        if (_process.HasExited)
            throw new InvalidOperationException("Xray завершился сразу после запуска. Откройте журнал для подробностей.");

        _log.Write($"Подключение: {node.Name} ({node.Protocol.ToUpperInvariant()}).");
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
                _process.WaitForExit(2000);
            }
        }
        catch
        {
            // Best-effort shutdown.
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private async Task EnsureCoreAsync()
    {
        Directory.CreateDirectory(_coreDirectory);
        var executable = Path.Combine(_coreDirectory, "xray.exe");

        if (File.Exists(executable))
            return;

        _log.Write("Скачивание Xray core…");

        using var releaseResponse = await _http.GetAsync(LatestXrayApi);
        releaseResponse.EnsureSuccessStatusCode();

        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(releaseStream);

        string? downloadUrl = null;
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), XrayAssetName, StringComparison.OrdinalIgnoreCase))
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
            using (var download = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                download.EnsureSuccessStatusCode();
                await using var input = await download.Content.ReadAsStreamAsync();
                await using var output = File.Create(zipPath);
                await input.CopyToAsync(output);
            }

            ZipFile.ExtractToDirectory(zipPath, extractDirectory, true);

            foreach (var fileName in new[] { "xray.exe", "geoip.dat", "geosite.dat" })
            {
                var source = Directory.EnumerateFiles(extractDirectory, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (source is not null)
                    File.Copy(source, Path.Combine(_coreDirectory, fileName), true);
            }

            if (!File.Exists(executable))
                throw new InvalidOperationException("Архив Xray не содержит xray.exe.");

            _log.Write("Xray core установлен.");
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
            try { Directory.Delete(extractDirectory, true); } catch { }
        }
    }

    private static string BuildConfig(ProxyNode node, AppSettings settings)
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
                var ip = rule.Pattern.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase)
                    ? rule.Pattern
                    : rule.Pattern;

                rules.Add(new Dictionary<string, object?>
                {
                    ["type"] = "field",
                    ["ip"] = new[] { ip },
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
            rules.Add(new Dictionary<string, object?>
            {
                ["type"] = "field",
                ["ip"] = new[] { "geoip:private", "geoip:ru" },
                ["outboundTag"] = "direct"
            });
            rules.Add(new Dictionary<string, object?>
            {
                ["type"] = "field",
                ["domain"] = new[] { "geosite:ru" },
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

        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?> { ["loglevel"] = "warning" },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["tag"] = "socks-in",
                    ["listen"] = "127.0.0.1",
                    ["port"] = settings.SocksPort,
                    ["protocol"] = "socks",
                    ["settings"] = new Dictionary<string, object?> { ["udp"] = true }
                },
                new Dictionary<string, object?>
                {
                    ["tag"] = "http-in",
                    ["listen"] = "127.0.0.1",
                    ["port"] = settings.HttpPort,
                    ["protocol"] = "http"
                }
            },
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

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
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
                var vlessUser = new Dictionary<string, object?>
                {
                    ["id"] = node.UserId,
                    ["encryption"] = "none"
                };
                if (!string.IsNullOrWhiteSpace(node.Flow))
                    vlessUser["flow"] = node.Flow;

                outbound["settings"] = new Dictionary<string, object?>
                {
                    ["vnext"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["address"] = node.Host,
                            ["port"] = node.Port,
                            ["users"] = new object[] { vlessUser }
                        }
                    }
                };
                outbound["streamSettings"] = BuildStreamSettings(node);
                break;

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
            stream["tlsSettings"] = new Dictionary<string, object?>
            {
                ["serverName"] = node.Sni ?? node.Host,
                ["fingerprint"] = node.Fingerprint
            };
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

    public void Dispose()
    {
        Disconnect();
        _http.Dispose();
    }
}
