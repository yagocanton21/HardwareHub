using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    public sealed class NetworkOptimizerService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static readonly DnsCandidate[] PublicDnsPresets =
        {
            new("cloudflare", "Cloudflare", new[] { "1.1.1.1", "1.0.0.1" }, new[] { "2606:4700:4700::1111", "2606:4700:4700::1001" }),
            new("google", "Google", new[] { "8.8.8.8", "8.8.4.4" }, new[] { "2001:4860:4860::8888", "2001:4860:4860::8844" }),
            new("quad9", "Quad9", new[] { "9.9.9.9", "149.112.112.112" }, new[] { "2620:fe::fe", "2620:fe::9" }),
            new("adguard", "AdGuard DNS", new[] { "94.140.14.14", "94.140.15.15" }, new[] { "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff" }),
            new("opendns", "OpenDNS", new[] { "208.67.222.222", "208.67.220.220" }, new[] { "2620:119:35::35", "2620:119:53::53" })
        };

        private static readonly string[] BenchmarkDomains =
        {
            "google.com", "youtube.com", "microsoft.com", "cloudflare.com",
            "github.com", "wikipedia.org", "uol.com.br", "mercadolivre.com.br"
        };

        private static readonly string[] VpnKeywords =
        {
            "vpn", "tap-windows", "wireguard", "wintun", "openvpn", "anyconnect",
            "fortinet", "forticlient", "nordlynx", "protonvpn", "expressvpn",
            "surfshark", "zerotier", "tailscale", "hamachi", "radmin"
        };

        public static string DefaultStatePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HardwareHub",
            "network-optimization.json");

        private readonly string _statePath;
        private readonly IRegistryBackend _registry;
        private readonly Func<IPAddress, string, CancellationToken, Task<double?>> _dnsProbe;
        private readonly Func<string, CancellationToken, Task<(int ExitCode, string Output)>> _runPowerShell;

        public NetworkOptimizerService(
            string? statePath = null,
            IRegistryBackend? registry = null,
            Func<IPAddress, string, CancellationToken, Task<double?>>? dnsProbeHook = null,
            Func<string, CancellationToken, Task<(int ExitCode, string Output)>>? powerShellHook = null)
        {
            _statePath = statePath ?? DefaultStatePath;
            _registry = registry ?? new WindowsRegistryBackend();
            _dnsProbe = dnsProbeHook ?? ProbeDnsUdpAsync;
            _runPowerShell = powerShellHook ?? RunPowerShellAsync;
        }

        public NetworkSavedState? LoadSavedState()
        {
            try
            {
                if (!File.Exists(_statePath)) return null;
                var state = JsonSerializer.Deserialize<NetworkSavedState>(File.ReadAllText(_statePath), JsonOptions);
                return state != null && !state.Reverted && state.AppliedIds.Count > 0 ? state : null;
            }
            catch
            {
                return null;
            }
        }

        public void SaveState(NetworkSavedState state)
        {
            string? dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tmp, _statePath, overwrite: true);
        }

        public static byte[] BuildDnsQueryPacket(ushort queryId, string domain)
        {
            var packet = new List<byte>(48)
            {
                (byte)(queryId >> 8), (byte)queryId,
                0x01, 0x00, // Standard query + Recursion Desired
                0x00, 0x01, // QDCOUNT = 1
                0x00, 0x00,
                0x00, 0x00,
                0x00, 0x00
            };

            foreach (string label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(label);
                packet.Add((byte)bytes.Length);
                packet.AddRange(bytes);
            }

            packet.Add(0x00); // root label
            packet.AddRange(new byte[] { 0x00, 0x01, 0x00, 0x01 }); // Type A, Class IN
            return packet.ToArray();
        }

        public async Task<DnsBenchmarkResult> MeasureDnsAsync(DnsCandidate candidate, CancellationToken ct = default)
        {
            if (!IPAddress.TryParse(candidate.IPv4.FirstOrDefault() ?? "", out IPAddress? serverIp))
            {
                return new DnsBenchmarkResult(candidate, 0, 0, 1, 1);
            }

            var times = new List<double>();
            int failures = 0;
            int total = 0;

            foreach (string domain in BenchmarkDomains)
            {
                ct.ThrowIfCancellationRequested();
                total++;
                double? ms = await _dnsProbe(serverIp, domain, ct);
                if (ms.HasValue && ms.Value >= 0) times.Add(ms.Value);
                else failures++;
            }

            if (times.Count == 0)
            {
                return new DnsBenchmarkResult(candidate, 0, 0, failures, total);
            }

            times.Sort();
            double median = times.Count % 2 == 1
                ? times[times.Count / 2]
                : (times[times.Count / 2 - 1] + times[times.Count / 2]) / 2.0;

            return new DnsBenchmarkResult(candidate, median, times.Average(), failures, total);
        }

        public async Task<NetworkAnalysisPlan?> AnalyzeAsync(
            Action<string>? onStage = null,
            Action<DnsBenchmarkResult>? onDnsMeasured = null,
            CancellationToken ct = default)
        {
            onStage?.Invoke("Detectando adaptador de rede ativo...");
            NetworkInterface? nic = await Task.Run(FindActiveAdapter, ct);
            if (nic == null) return null;

            string guid = nic.Id;
            IPInterfaceProperties ipProps = nic.GetIPProperties();
            int? ifIndex = GetInterfaceIndex(nic);
            bool isWifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            bool hasV6 = ipProps.UnicastAddresses.Any(u =>
                u.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                !u.Address.IsIPv6LinkLocal &&
                !u.Address.IsIPv6SiteLocal);

            List<string>? fixedV4 = ReadFixedDns(guid, ipv6: false);
            List<string>? fixedV6 = ReadFixedDns(guid, ipv6: true);

            onStage?.Invoke("Lendo configurações de TCP e Otimização de Entrega...");
            var (_, autoTuningOut) = await _runPowerShell("(Get-NetTCPSetting -SettingName Internet -ErrorAction Stop).AutoTuningLevelLocal", ct);
            string autoTuning = autoTuningOut.Trim();

            var (deliveryMode, deliveryRead) = ReadDeliveryOptimizationMode();
            bool deliveryManagedByPolicy = _registry.Read(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode").Exists;

            string[] currentDnsIps = ipProps.DnsAddresses
                .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                .Select(d => d.ToString())
                .Distinct()
                .ToArray();

            var candidates = new List<DnsCandidate>();
            if (currentDnsIps.Length > 0)
            {
                var knownMatch = PublicDnsPresets.FirstOrDefault(p => p.IPv4.Contains(currentDnsIps[0], StringComparer.OrdinalIgnoreCase));
                candidates.Add(knownMatch != null
                    ? knownMatch with { IsCurrent = true }
                    : new DnsCandidate("atual", fixedV4 == null ? "Atual (Roteador/Provedor)" : "Atual (Fixo)", currentDnsIps, Array.Empty<string>(), IsCurrent: true));
            }

            foreach (var preset in PublicDnsPresets)
            {
                if (!candidates.Any(c => c.Id == preset.Id))
                    candidates.Add(preset);
            }

            onStage?.Invoke("Medindo latência real dos servidores DNS...");
            var benchmarkTasks = candidates.Select(async c =>
            {
                var res = await MeasureDnsAsync(c, ct);
                onDnsMeasured?.Invoke(res);
                return res;
            });

            var results = (await Task.WhenAll(benchmarkTasks))
                .OrderBy(r => r.SortScore)
                .ToList();

            var currentBench = results.FirstOrDefault(r => r.Candidate.IsCurrent);
            var bestAlt = results.FirstOrDefault(r => r.IsReachable && !r.Candidate.IsCurrent);
            bool shouldSwitchDns = bestAlt != null &&
                (currentBench == null || !currentBench.IsReachable ||
                 bestAlt.MedianMs < currentBench.MedianMs - Math.Max(4.0, currentBench.MedianMs * 0.15));

            var snapshot = new NetworkSavedState
            {
                SavedAt = DateTime.Now,
                AdapterId = guid,
                AdapterName = nic.Name,
                PreviousDnsV4 = fixedV4,
                PreviousDnsV6 = fixedV6,
                PreviousAutoTuning = autoTuning,
                PreviousDeliveryMode = deliveryMode,
                PreviousDeliveryRead = deliveryRead
            };

            var adjustments = new List<NetworkTweakItem>
            {
                new()
                {
                    Id = "dns",
                    Title = shouldSwitchDns ? $"Trocar DNS para {bestAlt!.Candidate.Name}" : "Servidor DNS",
                    Explanation = "Seleciona o servidor DNS com menor latência real medida para acelerar a resolução de sites (com reversão segura).",
                    CurrentValue = currentBench == null ? "Sem DNS" : $"{currentBench.Candidate.Name} ({currentBench.FormattedMedian})",
                    TargetValue = shouldSwitchDns ? $"{bestAlt!.Candidate.Name} ({bestAlt.FormattedMedian})" : "DNS atual já está ótimo",
                    IsNeeded = shouldSwitchDns,
                    IsSelected = shouldSwitchDns
                },
                new()
                {
                    Id = "flushdns",
                    Title = "Limpar cache de DNS (Flush DNS)",
                    Explanation = "Remove entradas antigas em cache no Windows (ipconfig /flushdns). Seguro e sem efeito colateral.",
                    CurrentValue = "Cache ativo",
                    TargetValue = "Cache limpo",
                    IsNeeded = true,
                    IsSelected = true
                },
                new()
                {
                    Id = "autotuning",
                    Title = "Ajuste automático da janela TCP (AutoTuning)",
                    Explanation = "Mantém o padrão 'normal' do Windows 10/11 para máxima largura de banda em downloads.",
                    CurrentValue = string.IsNullOrWhiteSpace(autoTuning) ? "Indisponível" : autoTuning,
                    TargetValue = IsSubOptimalAutoTuning(autoTuning) ? "normal (padrão)" : "Já está otimizado",
                    IsNeeded = IsSubOptimalAutoTuning(autoTuning),
                    IsSelected = IsSubOptimalAutoTuning(autoTuning)
                },
                new()
                {
                    Id = "entrega",
                    Title = "Limitar Otimização de Entrega (Windows Update P2P)",
                    Explanation = "Evita que o Windows envie atualizações pela sua banda de upload para desconhecidos na internet (mantém rede local).",
                    CurrentValue = deliveryManagedByPolicy ? "Gerenciado por política" : (deliveryMode == 3 ? "P2P na Internet (ativo)" : "Restrito à rede local/desligado"),
                    TargetValue = !deliveryManagedByPolicy && deliveryRead && deliveryMode == 3 ? "Somente rede local (1)" : "Sem necessidade",
                    IsNeeded = !deliveryManagedByPolicy && deliveryRead && deliveryMode == 3,
                    IsSelected = !deliveryManagedByPolicy && deliveryRead && deliveryMode == 3
                }
            };

            var warnings = new List<string>();
            string? vpn = DetectActiveVpn();
            if (vpn != null) warnings.Add($"VPN ativa detectada ({vpn}) — medições refletem a rota da VPN.");
            string? proxy = DetectActiveProxy();
            if (proxy != null) warnings.Add($"Proxy configurado no Windows ({proxy}).");

            return new NetworkAnalysisPlan
            {
                AdapterId = guid,
                AdapterName = nic.Name,
                AdapterDescription = nic.Description,
                InterfaceIndex = ifIndex,
                IsWiFi = isWifi,
                HasIPv6 = hasV6,
                DnsBenchmarks = results,
                RecommendedDns = shouldSwitchDns ? bestAlt!.Candidate : null,
                Adjustments = adjustments,
                Warnings = warnings,
                Snapshot = snapshot
            };
        }

        public async Task<(NetworkSavedState State, List<string> Errors)> ApplyPlanAsync(
            NetworkAnalysisPlan plan,
            DnsCandidate? overrideDns = null,
            Action<string>? onStage = null,
            CancellationToken ct = default)
        {
            var state = plan.Snapshot;
            state.SavedAt = DateTime.Now;
            state.AppliedIds = new List<string>();
            state.Reverted = false;
            state.RevertedAt = null;
            SaveState(state);

            var errors = new List<string>();
            DnsCandidate? targetDns = overrideDns ?? plan.RecommendedDns;

            foreach (var adj in plan.Adjustments.Where(a => a.IsSelected && (a.IsNeeded || (a.Id == "dns" && overrideDns != null))))
            {
                ct.ThrowIfCancellationRequested();
                onStage?.Invoke($"{adj.Title}...");

                bool ok = false;
                switch (adj.Id)
                {
                    case "dns":
                        if (targetDns != null && plan.InterfaceIndex.HasValue)
                        {
                            ok = await SetAdapterDnsAsync(plan.InterfaceIndex.Value, targetDns, plan.HasIPv6, ct);
                            if (ok) state.AppliedDnsName = targetDns.Name;
                        }
                        break;
                    case "flushdns":
                        var (fExit, _) = await _runPowerShell("Clear-DnsClientCache -ErrorAction Stop", ct);
                        ok = fExit == 0;
                        break;
                    case "autotuning":
                        var (tExit, _) = await _runPowerShell("netsh interface tcp set global autotuninglevel=normal", ct);
                        ok = tExit == 0;
                        break;
                    case "entrega":
                        try
                        {
                            _registry.Write(@"HKU\S-1-5-20\Software\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Settings", "DownloadMode", RegistryValueKind.DWord, 1);
                            ok = true;
                        }
                        catch
                        {
                            ok = false;
                        }
                        break;
                }

                if (ok)
                {
                    if (adj.Id != "flushdns") state.AppliedIds.Add(adj.Id);
                }
                else
                {
                    errors.Add($"{adj.Title} (pode exigir Administrador).");
                }
                SaveState(state);
            }

            if (overrideDns != null && !state.AppliedIds.Contains("dns") && plan.InterfaceIndex.HasValue)
            {
                bool dnsOk = await SetAdapterDnsAsync(plan.InterfaceIndex.Value, overrideDns, plan.HasIPv6, ct);
                if (dnsOk)
                {
                    state.AppliedDnsName = overrideDns.Name;
                    state.AppliedIds.Add("dns");
                    SaveState(state);
                }
                else
                {
                    errors.Add($"Aplicar DNS {overrideDns.Name} (pode exigir Administrador).");
                }
            }

            return (state, errors);
        }

        public async Task<bool> SetAdapterDnsAsync(int interfaceIndex, DnsCandidate dns, bool includeIpv6, CancellationToken ct = default)
        {
            var servers = dns.IPv4
                .Concat(includeIpv6 ? dns.IPv6 : Array.Empty<string>())
                .Where(s => IPAddress.TryParse(s, out _))
                .Select(s => $"'{s}'");

            string addrList = string.Join(",", servers);
            if (string.IsNullOrEmpty(addrList)) return false;

            var (exit, _) = await _runPowerShell(
                $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ServerAddresses @({addrList}) -ErrorAction Stop; Clear-DnsClientCache",
                ct);
            return exit == 0;
        }

        public async Task<bool> ResetDnsToDhcpAsync(int interfaceIndex, CancellationToken ct = default)
        {
            var (exit, _) = await _runPowerShell(
                $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ResetServerAddresses -ErrorAction Stop; Clear-DnsClientCache",
                ct);
            return exit == 0;
        }

        public async Task<List<string>> RevertSavedOptimizationAsync(Action<string>? onStage = null, CancellationToken ct = default)
        {
            var state = LoadSavedState();
            var errors = new List<string>();
            if (state == null) return errors;

            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id.Equals(state.AdapterId, StringComparison.OrdinalIgnoreCase));
            int? ifIndex = nic != null ? GetInterfaceIndex(nic) : null;

            foreach (string id in state.AppliedIds.ToList())
            {
                ct.ThrowIfCancellationRequested();
                onStage?.Invoke($"Revertendo: {id}...");
                bool ok = false;
                switch (id)
                {
                    case "dns":
                        if (ifIndex.HasValue)
                        {
                            var sb = new StringBuilder($"Set-DnsClientServerAddress -InterfaceIndex {ifIndex.Value} -ResetServerAddresses -ErrorAction Stop");
                            var v4 = (state.PreviousDnsV4 ?? new List<string>()).Where(s => IPAddress.TryParse(s, out _)).ToList();
                            var v6 = (state.PreviousDnsV6 ?? new List<string>()).Where(s => IPAddress.TryParse(s, out _)).ToList();
                            if (v4.Count > 0)
                                sb.Append($"; Set-DnsClientServerAddress -InterfaceIndex {ifIndex.Value} -ServerAddresses @({string.Join(",", v4.Select(x => $"'{x}'"))}) -ErrorAction Stop");
                            if (v6.Count > 0)
                                sb.Append($"; Set-DnsClientServerAddress -InterfaceIndex {ifIndex.Value} -ServerAddresses @({string.Join(",", v6.Select(x => $"'{x}'"))}) -ErrorAction Stop");
                            sb.Append("; Clear-DnsClientCache");
                            var (dExit, _) = await _runPowerShell(sb.ToString(), ct);
                            ok = dExit == 0;
                        }
                        break;
                    case "autotuning":
                        if (!string.IsNullOrWhiteSpace(state.PreviousAutoTuning))
                        {
                            string prev = state.PreviousAutoTuning.ToLowerInvariant() switch
                            {
                                "disabled" or "restricted" or "highlyrestricted" or "experimental" or "normal" => state.PreviousAutoTuning.ToLowerInvariant(),
                                _ => "normal"
                            };
                            var (tExit, _) = await _runPowerShell($"netsh interface tcp set global autotuninglevel={prev}", ct);
                            ok = tExit == 0;
                        }
                        break;
                    case "entrega":
                        try
                        {
                            string key = @"HKU\S-1-5-20\Software\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Settings";
                            if (state.PreviousDeliveryMode.HasValue)
                                _registry.Write(key, "DownloadMode", RegistryValueKind.DWord, state.PreviousDeliveryMode.Value);
                            else
                                _registry.DeleteValue(key, "DownloadMode");
                            ok = true;
                        }
                        catch
                        {
                            ok = false;
                        }
                        break;
                }

                if (!ok) errors.Add(id);
            }

            if (errors.Count == 0)
            {
                state.Reverted = true;
                state.RevertedAt = DateTime.Now;
                SaveState(state);
            }
            return errors;
        }

        private static bool IsSubOptimalAutoTuning(string autoTuning) =>
            autoTuning.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
            autoTuning.Equals("restricted", StringComparison.OrdinalIgnoreCase) ||
            autoTuning.Equals("highlyrestricted", StringComparison.OrdinalIgnoreCase);

        private List<string>? ReadFixedDns(string guid, bool ipv6)
        {
            string key = $@"HKLM\SYSTEM\CurrentControlSet\Services\{(ipv6 ? "Tcpip6" : "Tcpip")}\Parameters\Interfaces\{guid}";
            string? raw = _registry.Read(key, "NameServer").Data as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var list = raw.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            return list.Count > 0 ? list : null;
        }

        private (int? Mode, bool Read) ReadDeliveryOptimizationMode()
        {
            try
            {
                var read = _registry.Read(@"HKU\S-1-5-20\Software\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Settings", "DownloadMode");
                return read.Exists ? (read.Data as int?, true) : (null, true);
            }
            catch
            {
                return (null, false);
            }
        }

        private string? DetectActiveProxy()
        {
            var en = _registry.Read(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyEnable");
            if (en.Data is int v && v == 1)
            {
                return (_registry.Read(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyServer").Data as string) ?? "Proxy manual";
            }
            return null;
        }

        private static string? DetectActiveVpn()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                        (n.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                         VpnKeywords.Any(k => n.Description.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                              n.Name.Contains(k, StringComparison.OrdinalIgnoreCase))))
                    ?.Description;
            }
            catch
            {
                return null;
            }
        }

        private static NetworkInterface? FindActiveAdapter()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                                n.GetIPProperties().GatewayAddresses.Any(g =>
                                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                    !g.Address.Equals(IPAddress.Any)))
                    .OrderByDescending(n => n.Speed)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static int? GetInterfaceIndex(NetworkInterface nic)
        {
            try { return nic.GetIPProperties().GetIPv4Properties()?.Index; } catch { }
            try { return nic.GetIPProperties().GetIPv6Properties()?.Index; } catch { }
            return null;
        }

        private static async Task<double?> ProbeDnsUdpAsync(IPAddress server, string domain, CancellationToken ct)
        {
            using var udp = new UdpClient(server.AddressFamily);
            var endPoint = new IPEndPoint(server, 53);
            ushort id = (ushort)Random.Shared.Next(1, 65535);
            byte[] payload = BuildDnsQueryPacket(id, domain);

            var sw = Stopwatch.StartNew();
            try
            {
                await udp.SendAsync(payload, endPoint, ct);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(1200);

                while (true)
                {
                    var recv = await udp.ReceiveAsync(timeoutCts.Token);
                    if (recv.Buffer.Length >= 12 && ((recv.Buffer[0] << 8) | recv.Buffer[1]) == id)
                    {
                        int rcode = recv.Buffer[3] & 0x0F;
                        return rcode is 0 or 3 ? sw.Elapsed.TotalMilliseconds : null;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<(int ExitCode, string Output)> RunPowerShellAsync(string script, CancellationToken ct)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o PowerShell.");
            using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
            string stdout = await proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
            string stderr = await proc.StandardError.ReadToEndAsync(CancellationToken.None);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
        }
    }
}

