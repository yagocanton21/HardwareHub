using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HardwareHub.Models;

namespace HardwareHub.Services
{
    /// <summary>
    /// Gera sugestões proativas cruzando inventário + saúde do Windows + discos, e exporta
    /// relatórios técnicos sanitizados (Resumo em texto, HTML responsivo e JSON).
    /// </summary>
    public sealed class ReportExportService
    {
        private const string ReportCss =
            ":root{--bg:#f5f6f8;--fg:#15171c;--muted:#5f6672;--line:#e1e4e9;--card:#fff;--accent:#4f46e5;--ok:#10b981;--warn:#d97706;--bad:#ef4444}" +
            "@media (prefers-color-scheme:dark){:root{--bg:#0f0f12;--fg:#f4f4f5;--muted:#a1a1aa;--line:#27272a;--card:#18181b;--accent:#818cf8;--ok:#34d399;--warn:#fbbf24;--bad:#f87171}}" +
            "*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.5 'Segoe UI',system-ui,sans-serif}" +
            ".wrap{max-width:1000px;margin:0 auto;padding:28px 16px 56px}.note{color:var(--muted);font-size:12px}" +
            "h1{margin:0;font-size:24px}h2{margin:28px 0 10px;font-size:17px;color:var(--accent)}h3{margin:0 0 8px;font-size:12px;color:var(--muted);text-transform:uppercase;letter-spacing:.5px}" +
            ".card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px 16px;margin-bottom:12px}" +
            ".grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(290px,1fr));gap:12px}.grid .card{margin:0}" +
            "table{width:100%;border-collapse:collapse}td,th{padding:6px 8px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}" +
            "th{color:var(--muted);font-size:12px}.k{color:var(--muted);width:42%}.mono{font-family:Consolas,monospace}" +
            ".badge{font-size:12px;font-weight:600;padding:2px 10px;border-radius:4px;color:#fff;background:var(--ok)}" +
            ".badge.warn{background:var(--warn)}.badge.bad{background:var(--bad)}.ok{color:var(--ok);font-weight:600;margin:0}" +
            "footer{margin-top:32px;color:var(--muted);font-size:12px}@media print{body{background:#fff;color:#000}.card{break-inside:avoid}}";

