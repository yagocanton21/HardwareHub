using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    public sealed class StartupManagerService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static readonly (string RunKey, string ApprovedKey, string Origin)[] RegistrySources =
        {
            (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Usuário atual (registro)"),
            (@"HKLM\Software\Microsoft\Windows\CurrentVersion\Run", @"HKLM\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Todos os usuários (registro)"),
            (@"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", @"HKLM\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", "Todos os usuários 32 bits (registro)")
        };

        private readonly IRegistryBackend _registry;
        private readonly string? _userStartupFolder;
        private readonly string? _commonStartupFolder;
        private readonly IReadOnlyList<StartupCatalogRule> _rules;

        public StartupManagerService(
            IRegistryBackend? registry = null,
            string? userStartupFolder = null,
            string? commonStartupFolder = null,
            IReadOnlyList<StartupCatalogRule>? customRules = null)
        {
            _registry = registry ?? new WindowsRegistryBackend();
            _userStartupFolder = userStartupFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            _commonStartupFolder = commonStartupFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
            _rules = customRules ?? LoadEmbeddedRules();
        }

        public static IReadOnlyList<StartupCatalogRule> LoadEmbeddedRules()
        {
            Assembly asm = typeof(StartupManagerService).Assembly;
            string? resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Resources.Data.inicializacao.json", StringComparison.OrdinalIgnoreCase));
            if (resName == null) return Array.Empty<StartupCatalogRule>();

            using Stream stream = asm.GetManifestResourceStream(resName)!;
            var list = JsonSerializer.Deserialize<List<StartupCatalogRule>>(stream, JsonOptions) ?? new List<StartupCatalogRule>();
            foreach (var rule in list)
            {
                if (!string.IsNullOrWhiteSpace(rule.Padrao))
                {
                    rule.CompiledRegex = new Regex(rule.Padrao, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
            }
            return list;
        }

        public List<StartupEntryViewModel> ListEntries()
        {
            var entries = new List<StartupEntryViewModel>();

            foreach (var (runKey, approvedKey, origin) in RegistrySources)
            {
                foreach (var (name, rawData) in _registry.ReadAllValues(runKey))
                {
                    string cmd = rawData?.ToString()?.Trim() ?? "";
                    if (cmd.Length == 0) continue;
                    entries.Add(CreateEntry(name, cmd, origin, approvedKey, name));
                }
            }

            AddFolderEntries(entries, _userStartupFolder, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", "Usuário atual (pasta)");
            AddFolderEntries(entries, _commonStartupFolder, @"HKLM\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", "Todos os usuários (pasta)");

            return entries.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public Task EnrichDetailsAsync(IReadOnlyList<StartupEntryViewModel> entries, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        EnrichSingle(entry);
                    }
                    catch
                    {
                    }
                }
            }, ct);
        }

        public void SetEnabled(StartupEntryViewModel entry, bool enabled)
        {
            byte[] payload = BuildApprovedBytes(enabled, DateTime.Now);
            _registry.Write(entry.ApprovedKey, entry.ApprovedName, RegistryValueKind.Binary, payload);
            entry.IsEnabled = enabled;
            entry.DisabledAt = enabled ? null : ReadDisabledTimestamp(payload);
        }

        internal static byte[] BuildApprovedBytes(bool enabled, DateTime timestamp)
        {
            byte[] bytes = new byte[12];
            bytes[0] = (byte)(enabled ? 2 : 3);
            if (!enabled)
            {
                BitConverter.GetBytes(timestamp.ToFileTime()).CopyTo(bytes, 4);
            }
            return bytes;
        }

        internal static (bool IsEnabled, DateTime? DisabledAt) DecodeApprovedBytes(object? rawData)
        {
            if (rawData is not byte[] bytes || bytes.Length == 0)
            {
                return (true, null);
            }

            bool disabled = (bytes[0] & 1) == 1;
            return (IsEnabled: !disabled, DisabledAt: disabled ? ReadDisabledTimestamp(bytes) : null);
        }

        private static DateTime? ReadDisabledTimestamp(byte[] bytes)
        {
            if (bytes.Length < 12) return null;
            long fileTime = BitConverter.ToInt64(bytes, 4);
            if (fileTime <= 0) return null;
            try
            {
                return DateTime.FromFileTime(fileTime);
            }
            catch
            {
                return null;
            }
        }

        private void AddFolderEntries(List<StartupEntryViewModel> entries, string? folder, string approvedKey, string origin)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            try
            {
                foreach (string file in Directory.GetFiles(folder))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    entries.Add(CreateEntry(Path.GetFileNameWithoutExtension(file), file, origin, approvedKey, fileName));
                }
            }
            catch
            {
            }
        }

        private StartupEntryViewModel CreateEntry(string name, string command, string origin, string approvedKey, string approvedName)
        {
            var reg = _registry.Read(approvedKey, approvedName);
            var (isEnabled, disabledAt) = DecodeApprovedBytes(reg.Exists ? reg.Data : null);

            var entry = new StartupEntryViewModel
            {
                Name = name,
                DisplayName = name,
                Command = command,
                Origin = origin,
                ApprovedKey = approvedKey,
                ApprovedName = approvedName,
                IsEnabled = isEnabled,
                DisabledAt = disabledAt
            };

            EnrichFromCatalogOnly(entry);
            return entry;
        }

        private void EnrichFromCatalogOnly(StartupEntryViewModel entry)
        {
            var match = FindCatalogMatch(entry, extra: null);
            if (match != null)
            {
                if (!string.IsNullOrWhiteSpace(match.Nome)) entry.DisplayName = match.Nome;
                entry.Description = match.OQueE;
                entry.Advice = ParseAdvice(match.Desligar);
            }
        }

        internal void EnrichSingle(StartupEntryViewModel entry)
        {
            string? resolvedPath = ResolveCommandFile(entry.Command);
            bool exists = !string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath);
            entry.FilePath = resolvedPath ?? "";
            entry.FileExists = exists || resolvedPath == null;

            string productName = "";
            string fileDescription = "";
            string companyName = "";

            if (exists && resolvedPath != null)
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(resolvedPath);
                    productName = CleanString(vi.ProductName);
                    fileDescription = CleanString(vi.FileDescription);
                    companyName = CleanString(vi.CompanyName);
                }
                catch
                {
                }
            }

            entry.Publisher = companyName.Length > 0 ? companyName : (exists ? "Editora não informada" : "Arquivo ausente");

            bool usableProductName = productName.Length > 0 &&
                !Regex.IsMatch(productName, @"Windows.*(Operating System|Sistema Operacional)|Sistema Operacional", RegexOptions.IgnoreCase);

            var match = FindCatalogMatch(entry, $"{productName} {fileDescription} {companyName} {Path.GetFileName(resolvedPath ?? "")}");
            entry.DisplayName = match?.Nome ?? (usableProductName ? productName : (fileDescription.Length > 0 ? fileDescription : entry.Name));

            if (!exists && resolvedPath != null)
            {
                entry.Description = "O executável deste atalho não existe mais no disco (programa removido ou movido). Pode desligar sem risco.";
                entry.Advice = StartupAdvice.CanDisable;
                return;
            }

            if (match != null)
            {
                entry.Description = match.OQueE;
                entry.Advice = ParseAdvice(match.Desligar);
                return;
            }

            string fallbackDesc = fileDescription.Length > 0 && !fileDescription.Equals(entry.DisplayName, StringComparison.OrdinalIgnoreCase)
                ? fileDescription
                : (productName.Length > 0 && !productName.Equals(entry.DisplayName, StringComparison.OrdinalIgnoreCase) ? productName : "");

            entry.Description = fallbackDesc.Length > 0
                ? $"Descrição do arquivo: \"{fallbackDesc}\"."
                : "Programa desconhecido no catálogo local. Confira antes de desativar.";
            entry.Advice = StartupAdvice.Unknown;
        }

        private StartupCatalogRule? FindCatalogMatch(StartupEntryViewModel entry, string? extra)
        {
            string hay = $"{entry.Name} {entry.Command} {entry.DisplayName} {entry.Publisher} {extra}";
            return _rules.FirstOrDefault(r => r.CompiledRegex?.IsMatch(hay) ?? false);
        }

        private static StartupAdvice ParseAdvice(string? raw) => raw?.Trim().ToLowerInvariant() switch
        {
            "pode" => StartupAdvice.CanDisable,
            "cuidado" => StartupAdvice.Caution,
            "deixe" => StartupAdvice.KeepEnabled,
            _ => StartupAdvice.Unknown
        };

        private static string CleanString(string? input) => (input ?? "").Replace("\0", "").Trim();

        public static string? ResolveCommandFile(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string expanded = Environment.ExpandEnvironmentVariables(command.Trim());
            string exe;
            string args;

            if (expanded.StartsWith('"'))
            {
                int closing = expanded.IndexOf('"', 1);
                exe = closing > 0 ? expanded[1..closing] : expanded.Trim('"');
                args = closing > 0 ? expanded[(closing + 1)..] : "";
            }
            else
            {
                var match = Regex.Match(expanded, @"^(.+?\.(exe|lnk|bat|cmd|com|vbs|url))(\s|$)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    exe = match.Groups[1].Value;
                    args = expanded[match.Length..];
                }
                else
                {
                    int space = expanded.IndexOf(' ');
                    exe = space > 0 ? expanded[..space] : expanded;
                    args = space > 0 ? expanded[space..] : "";
                }
            }

            exe = exe.Trim();
            if (!Path.IsPathRooted(exe))
            {
                exe = FindInPath(exe) ?? exe;
            }

            if (exe.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(exe))
            {
                exe = ResolveShortcutTarget(exe) ?? exe;
            }

            string fileName = Path.GetFileName(exe);
            if (fileName.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase))
            {
                string dll = args.Trim().Split(',')[0].Trim().Trim('"');
                if (dll.Length > 0)
                {
                    if (!Path.IsPathRooted(dll)) dll = FindInPath(dll) ?? dll;
                    if (File.Exists(dll)) return dll;
                }
            }

            if (fileName.Equals("Update.exe", StringComparison.OrdinalIgnoreCase))
            {
                var procMatch = Regex.Match(args, @"--processStart(?:AndWait)?\s+""?([^""\s]+)""?", RegexOptions.IgnoreCase);
                string? dir = Path.GetDirectoryName(exe);
                if (procMatch.Success && dir != null)
                {
                    string direct = Path.Combine(dir, procMatch.Groups[1].Value);
                    if (File.Exists(direct)) return direct;
                    try
                    {
                        string? latest = Directory.GetDirectories(dir, "app-*")
                            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                            .Select(d => Path.Combine(d, procMatch.Groups[1].Value))
                            .FirstOrDefault(File.Exists);
                        if (latest != null) return latest;
                    }
                    catch
                    {
                    }
                }
            }

            return exe;
        }

        private static string? FindInPath(string fileName)
        {
            if (!Path.HasExtension(fileName)) fileName += ".exe";
            var dirs = new[]
            {
                Environment.SystemDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));

            foreach (string dir in dirs)
            {
                try
                {
                    string candidate = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim()), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                }
            }
            return null;
        }

        private static string? ResolveShortcutTarget(string lnkPath)
        {
            object? shell = null;
            object? shortcut = null;
            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                shell = Activator.CreateInstance(shellType);
                if (shell == null) return null;
                dynamic dynShell = shell;
                shortcut = dynShell.CreateShortcut(lnkPath);
                if (shortcut == null) return null;
                dynamic dynShortcut = shortcut;
                string? target = dynShortcut.TargetPath as string;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}

