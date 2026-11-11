using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using HardwareHub.Models;

namespace HardwareHub.Services
{
    public sealed class BatteryDiagnosticsService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        public static (int? ChargePercent, bool? OnAcPower, bool NoBattery) ReadLivePowerStatus()
        {
            if (!GetSystemPowerStatus(out var st))
            {
                return (null, null, false);
            }

            bool noBattery = (st.BatteryFlag & 128) != 0 || st.BatteryFlag == 255;
            int? charge = st.BatteryLifePercent <= 100 ? st.BatteryLifePercent : null;
            bool? ac = st.ACLineStatus switch
            {
                0 => false,
                1 => true,
                _ => null
            };
            return (charge, ac, noBattery);
        }

        public async Task<BatteryInfoReport> ReadBatteryReportAsync(CancellationToken ct = default)
        {
            var (charge, ac, noBattery) = ReadLivePowerStatus();
            if (noBattery)
            {
                return new BatteryInfoReport { HasBattery = false };
            }

            string tempXml = Path.Combine(Path.GetTempPath(), $"hardwarehub_battery_{Guid.NewGuid():N}.xml");
            try
            {
                var psi = new ProcessStartInfo("powercfg.exe", $"/batteryreport /xml /output \"{tempXml}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return new BatteryInfoReport
                    {
                        HasBattery = true,
                        ChargePercent = charge,
                        OnAcPower = ac,
                        ErrorMessage = "Não foi possível executar powercfg.exe."
                    };
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(30000);
                await proc.WaitForExitAsync(timeoutCts.Token);

                if (!File.Exists(tempXml))
                {
                    return new BatteryInfoReport
                    {
                        HasBattery = true,
                        ChargePercent = charge,
                        OnAcPower = ac,
                        ErrorMessage = "O Windows não gerou o relatório XML da bateria."
                    };
                }

                var doc = XDocument.Load(tempXml);
                return ParseBatteryReportXml(doc) with
                {
                    ChargePercent = charge,
                    OnAcPower = ac
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new BatteryInfoReport
                {
                    HasBattery = true,
                    ChargePercent = charge,
                    OnAcPower = ac,
                    ErrorMessage = "Tempo esgotado ao gerar relatório de bateria."
                };
            }
            catch (Exception ex)
            {
                return new BatteryInfoReport
                {
                    HasBattery = !noBattery,
                    ChargePercent = charge,
                    OnAcPower = ac,
                    ErrorMessage = ex.Message
                };
            }
            finally
            {
                try { if (File.Exists(tempXml)) File.Delete(tempXml); } catch { }
            }
        }

        public static BatteryInfoReport ParseBatteryReportXml(XDocument doc)
        {
            XElement? root = doc.Root;
            var batteries = Child(root, "Batteries")?
                .Elements()
                .Where(e => e.Name.LocalName == "Battery")
                .ToList() ?? new List<XElement>();

            if (batteries.Count == 0)
            {
                return new BatteryInfoReport { HasBattery = false };
            }

            long designTotal = 0;
            long fullTotal = 0;
            int cycleTotal = 0;
            foreach (var b in batteries)
            {
                designTotal += ParseInt64(Text(b, "DesignCapacity"));
                fullTotal += ParseInt64(Text(b, "FullChargeCapacity"));
                cycleTotal += (int)ParseInt64(Text(b, "CycleCount"));
            }

            var history = new List<BatteryHistoryPoint>();
            foreach (var entry in Child(root, "History")?.Elements().Where(e => e.Name.LocalName == "HistoryEntry") ?? Enumerable.Empty<XElement>())
            {
                long full = ParseInt64(entry.Attribute("FullChargeCapacity")?.Value);
                long design = ParseInt64(entry.Attribute("DesignCapacity")?.Value);
                if (full > 0 && design > 0 &&
                    DateTime.TryParse(entry.Attribute("LocalStartDate")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                {
                    history.Add(new BatteryHistoryPoint(dt, full, design));
                }
            }

            DateTime? scannedAt = DateTime.TryParse(
                Text(Child(root, "ReportInformation"), "LocalScanTime"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var scanDt)
                ? scanDt
                : null;

            return new BatteryInfoReport
            {
                HasBattery = true,
                BatteryCount = batteries.Count,
                Name = string.Join(" + ", batteries.Select(b => Text(b, "Id")).Where(s => s.Length > 0).Distinct()),
                Manufacturer = string.Join(" + ", batteries.Select(b => Text(b, "Manufacturer")).Where(s => s.Length > 0).Distinct()),
                Chemistry = string.Join(" + ", batteries.Select(b => MapChemistry(Text(b, "Chemistry"))).Where(s => s.Length > 0).Distinct()),
                DesignCapacityMwh = designTotal,
                FullChargeCapacityMwh = fullTotal,
                CycleCount = cycleTotal > 0 ? cycleTotal : null,
                EstimatedRuntimeCurrent = ParseDuration(Child(root, "RuntimeEstimates"), "FullChargeCapacity"),
                EstimatedRuntimeOriginal = ParseDuration(Child(root, "RuntimeEstimates"), "DesignCapacity"),
                History = history.OrderBy(h => h.Date).ToList(),
                ScannedAt = scannedAt
            };
        }

        private static XElement? Child(XElement? el, string name) =>
            el?.Elements().FirstOrDefault(x => x.Name.LocalName == name);

        private static string Text(XElement? el, string childName) =>
            Child(el, childName)?.Value.Trim() ?? "";

        private static long ParseInt64(string? text) =>
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long val) ? val : 0;

        private static TimeSpan? ParseDuration(XElement? runtimeEl, string qual)
        {
            string val = Text(Child(runtimeEl, qual), "ActiveRuntime");
            if (string.IsNullOrWhiteSpace(val)) return null;
            try
            {
                TimeSpan ts = XmlConvert.ToTimeSpan(val);
                return ts > TimeSpan.FromMinutes(1) ? ts : null;
            }
            catch
            {
                return null;
            }
        }

        private static string MapChemistry(string code) => code.Trim().ToUpperInvariant() switch
        {
            "" => "",
            "LION" or "LI-I" or "LI-ION" => "Íon-Lítio (Li-Ion)",
            "LIP" or "LI-P" or "LIPO" => "Polímero de Lítio (Li-Po)",
            "NIMH" => "Níquel-Hidreto Metálico (NiMH)",
            "NICD" => "Níquel-Cádmio (NiCd)",
            _ => code
        };
    }
}

