using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HardwareHub.Models;
using HardwareHub.Services;

namespace HardwareHub.ViewModels
{
    public sealed class NetworkOptimizerViewModel : INotifyPropertyChanged
    {
        private readonly NetworkOptimizerService _service;
        private readonly Action<string> _log;
        private readonly Func<CancellationToken, Task<(int ExitCode, string Output)>> _flushDnsRunner;

        private bool _isBusyNetwork;
        private string _networkStatusBanner = "Clique em \"Analisar Rede & DNS\" para testar latência DNS e diagnosticar o adaptador ativo.";
        private NetworkAnalysisPlan? _currentPlan;
        private NetworkSavedState? _savedState;
        private DnsCandidate _selectedManualDns = NetworkOptimizerService.PublicDnsPresets[0];
        private CancellationTokenSource? _cts;
        private bool _analyzedOnce;

        public ObservableCollection<DnsBenchmarkResult> DnsBenchmarks { get; } = new();
        public ObservableCollection<NetworkTweakItem> Adjustments { get; } = new();
        public ObservableCollection<string> Warnings { get; } = new();
        public IReadOnlyList<DnsCandidate> DnsPresets => NetworkOptimizerService.PublicDnsPresets;

        public NetworkOptimizerViewModel(
            NetworkOptimizerService? service = null,
            Action<string>? logCallback = null,
            Func<CancellationToken, Task<(int ExitCode, string Output)>>? flushDnsHook = null)
        {
            _service = service ?? new NetworkOptimizerService();
            _log = logCallback ?? (_ => { });
            _flushDnsRunner = flushDnsHook ?? DefaultFlushDnsAsync;
            _savedState = _service.LoadSavedState();

            AnalyzeNetworkCommand = new RelayCommand(_ => _ = AnalyzeAsync(), _ => !IsBusyNetwork);
            ApplyNetworkPlanCommand = new RelayCommand(_ => _ = ApplyPlanAsync(), _ => !IsBusyNetwork && CurrentPlan != null);
            ApplyPresetDnsCommand = new RelayCommand(p => _ = ApplyDnsAsync(p), _ => !IsBusyNetwork && CurrentPlan?.InterfaceIndex != null);
            ResetDnsDhcpCommand = new RelayCommand(_ => _ = ResetDnsDhcpAsync(), _ => !IsBusyNetwork && CurrentPlan?.InterfaceIndex != null);
            RevertNetworkCommand = new RelayCommand(_ => _ = RevertAsync(), _ => !IsBusyNetwork && HasSavedRevert);
            FlushDnsCommand = new RelayCommand(_ => _ = FlushDnsAsync(), _ => !IsBusyNetwork);
            CancelNetworkTaskCommand = new RelayCommand(_ => CancelActiveTask(), _ => IsBusyNetwork);
        }

        public bool IsBusyNetwork
        {
            get => _isBusyNetwork;
            private set
            {
                if (SetField(ref _isBusyNetwork, value))
                    InvalidateCommands();
            }
        }

        public string NetworkStatusBanner
        {
            get => _networkStatusBanner;
            private set => SetField(ref _networkStatusBanner, value);
        }

        public NetworkAnalysisPlan? CurrentPlan
        {
            get => _currentPlan;
            private set
            {
                if (SetField(ref _currentPlan, value))
                {
                    OnPropertyChanged(nameof(HasAdapterPlan));
                    OnPropertyChanged(nameof(AdapterSummary));
                    OnPropertyChanged(nameof(RecommendedDnsLabel));
                    InvalidateCommands();
                }
            }
        }

        public bool HasAdapterPlan => CurrentPlan != null;

        public string AdapterSummary
        {
            get
            {
                if (CurrentPlan == null)
                    return "Nenhum adaptador analisado ainda.";
                var type = CurrentPlan.IsWiFi ? "Wi-Fi" : "Ethernet / Cabo";
                var ipv6 = CurrentPlan.HasIPv6 ? "IPv4 + IPv6" : "Somente IPv4";
                return $"{CurrentPlan.AdapterName} ({type} • {ipv6}) — {CurrentPlan.AdapterDescription}";
            }
        }

        public string RecommendedDnsLabel => CurrentPlan?.RecommendedDns != null
            ? $"Recomendado: {CurrentPlan.RecommendedDns.Name} ({string.Join(", ", CurrentPlan.RecommendedDns.IPv4)})"
            : "Nenhum DNS mais rápido detectado.";

        public DnsCandidate SelectedManualDns
        {
            get => _selectedManualDns;
            set => SetField(ref _selectedManualDns, value);
        }

        public bool HasWarnings => Warnings.Count > 0;