        public List<SystemSuggestionItem> EvaluateSuggestions(
            SystemInventoryReport? inventory,
            WindowsHealthReport? health,
            DiskOptimizationConfig? diskConfig = null)
        {
            var suggestions = new List<SystemSuggestionItem>();

            if (inventory != null)
            {
                foreach (var vol in inventory.Volumes.Where(v => v.IsNearlyFull && !v.IsRemovable))
                {
                    suggestions.Add(new SystemSuggestionItem
                    {
                        Id = $"low_space_{vol.DriveLetter}",
                        Severity = vol.UsagePercent >= 96 ? WindowsHealthStatus.Critical : WindowsHealthStatus.Attention,
                        Icon = "💾",
                        Title = $"Unidade {vol.DriveLetter} quase cheia ({vol.UsagePercent:0}% usado)",
                        Description = $"Restam apenas {vol.FormattedFree} livres de {vol.FormattedTotal}. Execute a limpeza de temporários e analise arquivos grandes.",
                        ActionLabel = "Abrir Limpeza & Disco",
                        NavigationTarget = "StorageTools"
                    });
                }

                foreach (var disk in inventory.Disks.Where(d => d.HealthStatus is "Atenção" or "Crítico"))
                {
                    suggestions.Add(new SystemSuggestionItem
                    {
                        Id = $"smart_{disk.Model}",
                        Severity = disk.HealthStatus == "Crítico" ? WindowsHealthStatus.Critical : WindowsHealthStatus.Attention,
                        Icon = "💿",
                        Title = $"Alerta S.M.A.R.T. no disco {disk.Model}",
                        Description = $"Estado reportado: {disk.HealthStatus}. Faça backup preventivo dos arquivos importantes.",
                        ActionLabel = "Ver Saúde dos Discos",
                        NavigationTarget = "Disks"
                    });
                }

                var xmpLine = inventory.MemoryLines.FirstOrDefault(l => l.Name.StartsWith("XMP/EXPO", StringComparison.Ordinal));
                if (xmpLine != null && xmpLine.Value.Contains("DESLIGADO", StringComparison.Ordinal))
                {
                    suggestions.Add(new SystemSuggestionItem
                    {
                        Id = "ram_xmp_off",
                        Severity = WindowsHealthStatus.Attention,
                        Icon = "⚡",
                        Title = "Memória RAM rodando abaixo da frequência nominal",
                        Description = xmpLine.Value,
                        ActionLabel = "Ver Detalhes",
                        NavigationTarget = "HealthReport"
                    });
                }
            }

            if (health != null)
            {
                if (health.ImageHealthState == "Repairable" || health.SfcFoundCorruption == true)
                {
                    suggestions.Add(new SystemSuggestionItem
                    {
                        Id = "windows_repair_needed",
                        Severity = WindowsHealthStatus.Attention,
                        Icon = "🩺",
                        Title = "Corrupção detectada na imagem ou arquivos do Windows",
                        Description = "A checagem apontou divergências no DISM/SFC. Execute DISM RestoreHealth e SFC /scannow na Central de Manutenção.",
                        ActionLabel = "Ir para Manutenção",
                        NavigationTarget = "Maintenance"
                    });
                }

                if (health.HasWmiIssue)
                {
                    suggestions.Add(new SystemSuggestionItem
                    {
                        Id = "wmi_repair_needed",
                        Severity = WindowsHealthStatus.Critical,
                        Icon = "🗄️",
                        Title = "Banco de dados WMI inconsistente",
                        Description = "O repositório WMI retornou inconsistência. Use o passo 'Verificar & Recuperar Banco WMI' na aba Reparos Extras.",
                        ActionLabel = "Reparar WMI",
                        NavigationTarget = "HealthReport"
                    });
                }

                if (health.ClockDelta.HasValue && health.ClockDelta.Value.Duration() >= WindowsHealthRepairService.ClockTolerance)
                {
                    suggestions.Add(new SystemSuggestionItem
                    {
                        Id = "clock_out_of_sync",
                        Severity = WindowsHealthStatus.Attention,
                        Icon = "🕒",
                        Title = $"Relógio do Windows {WindowsHealthRepairService.DescribeClockDelta(health.ClockDelta.Value)}",
                        Description = "Horário incorreto quebra HTTPS/SSL, Windows Update e ativação. Sincronize na aba Reparos Extras.",
                        ActionLabel = "Sincronizar Hora",
                        NavigationTarget = "HealthReport"
                    });
                }

                if (health.PendingReboot)
                {
                    suggestions.Add(new SystemSuggestionItem
                    {
                        Id = "pending_reboot",
                        Severity = WindowsHealthStatus.Attention,
                        Icon = "🔄",
                        Title = "Reinicialização do Windows pendente",
                        Description = "Existem atualizações aguardando reinício completo para finalizar instalação.",
                        ActionLabel = "Ver Drivers & Update",
                        NavigationTarget = "DriversUpdate"
                    });
                }
            }

            if (diskConfig != null && !diskConfig.TrimEnabled)
            {
                suggestions.Add(new SystemSuggestionItem
                {
                    Id = "trim_disabled",
                    Severity = WindowsHealthStatus.Attention,
                    Icon = "⚡",
                    Title = "TRIM automático do Windows desativado",
                    Description = "Ativar DisableDeleteNotification=0 preserva desempenho e vida útil de SSDs NVMe/SATA.",
                    ActionLabel = "Otimizar Discos",
                    NavigationTarget = "DiskOptimization"
                });
            }

            if (suggestions.Count == 0 && (inventory != null || health != null))
            {
                suggestions.Add(new SystemSuggestionItem
                {
                    Id = "all_good",
                    Severity = WindowsHealthStatus.Healthy,
                    Icon = "✅",
                    Title = "Nenhum ponto crítico encontrado na última análise",
                    Description = "Armazenamento, saúde do sistema e memória sem alertas imediatos.",
                    ActionLabel = "Exportar Relatório",
                    NavigationTarget = "HealthReport"
                });
            }

            return suggestions;
        }

        public List<string> CollectProblems(SystemInventoryReport inventory, WindowsHealthReport? health = null)
        {
            return EvaluateSuggestions(inventory, health)
                .Where(s => s.Severity is WindowsHealthStatus.Attention or WindowsHealthStatus.Critical)
                .Select(s => $"{s.Title}: {s.Description}")
                .ToList();
        }

