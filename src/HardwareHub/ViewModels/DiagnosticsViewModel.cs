using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HardwareHub.Models;
using HardwareHub.Services;

namespace HardwareHub.ViewModels
{
    public sealed record DisplayTestColor(string Name, string Hex);

    public sealed class DiagnosticsViewModel : INotifyPropertyChanged
    {
        private readonly BatteryDiagnosticsService _batteryService;
        private readonly Action<string> _log;

        private string _activeSubTab = "Battery"; // "Battery" | "Peripherals" | "Display"
        private bool _isBusyBattery;
        private BatteryInfoReport? _batteryReport;
        private string _batteryStatusBanner = "Clique em \"Atualizar Relatório\" para analisar saúde, ciclos e desgaste da bateria.";
        private bool _batteryLoadedOnce;

        // Keyboard ABNT2 state
        private readonly Dictionary<string, KeyboardKeyVisualItem> _keyMap = new(StringComparer.OrdinalIgnoreCase);
        private string _lastPressedKeyText = "Pressione qualquer tecla abaixo para testar (clique no painel do teclado)...";

        // Mouse test state
        private int _leftClicks;
        private int _rightClicks;
        private int _middleClicks;
        private int _x1Clicks;
        private int _x2Clicks;
        private int _wheelUpTicks;
        private int _wheelDownTicks;
        private long _lastMoveTicks;
        private int _currentPollingHz;
        private int _peakPollingHz;
        private string _lastMouseEventText = "Clique ou role o scroll dentro da área de teste abaixo.";

        // Display dead pixel test state
        private int _currentPixelColorIndex;

        public static readonly DisplayTestColor[] DisplayTestColors =
        {
            new("Preto", "#000000"),
            new("Branco", "#FFFFFF"),
            new("Vermelho", "#EF4444"),
            new("Verde", "#22C55E"),
            new("Azul", "#3B82F6"),
            new("Amarelo", "#FACC15"),
            new("Magenta", "#EC4899"),
            new("Ciano", "#06B6D4")
        };

        public ObservableCollection<BatteryHistoryPoint> BatteryHistory { get; } = new();
        public ObservableCollection<ObservableCollection<KeyboardKeyVisualItem>> KeyboardRows { get; } = new();

        public DiagnosticsViewModel(
            BatteryDiagnosticsService? batteryService = null,
            Action<string>? logCallback = null)
        {
            _batteryService = batteryService ?? new BatteryDiagnosticsService();
            _log = logCallback ?? (_ => { });

            BuildAbnt2KeyboardLayout();

            SetSubTabCommand = new RelayCommand(p => ActiveSubTab = p?.ToString() ?? "Battery");
            RefreshBatteryCommand = new RelayCommand(_ => _ = RefreshBatteryAsync(), _ => !IsBusyBattery);
            ResetKeyboardCommand = new RelayCommand(_ => ResetKeyboardTest());
            ResetMouseCommand = new RelayCommand(_ => ResetMouseTest());
            NextPixelColorCommand = new RelayCommand(_ => NextPixelColor());
            PrevPixelColorCommand = new RelayCommand(_ => PrevPixelColor());
            SelectPixelColorCommand = new RelayCommand(p =>
            {
                if (p is int idx)
                    CurrentPixelColorIndex = ((idx % DisplayTestColors.Length) + DisplayTestColors.Length) % DisplayTestColors.Length;
                else if (int.TryParse(p?.ToString(), out var parsed))
                    CurrentPixelColorIndex = ((parsed % DisplayTestColors.Length) + DisplayTestColors.Length) % DisplayTestColors.Length;
            });
        }

        public string ActiveSubTab
        {
            get => _activeSubTab;
            set
            {
                if (SetField(ref _activeSubTab, value))
                {
                    OnPropertyChanged(nameof(IsBatteryTabActive));
                    OnPropertyChanged(nameof(IsPeripheralsTabActive));
                    OnPropertyChanged(nameof(IsDisplayTabActive));
                    if (value == "Battery")
                        EnsureBatteryLoaded();
                }
            }
        }