        public bool HasSavedRevert => _savedState != null && !_savedState.Reverted && _savedState.AppliedIds.Count > 0;

        public string SavedRevertSummary => HasSavedRevert && _savedState != null
            ? $"Backup salvo em {_savedState.SavedAt:dd/MM HH:mm} ({_savedState.AdapterName} • {string.Join(", ", _savedState.AppliedIds)})"
            : "Nenhuma otimização pendente para reverter.";

        public ICommand AnalyzeNetworkCommand { get; }
        public ICommand ApplyNetworkPlanCommand { get; }
        public ICommand ApplyPresetDnsCommand { get; }
        public ICommand ResetDnsDhcpCommand { get; }
        public ICommand RevertNetworkCommand { get; }
        public ICommand FlushDnsCommand { get; }
        public ICommand CancelNetworkTaskCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void EnsureLoaded()
        {
            if (!_analyzedOnce && !IsBusyNetwork)
            {
                _analyzedOnce = true;
                _ = AnalyzeAsync();
            }
        }

        public async Task AnalyzeAsync()
        {
            if (IsBusyNetwork)
                return;

            var ct = BeginTask();
            IsBusyNetwork = true;
            DnsBenchmarks.Clear();
            Adjustments.Clear();
            Warnings.Clear();
            OnPropertyChanged(nameof(HasWarnings));
            NetworkStatusBanner = "Medindo latência UDP real dos servidores DNS...";

            try
            {
                var plan = await _service.AnalyzeAsync(
                    onStage: msg => NetworkStatusBanner = msg,
                    onDnsMeasured: res => DnsBenchmarks.Add(res),
                    ct: ct);

                if (plan == null)
                {
                    CurrentPlan = null;
                    NetworkStatusBanner = "Nenhum adaptador de rede ativo com gateway padrão encontrado.";
                    return;
                }

                // Reorder DnsBenchmarks cleanly by plan order (sorted by SortScore)
                DnsBenchmarks.Clear();
                foreach (var d in plan.DnsBenchmarks)
                    DnsBenchmarks.Add(d);

                foreach (var adj in plan.Adjustments)
                {
                    adj.PropertyChanged += OnAdjustmentPropertyChanged;
                    Adjustments.Add(adj);
                }

                foreach (var w in plan.Warnings)
                    Warnings.Add(w);
                OnPropertyChanged(nameof(HasWarnings));

                CurrentPlan = plan;
                if (plan.RecommendedDns != null && !plan.RecommendedDns.IsCurrent)
                    SelectedManualDns = plan.RecommendedDns;

                var neededCount = plan.Adjustments.Count(a => a.IsNeeded);
                NetworkStatusBanner =
                    $"Análise concluída para \"{plan.AdapterName}\": {neededCount} melhoria(s) recomendada(s).";
                _log($"[REDE] {NetworkStatusBanner}");
            }
            catch (OperationCanceledException)
            {
                NetworkStatusBanner = "Análise de rede cancelada.";
            }
            finally
            {
                _savedState = _service.LoadSavedState();
                OnPropertyChanged(nameof(HasSavedRevert));
                OnPropertyChanged(nameof(SavedRevertSummary));
                IsBusyNetwork = false;
                InvalidateCommands();
            }
        }

        public async Task ApplyPlanAsync()
        {
            if (IsBusyNetwork || CurrentPlan == null)
                return;

            var ct = BeginTask();
            IsBusyNetwork = true;
            NetworkStatusBanner = "Aplicando otimizações selecionadas (salvando backup anterior)...";

            try
            {
                var (state, errors) = await _service.ApplyPlanAsync(
                    CurrentPlan,
                    overrideDns: SelectedManualDns,
                    onStage: msg => NetworkStatusBanner = msg,
                    ct: ct);

                _savedState = _service.LoadSavedState() ?? state;
                OnPropertyChanged(nameof(HasSavedRevert));
                OnPropertyChanged(nameof(SavedRevertSummary));

                if (errors.Count == 0)
                    NetworkStatusBanner = $"Otimização aplicada com sucesso ({state.AppliedIds.Count} ajuste(s)).";
                else
                    NetworkStatusBanner = $"Aplicado com {errors.Count} aviso(s): {errors[0]}";

                _log($"[REDE] {NetworkStatusBanner}");
            }
            catch (OperationCanceledException)
            {
                NetworkStatusBanner = "Aplicação cancelada.";
            }
            finally
            {
                IsBusyNetwork = false;
                InvalidateCommands();
            }
        }