        public string BuildTextSummary(SystemInventoryReport inventory, WindowsHealthReport? health = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Resumo Técnico do PC (Hardware HUB)");
            string build = inventory.SystemLines.FirstOrDefault(l => l.Name == "Build")?.Value ?? "";
            string uptime = inventory.SystemLines.FirstOrDefault(l => l.Name == "Ligado há")?.Value ?? "";
            sb.AppendLine($"Windows: {inventory.SystemSummary}" +
                          (build.Length > 0 ? $" (build {build})" : "") +
                          (uptime.Length > 0 ? $" · ligado há {uptime}" : ""));
            sb.AppendLine($"Processador: {inventory.CpuSummary}");

            string channels = inventory.MemoryLines.FirstOrDefault(l => l.Name.StartsWith("Canais", StringComparison.Ordinal))?.Value ?? "";
            sb.AppendLine($"Memória: {inventory.MemorySummary}" +
                          (channels.StartsWith("Dual", StringComparison.OrdinalIgnoreCase) ? " · dual channel" : ""));

            if (inventory.VideoAdapters.Count > 0)
                sb.AppendLine("Placa de vídeo: " + string.Join(" + ", inventory.VideoAdapters.Select(g => $"{g.Name} ({g.FormattedVram})")));

            if (inventory.MotherboardSummary != "—")
                sb.AppendLine($"Placa-mãe: {inventory.MotherboardSummary}");

            if (inventory.Disks.Count > 0)
                sb.AppendLine("Discos S.M.A.R.T.: " + string.Join(" · ", inventory.Disks.Select(d => $"{d.Model} ({d.HealthStatus})")));

            var internalVols = inventory.Volumes.Where(v => v.Kind == "Interno").Take(4).ToList();
            if (internalVols.Count > 0)
                sb.AppendLine("Espaço livre: " + string.Join(" · ", internalVols.Select(v => $"{v.DriveLetter} {v.FormattedFree} / {v.FormattedTotal}")));

            if (health != null)
                sb.AppendLine($"Saúde do Windows: {health.SummaryMessage}");

            var problems = CollectProblems(inventory, health);
            sb.AppendLine("Alertas: " + (problems.Count == 0 ? "nenhum" : string.Join("; ", problems.Take(6))));

            return PrivacySanitizer.Sanitize(sb.ToString().TrimEnd());
        }