        public bool IsBatteryTabActive => ActiveSubTab == "Battery";
        public bool IsPeripheralsTabActive => ActiveSubTab == "Peripherals";
        public bool IsDisplayTabActive => ActiveSubTab == "Display";

        // ---- BATTERY PROPERTIES ----

        public bool IsBusyBattery
        {
            get => _isBusyBattery;
            private set
            {
                if (SetField(ref _isBusyBattery, value))
                    (RefreshBatteryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public string BatteryStatusBanner
        {
            get => _batteryStatusBanner;
            private set => SetField(ref _batteryStatusBanner, value);
        }

        public BatteryInfoReport? BatteryReport
        {
            get => _batteryReport;
            private set
            {
                if (SetField(ref _batteryReport, value))
                    NotifyBatteryComputedProperties();
            }
        }

        public bool HasBatteryData => BatteryReport != null && BatteryReport.HasBattery;

        public string BatteryHealthText => BatteryReport switch
        {
            null => "—",
            { HasBattery: false } => "Sem bateria",
            _ => $"{BatteryReport.HealthPercent:0.0}%"
        };

        public string BatteryWearText => BatteryReport switch
        {
            null or { HasBattery: false } => "—",
            _ => $"{BatteryReport.WearPercent:0.0}%"
        };

        public string BatteryDesignCapacityText => BatteryReport?.DesignCapacityMwh > 0
            ? $"{BatteryReport.DesignCapacityMwh:N0} mWh"
            : "—";

        public string BatteryFullChargeText => BatteryReport?.FullChargeCapacityMwh > 0
            ? $"{BatteryReport.FullChargeCapacityMwh:N0} mWh"
            : "—";

        public string BatteryCycleCountText => BatteryReport?.CycleCount is int cycles
            ? cycles.ToString("N0")
            : "Indisponível";

        public string BatteryLiveStatusText
        {
            get
            {
                if (BatteryReport == null) return "Aguardando leitura";
                if (!BatteryReport.HasBattery) return "Alimentação AC / Sem bateria";
                string pct = BatteryReport.ChargePercent is int c ? $"{c}%" : "N/D";
                string ac = BatteryReport.OnAcPower switch
                {
                    true => "Conectado na tomada",
                    false => "Usando bateria",
                    null => "Energia desconhecida"
                };
                return $"{pct} • {ac}";
            }
        }

        public string BatteryEstimatedRuntimeText
        {
            get
            {
                if (BatteryReport?.EstimatedRuntimeCurrent is TimeSpan cur)
                {
                    string curFmt = $"{(int)cur.TotalHours}h {cur.Minutes:00}min";
                    if (BatteryReport.EstimatedRuntimeOriginal is TimeSpan orig)
                        return $"{curFmt} (original ~{(int)orig.TotalHours}h {orig.Minutes:00}min)";
                    return curFmt;
                }
                return "Disponível após ciclos na bateria";
            }
        }

        public string BatteryMetaText => BatteryReport != null && BatteryReport.HasBattery
            ? $"{BatteryReport.Name} • {BatteryReport.Manufacturer} ({BatteryReport.Chemistry})"
            : "Compatível com notebooks Windows via powercfg /batteryreport";

        public string BatteryVerdictText => BatteryReport?.HealthVerdict ?? "Não analisado";
        public string BatteryHealthColor => BatteryReport?.HealthColor ?? "#71717A";

        // ---- KEYBOARD PROPERTIES ----

        public int TotalKeysCount => _keyMap.Values.Distinct().Count();
        public int TestedKeysCount => _keyMap.Values.Distinct().Count(k => k.WasTested);

        public string KeyboardProgressText => $"Teclas testadas: {TestedKeysCount} / {TotalKeysCount}";

        public string LastPressedKeyText
        {
            get => _lastPressedKeyText;
            private set => SetField(ref _lastPressedKeyText, value);
        }

        // ---- MOUSE PROPERTIES ----

        public int LeftClicks
        {
            get => _leftClicks;
            private set => SetField(ref _leftClicks, value);
        }

        public int RightClicks
        {
            get => _rightClicks;
            private set => SetField(ref _rightClicks, value);
        }

        public int MiddleClicks
        {
            get => _middleClicks;
            private set => SetField(ref _middleClicks, value);
        }

        public int X1Clicks
        {
            get => _x1Clicks;
            private set => SetField(ref _x1Clicks, value);
        }

        public int X2Clicks
        {
            get => _x2Clicks;
            private set => SetField(ref _x2Clicks, value);
        }

        public int WheelUpTicks
        {
            get => _wheelUpTicks;
            private set => SetField(ref _wheelUpTicks, value);
        }

        public int WheelDownTicks
        {
            get => _wheelDownTicks;
            private set => SetField(ref _wheelDownTicks, value);
        }

        public int CurrentPollingHz
        {
            get => _currentPollingHz;
            private set => SetField(ref _currentPollingHz, value);
        }

        public int PeakPollingHz
        {
            get => _peakPollingHz;
            private set => SetField(ref _peakPollingHz, value);
        }

        public string LastMouseEventText
        {
            get => _lastMouseEventText;
            private set => SetField(ref _lastMouseEventText, value);
        }

        // ---- DISPLAY TEST PROPERTIES ----

        public int CurrentPixelColorIndex
        {
            get => _currentPixelColorIndex;
            private set
            {
                if (SetField(ref _currentPixelColorIndex, value))
                {
                    OnPropertyChanged(nameof(CurrentPixelColorHex));
                    OnPropertyChanged(nameof(CurrentPixelColorName));
                    OnPropertyChanged(nameof(DisplayColorCaption));
                }
            }
        }

        public DisplayTestColor CurrentDisplayColor => DisplayTestColors[_currentPixelColorIndex];
        public string CurrentPixelColorHex => CurrentDisplayColor.Hex;
        public string CurrentPixelColorName => CurrentDisplayColor.Name;
        public string DisplayColorCaption => $"Cor atual ({_currentPixelColorIndex + 1}/{DisplayTestColors.Length}): {CurrentPixelColorName} — clique na tela cheia para avançar ou ESC para sair";

        public ICommand SetSubTabCommand { get; }
        public ICommand RefreshBatteryCommand { get; }
        public ICommand ResetKeyboardCommand { get; }
        public ICommand ResetMouseCommand { get; }
        public ICommand NextPixelColorCommand { get; }
        public ICommand PrevPixelColorCommand { get; }
        public ICommand SelectPixelColorCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void EnsureBatteryLoaded()
        {
            if (!_batteryLoadedOnce && !IsBusyBattery)
            {
                _batteryLoadedOnce = true;
                _ = RefreshBatteryAsync();
            }
        }

        public async Task RefreshBatteryAsync()
        {
            if (IsBusyBattery)
                return;

            IsBusyBattery = true;
            BatteryStatusBanner = "Coletando diagnóstico de bateria via powercfg /batteryreport...";

            try
            {
                var report = await _batteryService.ReadBatteryReportAsync();
                BatteryReport = report;

                BatteryHistory.Clear();
                foreach (var pt in report.History)
                    BatteryHistory.Add(pt);

                BatteryStatusBanner = !string.IsNullOrWhiteSpace(report.ErrorMessage)
                    ? $"Aviso no relatório: {report.ErrorMessage}"
                    : (report.HasBattery
                        ? $"Saúde da bateria: {report.HealthPercent:0.0}% ({report.FullChargeCapacityMwh:N0}/{report.DesignCapacityMwh:N0} mWh)"
                        : report.HealthVerdict);

                _log($"[BATERIA] {BatteryStatusBanner}");
            }
            finally
            {
                IsBusyBattery = false;
            }
        }

        public bool HandleKeyEvent(string wpfKeyName, bool isDown, string? friendlyText = null)
        {
            if (string.IsNullOrWhiteSpace(wpfKeyName))
                return false;

            if (_keyMap.TryGetValue(wpfKeyName, out var visual))
            {
                visual.IsPressed = isDown;
                if (isDown)
                {
                    visual.WasTested = true;
                    LastPressedKeyText = $"Última tecla: {visual.Label} ({wpfKeyName})";
                    OnPropertyChanged(nameof(TestedKeysCount));
                    OnPropertyChanged(nameof(KeyboardProgressText));
                }
                return true;
            }

            if (isDown)
                LastPressedKeyText = $"Tecla detectada: {friendlyText ?? wpfKeyName}";
            return false;
        }

        public void ResetKeyboardTest()
        {
            foreach (var key in _keyMap.Values.Distinct())
            {
                key.IsPressed = false;
                key.WasTested = false;
            }
            LastPressedKeyText = "Teclado resetado. Pressione as teclas para validar cada uma.";
            OnPropertyChanged(nameof(TestedKeysCount));
            OnPropertyChanged(nameof(KeyboardProgressText));
        }

        public void RecordMouseButton(string buttonName)
        {
            switch (buttonName)
            {
                case "Left":
                    LeftClicks++;
                    LastMouseEventText = $"Botão Esquerdo ({LeftClicks}x)";
                    break;
                case "Right":
                    RightClicks++;
                    LastMouseEventText = $"Botão Direito ({RightClicks}x)";
                    break;
                case "Middle":
                    MiddleClicks++;
                    LastMouseEventText = $"Botão do Meio / Scroll ({MiddleClicks}x)";
                    break;
                case "XButton1":
                    X1Clicks++;
                    LastMouseEventText = $"Botão Lateral Voltar X1 ({X1Clicks}x)";
                    break;
                case "XButton2":
                    X2Clicks++;
                    LastMouseEventText = $"Botão Lateral Avançar X2 ({X2Clicks}x)";
                    break;
            }
        }

        public void RecordMouseWheel(int delta)
        {
            if (delta > 0)
            {
                WheelUpTicks++;
                LastMouseEventText = $"Scroll para Cima (+{delta})";
            }
            else if (delta < 0)
            {
                WheelDownTicks++;
                LastMouseEventText = $"Scroll para Baixo ({delta})";
            }
        }

        public void RecordMouseMove(long currentTimestampTicks)
        {
            if (_lastMoveTicks > 0 && currentTimestampTicks > _lastMoveTicks)
            {
                double elapsedSeconds = (currentTimestampTicks - _lastMoveTicks) / (double)Stopwatch.Frequency;
                if (elapsedSeconds >= 0.0005 && elapsedSeconds <= 0.2)
                {
                    int hz = (int)Math.Clamp(Math.Round(1.0 / elapsedSeconds), 1, 8000);
                    CurrentPollingHz = hz;
                    if (hz > PeakPollingHz)
                        PeakPollingHz = hz;
                }
            }
            _lastMoveTicks = currentTimestampTicks;
        }

        public void ResetMouseTest()
        {
            LeftClicks = 0;
            RightClicks = 0;
            MiddleClicks = 0;
            X1Clicks = 0;
            X2Clicks = 0;
            WheelUpTicks = 0;
            WheelDownTicks = 0;
            CurrentPollingHz = 0;
            PeakPollingHz = 0;
            _lastMoveTicks = 0;
            LastMouseEventText = "Contadores do mouse zerados.";
        }

        public void NextPixelColor()
        {
            CurrentPixelColorIndex = (CurrentPixelColorIndex + 1) % DisplayTestColors.Length;
        }

        public void PrevPixelColor()
        {
            CurrentPixelColorIndex = (CurrentPixelColorIndex - 1 + DisplayTestColors.Length) % DisplayTestColors.Length;
        }

        private void NotifyBatteryComputedProperties()
        {
            OnPropertyChanged(nameof(HasBatteryData));
            OnPropertyChanged(nameof(BatteryHealthText));
            OnPropertyChanged(nameof(BatteryWearText));
            OnPropertyChanged(nameof(BatteryDesignCapacityText));
            OnPropertyChanged(nameof(BatteryFullChargeText));
            OnPropertyChanged(nameof(BatteryCycleCountText));
            OnPropertyChanged(nameof(BatteryLiveStatusText));
            OnPropertyChanged(nameof(BatteryEstimatedRuntimeText));
            OnPropertyChanged(nameof(BatteryMetaText));
            OnPropertyChanged(nameof(BatteryVerdictText));
            OnPropertyChanged(nameof(BatteryHealthColor));
        }

        private void BuildAbnt2KeyboardLayout()
        {
            KeyboardRows.Clear();
            _keyMap.Clear();

            AddRow(
                K("Escape", "Esc", 1.15),
                K("F1", "F1"), K("F2", "F2"), K("F3", "F3"), K("F4", "F4"),
                K("F5", "F5"), K("F6", "F6"), K("F7", "F7"), K("F8", "F8"),
                K("F9", "F9"), K("F10", "F10"), K("F11", "F11"), K("F12", "F12"),
                K("Delete", "Del", 1.25));

            AddRow(
                K("Oem3", "'\"", 1.0, "OemTilde"),
                K("D1", "1"), K("D2", "2"), K("D3", "3"), K("D4", "4"),
                K("D5", "5"), K("D6", "6"), K("D7", "7"), K("D8", "8"),
                K("D9", "9"), K("D0", "0"),
                K("OemMinus", "-_"), K("OemPlus", "=+"),
                K("Back", "Bksp", 1.9));

            AddRow(
                K("Tab", "Tab", 1.45),
                K("Q", "Q"), K("W", "W"), K("E", "E"), K("R", "R"), K("T", "T"),
                K("Y", "Y"), K("U", "U"), K("I", "I"), K("O", "O"), K("P", "P"),
                K("OemOpenBrackets", "´`", 1.0, "Oem1"),
                K("Oem6", "[{", 1.0, "OemCloseBrackets"),
                K("Return", "Enter", 1.45, "Enter"));

            AddRow(
                K("Capital", "Caps", 1.7, "CapsLock"),
                K("A", "A"), K("S", "S"), K("D", "D"), K("F", "F"), K("G", "G"),
                K("H", "H"), K("J", "J"), K("K", "K"), K("L", "L"),
                K("Oem3_Cedilha", "Ç", 1.0, "OemSemicolon", "Oem1"),
                K("OemQuotes", "~^", 1.0, "Oem7"),
                K("OemBackslash", "]}", 1.0, "Oem5", "OemPipe"),
                K("Home", "Home", 1.2));

            AddRow(
                K("LeftShift", "Shift", 1.4),
                K("Oem102", "\\|"),
                K("Z", "Z"), K("X", "X"), K("C", "C"), K("V", "V"), K("B", "B"),
                K("N", "N"), K("M", "M"),
                K("OemComma", ",<"), K("OemPeriod", ".>"),
                K("OemQuestion", ";:", 1.0, "Oem2"),
                K("AbntC1", "/?"),
                K("RightShift", "Shift", 1.5),
                K("Up", "↑", 1.0));

            AddRow(
                K("LeftCtrl", "Ctrl", 1.3),
                K("LWin", "Win", 1.15, "RWin"),
                K("LeftAlt", "Alt", 1.15),
                K("Space", "Espaço", 5.2),
                K("RightAlt", "AltGr", 1.25),
                K("Apps", "Menu", 1.1),
                K("RightCtrl", "Ctrl", 1.3),
                K("Left", "←"), K("Down", "↓"), K("Right", "→"));
        }

        private void AddRow(params KeyboardKeyVisualItem[] keys)
        {
            var row = new ObservableCollection<KeyboardKeyVisualItem>();
            foreach (var key in keys)
                row.Add(key);
            KeyboardRows.Add(row);
        }

        private KeyboardKeyVisualItem K(string primaryId, string label, double width = 1.0, params string[] aliases)
        {
            var item = new KeyboardKeyVisualItem
            {
                KeyId = primaryId,
                Label = label,
                WidthUnits = width
            };
            _keyMap[primaryId] = item;
            foreach (var alias in aliases)
            {
                if (!_keyMap.ContainsKey(alias))
                    _keyMap[alias] = item;
            }
            return item;
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