        public async Task ApplyDnsAsync(object? param)
        {
            if (IsBusyNetwork || CurrentPlan?.InterfaceIndex == null)
                return;

            var dns = param switch
            {
                DnsBenchmarkResult br => br.Candidate,
                DnsCandidate c => c,
                _ => SelectedManualDns
            };

            if (dns == null || dns.IPv4.Length == 0)
                return;

            var ct = BeginTask();
            IsBusyNetwork = true;
            NetworkStatusBanner = $"Aplicando DNS {dns.Name} ({string.Join(", ", dns.IPv4)})...";

            try
            {
                bool ok = await _service.SetAdapterDnsAsync(CurrentPlan.InterfaceIndex.Value, dns, CurrentPlan.HasIPv6, ct);
                NetworkStatusBanner = ok
                    ? $"DNS alterado para {dns.Name} ({string.Join(", ", dns.IPv4)})."
                    : $"Não foi possível alterar o DNS (verifique permissões de administrador).";
                _log($"[REDE] {NetworkStatusBanner}");
            }
            catch (OperationCanceledException)
            {
                NetworkStatusBanner = "Alteração de DNS cancelada.";
            }
            finally
            {
                IsBusyNetwork = false;
                InvalidateCommands();
            }
        }

        public async Task ResetDnsDhcpAsync()
        {
            if (IsBusyNetwork || CurrentPlan?.InterfaceIndex == null)
                return;

            var ct = BeginTask();
            IsBusyNetwork = true;
            NetworkStatusBanner = $"Restaurando DNS automático (DHCP) em {CurrentPlan.AdapterName}...";

            try
            {
                bool ok = await _service.ResetDnsToDhcpAsync(CurrentPlan.InterfaceIndex.Value, ct);
                NetworkStatusBanner = ok
                    ? $"DNS restaurado para Automático (DHCP) em {CurrentPlan.AdapterName}."
                    : "Falha ao restaurar DNS via DHCP.";
                _log($"[REDE] {NetworkStatusBanner}");
            }
            catch (OperationCanceledException)
            {
                NetworkStatusBanner = "Operação cancelada.";
            }
            finally
            {
                IsBusyNetwork = false;
                InvalidateCommands();
            }
        }

        public async Task RevertAsync()
        {
            if (IsBusyNetwork || !HasSavedRevert)
                return;

            var ct = BeginTask();
            IsBusyNetwork = true;
            NetworkStatusBanner = "Revertendo alterações de rede para o estado salvo...";

            try
            {
                var errors = await _service.RevertSavedOptimizationAsync(msg => NetworkStatusBanner = msg, ct);
                _savedState = _service.LoadSavedState();
                OnPropertyChanged(nameof(HasSavedRevert));
                OnPropertyChanged(nameof(SavedRevertSummary));

                NetworkStatusBanner = errors.Count == 0
                    ? "Rede restaurada exatamente para o estado anterior."
                    : $"Reversão concluída com {errors.Count} aviso(s): {errors[0]}";
                _log($"[REDE] {NetworkStatusBanner}");
            }
            catch (OperationCanceledException)
            {
                NetworkStatusBanner = "Reversão cancelada.";
            }
            finally
            {
                IsBusyNetwork = false;
                InvalidateCommands();
            }
        }

        public async Task FlushDnsAsync()
        {
            if (IsBusyNetwork)
                return;

            var ct = BeginTask();
            IsBusyNetwork = true;
            NetworkStatusBanner = "Limpando cache DNS local (ipconfig /flushdns)...";

            try
            {
                var (code, output) = await _flushDnsRunner(ct);
                NetworkStatusBanner = code == 0
                    ? "Cache DNS do Windows limpo com sucesso."
                    : $"Falha ao limpar cache DNS: {output.Trim()}";
                _log($"[REDE] {NetworkStatusBanner}");
            }
            catch (OperationCanceledException)
            {
                NetworkStatusBanner = "Limpeza de cache cancelada.";
            }
            finally
            {
                IsBusyNetwork = false;
                InvalidateCommands();
            }
        }

        public void CancelActiveTask() => _cts?.Cancel();

        private CancellationToken BeginTask()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            return _cts.Token;
        }

        private void OnAdjustmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NetworkTweakItem.IsSelected))
                InvalidateCommands();
        }

        private static async Task<(int ExitCode, string Output)> DefaultFlushDnsAsync(CancellationToken ct)
        {
            var psi = new ProcessStartInfo("ipconfig.exe", "/flushdns")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ipconfig não pôde ser iniciado.");
            string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, stdout);
        }

        private void InvalidateCommands()
        {
            (AnalyzeNetworkCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyNetworkPlanCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyPresetDnsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ResetDnsDhcpCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RevertNetworkCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (FlushDnsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelNetworkTaskCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? prop = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}