        public string BuildJsonReport(SystemInventoryReport inventory, WindowsHealthReport? health = null)
        {
            var payload = new
            {
                gerado_por = "Hardware HUB",
                gerado_em = inventory.CollectedAt.ToString("s"),
                privacidade = "Privacidade: nome de usuário, nome do computador e diretórios pessoais foram anonimizados.",
                alertas = CollectProblems(inventory, health),
                sistema = new
                {
                    resumo = inventory.SystemSummary,
                    detalhes = inventory.SystemLines
                        .Where(l => !l.Name.Equals("Nome do computador", StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(l => l.Name, l => l.Value)
                },
                placa_mae = new { resumo = inventory.MotherboardSummary, detalhes = ToSafeDictionary(inventory.MotherboardLines) },
                processador = new { resumo = inventory.CpuSummary, detalhes = ToSafeDictionary(inventory.CpuLines) },
                memoria = new
                {
                    resumo = inventory.MemorySummary,
                    detalhes = ToSafeDictionary(inventory.MemoryLines),
                    modulos = inventory.RamModules.Select(m => new
                    {
                        slot = m.Slot,
                        fabricante = m.Manufacturer,
                        part_number = m.PartNumber,
                        capacidade = m.FormattedCapacity,
                        tipo = m.Type,
                        formato = m.FormFactor,
                        velocidade = m.FormattedSpeed
                    })
                },
                placas_video = inventory.VideoAdapters.Select(g => new { nome = g.Name, vram = g.FormattedVram, driver = g.DriverVersion }),
                unidades = inventory.Volumes.Select(v => new { unidade = v.Title, tipo = v.Kind, livre = v.FormattedFree, total = v.FormattedTotal, uso_pct = Math.Round(v.UsagePercent, 1) }),
                saude_windows = health == null ? null : new
                {
                    checagem_completa = health.IsFullScan,
                    resumo = health.SummaryMessage,
                    imagem_dism = health.ImageHealthState,
                    wmi = health.WmiStatus,
                    sfc_corrompido = health.SfcFoundCorruption,
                    reinicio_pendente = health.PendingReboot,
                    itens = health.Items.Select(i => new { i.Title, i.StatusText, i.Detail })
                }
            };

            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            return PrivacySanitizer.Sanitize(json);
        }

        public string BuildHtmlReport(SystemInventoryReport inventory, WindowsHealthReport? health = null)
        {
            var sb = new StringBuilder(32768);
            sb.Append("<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append($"<title>Hardware HUB · Relatório Técnico · {inventory.CollectedAt:dd/MM/yyyy}</title><style>{ReportCss}</style></head><body><div class=\"wrap\">");
            sb.Append($"<h1>Hardware HUB · Relatório do PC</h1><p class=\"note\">Gerado em {inventory.CollectedAt:dd/MM/yyyy HH:mm} · Nome de usuário, nome do computador e caminhos pessoais anonimizados.</p>");

            var problems = CollectProblems(inventory, health);
            sb.Append("<section><h2>Alertas & Diagnóstico</h2><div class=\"card\">");
            if (problems.Count == 0)
            {
                sb.Append("<p class=\"ok\">Nenhum problema crítico detectado.</p>");
            }
            else
            {
                sb.Append("<ul>");
                foreach (string p in problems) sb.Append($"<li>{Encode(p)}</li>");
                sb.Append("</ul>");
            }
            sb.Append("</div></section>");

            sb.Append("<section><h2>Hardware & Sistema</h2><div class=\"grid\">");
            AppendCard(sb, "Sistema", inventory.SystemSummary, inventory.SystemLines.Where(l => l.Name != "Nome do computador"));
            AppendCard(sb, "Placa-mãe & BIOS", inventory.MotherboardSummary, inventory.MotherboardLines);
            AppendCard(sb, "Processador", inventory.CpuSummary, inventory.CpuLines);
            foreach (var gpu in inventory.VideoAdapters)
                AppendCard(sb, "Placa de Vídeo", gpu.Name, gpu.Lines);
            sb.Append("</div></section>");

            sb.Append("<section><h2>Memória RAM</h2>");
            AppendCard(sb, "Resumo", inventory.MemorySummary, inventory.MemoryLines);
            if (inventory.RamModules.Count > 0)
            {
                sb.Append("<div class=\"card\"><h3>Pentes Instalados</h3><table><tr><th>Slot</th><th>Fabricante</th><th>Part Number</th><th>Capacidade</th><th>Tipo</th><th>Velocidade</th></tr>");
                foreach (var m in inventory.RamModules)
                {
                    sb.Append($"<tr><td>{Encode(m.Slot)}</td><td>{Encode(m.Manufacturer)}</td><td class=\"mono\">{Encode(m.PartNumber)}</td><td>{Encode(m.FormattedCapacity)}</td><td>{Encode(m.Type)}</td><td>{Encode(m.FormattedSpeed)}</td></tr>");
                }
                sb.Append("</table></div>");
            }
            sb.Append("</section>");

            if (health != null && health.Items.Count > 0)
            {
                sb.Append("<section><h2>Saúde do Windows</h2><div class=\"card\"><table><tr><th>Verificação</th><th>Status</th><th>Detalhe</th></tr>");
                foreach (var item in health.Items)
                {
                    string cls = item.Status switch
                    {
                        WindowsHealthStatus.Critical => "bad",
                        WindowsHealthStatus.Attention => "warn",
                        _ => ""
                    };
                    sb.Append($"<tr><td><b>{Encode(item.Title)}</b></td><td><span class=\"badge {cls}\">{Encode(item.StatusText)}</span></td><td>{Encode(item.Detail)}</td></tr>");
                }
                sb.Append("</table></div></section>");
            }

            if (inventory.Volumes.Count > 0)
            {
                sb.Append("<section><h2>Unidades de Armazenamento</h2><div class=\"card\"><table><tr><th>Unidade</th><th>Tipo</th><th>Disco</th><th>Livre</th><th>Total</th><th>Uso</th></tr>");
                foreach (var v in inventory.Volumes)
                {
                    sb.Append($"<tr><td><b>{Encode(v.Title)}</b></td><td>{Encode(v.Kind)} ({Encode(v.FileSystem)})</td><td>{Encode(v.DiskModel)}</td><td>{Encode(v.FormattedFree)}</td><td>{Encode(v.FormattedTotal)}</td><td>{v.UsagePercent:0}%</td></tr>");
                }
                sb.Append("</table></div></section>");
            }

            sb.Append("<footer>Gerado localmente pelo Hardware HUB.</footer></div></body></html>");
            return sb.ToString();
        }

        public void SaveReportToFile(SystemInventoryReport inventory, string filePath, WindowsHealthReport? health = null)
        {
            bool isJson = string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase);
            bool isTxt = string.Equals(Path.GetExtension(filePath), ".txt", StringComparison.OrdinalIgnoreCase);

            string content = isJson
                ? BuildJsonReport(inventory, health)
                : isTxt
                    ? BuildTextSummary(inventory, health)
                    : BuildHtmlReport(inventory, health);

            File.WriteAllText(filePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        private static Dictionary<string, string> ToSafeDictionary(IEnumerable<PcInfoLine> lines)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines) dict.TryAdd(l.Name, l.Value);
            return dict;
        }

        private static string Encode(string? text)
            => WebUtility.HtmlEncode(PrivacySanitizer.Sanitize(text ?? string.Empty));

        private static void AppendCard(StringBuilder sb, string title, string bigSummary, IEnumerable<PcInfoLine> lines)
        {
            sb.Append($"<div class=\"card\"><h3>{Encode(title)}</h3>");
            if (!string.IsNullOrWhiteSpace(bigSummary) && bigSummary != "—")
                sb.Append($"<p><b>{Encode(bigSummary)}</b></p>");
            sb.Append("<table>");
            foreach (var l in lines)
                sb.Append($"<tr><td class=\"k\">{Encode(l.Name)}</td><td>{Encode(l.Value)}</td></tr>");
            sb.Append("</table></div>");
        }
    }
}
