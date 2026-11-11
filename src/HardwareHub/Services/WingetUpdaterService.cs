using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HardwareHub.Models;

namespace HardwareHub.Services
{
    public sealed class WingetUpdaterService
    {
        private static readonly Regex ValidWingetIdRegex = new(@"^([\w+-]+(\.[\w.+-]+)+|[0-9A-Z]{12})$", RegexOptions.Compiled);
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly string _ignoredPath;
        private readonly object _gate = new();
        private HashSet<string>? _ignoredIds;

        public static string DefaultIgnoredPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HardwareHub",
            "ignored-updates.json");

        public WingetUpdaterService(string? ignoredPath = null)
        {
            _ignoredPath = ignoredPath ?? DefaultIgnoredPath;
        }

        public static bool IsValidWingetId(string? id) =>
            !string.IsNullOrWhiteSpace(id) && ValidWingetIdRegex.IsMatch(id.Trim());

        public IReadOnlySet<string> GetIgnoredIds()
        {
            lock (_gate)
            {
                if (_ignoredIds != null) return _ignoredIds;

                _ignoredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(_ignoredPath))
                    {
                        var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_ignoredPath));
                        if (list != null)
                        {
                            foreach (string id in list)
                            {
                                if (!string.IsNullOrWhiteSpace(id)) _ignoredIds.Add(id.Trim());
                            }
                        }
                    }
                }
                catch
                {
                }
                return _ignoredIds;
            }
        }

        public void IgnoreUpdate(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            lock (_gate)
            {
                GetIgnoredIds();
                _ignoredIds!.Add(id.Trim());
                SaveIgnored();
            }
        }

        public void UnignoreAll()
        {
            lock (_gate)
            {
                _ignoredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                SaveIgnored();
            }
        }

        private void SaveIgnored()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_ignoredPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_ignoredPath, JsonSerializer.Serialize(_ignoredIds!.OrderBy(x => x).ToList(), JsonOptions));
            }
            catch
            {
            }
        }

        public async Task<List<ProgramUpdateItemViewModel>> ListAvailableUpgradesAsync(
            bool includeIgnored = false,
            CancellationToken ct = default)
        {
            string winget = SoftwareService.GetWingetPath();
            var (_, stdout) = await RunProcessCaptureAsync(
                winget,
                "upgrade --accept-source-agreements --disable-interactivity",
                onLine: null,
                ct);

            var parsed = ParseUpgradeOutput(stdout);
            if (!includeIgnored)
            {
                var ignored = GetIgnoredIds();
                parsed = parsed.Where(p => !ignored.Contains(p.Id)).ToList();
            }
            return parsed;
        }

        public async Task<(bool Ok, string Message)> UpgradeAsync(
            ProgramUpdateItemViewModel item,
            Action<string>? logProgress = null,
            CancellationToken ct = default)
        {
            if (!IsValidWingetId(item.Id))
            {
                return (false, $"ID '{item.Id}' truncado ou inválido para o Winget.");
            }

            item.IsUpdating = true;
            item.StatusMessage = "Atualizando...";
            try
            {
                string winget = SoftwareService.GetWingetPath();
                string sourceArg = string.IsNullOrWhiteSpace(item.Source) ? "" : $" --source \"{item.Source.Replace("\"", "")}\"";
                string args = $"upgrade --id \"{item.Id}\" -e{sourceArg} --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";

                var (exitCode, stdout) = await RunProcessCaptureAsync(winget, args, logProgress, ct);
                uint uCode = unchecked((uint)exitCode);

                bool ok = exitCode == 0 || uCode == 0x8A15010B; // 0x8A15010B = no applicable update found / already up to date
                if (ok)
                {
                    item.IsUpdated = true;
                    item.IsSelected = false;
                    item.StatusMessage = uCode == 0x8A15010B ? "Já atualizado" : "Atualizado";
                    return (true, item.StatusMessage);
                }

                string msg = uCode switch
                {
                    0x8A1500F1 => "Este app bloqueia atualização silenciosa pelo Winget; atualize dentro do próprio programa.",
                    _ => ExtractRelevantError(stdout, exitCode)
                };
                item.StatusMessage = "Falhou";
                return (false, msg);
            }
            catch (OperationCanceledException)
            {
                item.StatusMessage = "Cancelado";
                throw;
            }
            catch (Exception ex)
            {
                item.StatusMessage = "Erro";
                return (false, ex.Message);
            }
            finally
            {
                item.IsUpdating = false;
            }
        }

        public static List<ProgramUpdateItemViewModel> ParseUpgradeOutput(string stdout)
        {
            var result = new List<ProgramUpdateItemViewModel>();
            if (string.IsNullOrWhiteSpace(stdout)) return result;

            List<string> lines = stdout.Replace("\r\n", "\n")
                .Split('\n')
                .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
                .ToList();

            int sepIndex = lines.FindIndex(l => l.Trim().Length > 20 && l.Trim().All(c => c == '-'));
            if (sepIndex < 1) return result;

            string header = lines[sepIndex - 1];
            var colStarts = new List<int>();
            for (int i = 0; i < header.Length; i++)
            {
                if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
                {
                    colStarts.Add(i);
                }
            }

            if (colStarts.Count < 4) return result;
            bool hasSourceCol = colStarts.Count >= 5;

            for (int row = sepIndex + 1; row < lines.Count; row++)
            {
                string line = lines[row].TrimEnd();
                if (line.Length == 0 || line.Length < colStarts[3]) break;

                string Col(int c)
                {
                    int start = colStarts[c];
                    int end = c + 1 < colStarts.Count ? colStarts[c + 1] : line.Length;
                    return start < line.Length
                        ? line.Substring(start, Math.Min(end, line.Length) - start).Trim()
                        : "";
                }

                string name = Col(0);
                string id = Col(1);
                string installedVer = Col(2);
                string availableVer = Col(3);
                string source = hasSourceCol ? Col(4) : "winget";

                bool misaligned = colStarts.Skip(1).Take(3).Any(pos => pos < line.Length && pos > 0 && line[pos - 1] != ' ');
                if (misaligned || id.Contains(' ') || id.Length == 0 || availableVer.Length == 0 || (hasSourceCol && source.Contains(' ')))
                {
                    var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    int tailCount = hasSourceCol ? 4 : 3;
                    if (tokens.Count < tailCount + 1) continue;

                    source = hasSourceCol ? tokens[^1] : "winget";
                    availableVer = tokens[^(tailCount - 2)];
                    installedVer = tokens[^(tailCount - 1)];
                    int idOffset = tailCount;
                    string maybePrefix = tokens[^tailCount];
                    if ((maybePrefix == "<" || maybePrefix == ">") && tokens.Count >= tailCount + 2)
                    {
                        installedVer = $"{maybePrefix} {installedVer}";
                        idOffset = tailCount + 1;
                    }

                    id = tokens[^idOffset];
                    name = string.Join(' ', tokens.Take(tokens.Count - idOffset));
                }

                if (name.EndsWith('…')) name = name.TrimEnd('…').Trim();
                if (id.EndsWith('…') || !IsValidWingetId(id)) continue;
                if (installedVer.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                    installedVer.Equals("Desconhecido", StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(new ProgramUpdateItemViewModel
                {
                    Name = name,
                    Id = id,
                    InstalledVersion = installedVer,
                    AvailableVersion = availableVer,
                    Source = string.IsNullOrWhiteSpace(source) ? "winget" : source
                });
            }

            return result;
        }

        private static string ExtractRelevantError(string stdout, int exitCode)
        {
            string? lastMeaningful = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.Length > 3 && !l.All(c => c is '-' or '\\' or '|' or '/'));
            return string.IsNullOrWhiteSpace(lastMeaningful)
                ? $"Falhou (código 0x{exitCode:X8})."
                : $"{lastMeaningful} (código 0x{exitCode:X8})";
        }

        private static async Task<(int ExitCode, string Stdout)> RunProcessCaptureAsync(
            string fileName,
            string arguments,
            Action<string>? onLine,
            CancellationToken ct)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar '{fileName}'.");
            using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

            var sb = new StringBuilder();
            var readOut = Task.Run(async () =>
            {
                while (await proc.StandardOutput.ReadLineAsync() is { } line)
                {
                    sb.AppendLine(line);
                    if (!string.IsNullOrWhiteSpace(line) && !line.Trim().All(c => c is '-' or '\\' or '|' or '/'))
                    {
                        onLine?.Invoke(line.Trim());
                    }
                }
            }, CancellationToken.None);

            var readErr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            await proc.WaitForExitAsync(ct);
            await Task.WhenAll(readOut, readErr);

            return (proc.ExitCode, sb.ToString());
        }
    }
}

