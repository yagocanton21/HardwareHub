using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HardwareHub.Models;

namespace HardwareHub.Services
{
    public sealed class BloatwareService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true
        };

        private readonly string _historyPath;

        public IReadOnlyList<BloatwareDefinition> Catalog { get; }
        public IReadOnlyList<ProtectedProgramRule> ProtectedPrograms { get; }
        public IReadOnlyList<ProtectedProgramRule> CautionPrograms { get; }

        public static string DefaultHistoryPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HardwareHub",
            "removed-bloatware.json");

        public BloatwareService(BloatwareCatalog? customCatalog = null, string? historyPath = null)
        {
            var catalog = customCatalog ?? LoadEmbeddedCatalog();
            Catalog = catalog.Apps;
            ProtectedPrograms = catalog.ProgramasProtegidos;
            CautionPrograms = catalog.ProgramasCuidado;
            _historyPath = historyPath ?? DefaultHistoryPath;
        }

        public static BloatwareCatalog LoadEmbeddedCatalog()
        {
            Assembly asm = typeof(BloatwareService).Assembly;
            string? resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Resources.Data.bloatware.json", StringComparison.OrdinalIgnoreCase));
            if (resName == null)
            {
                throw new FileNotFoundException("Recurso embutido Resources.Data.bloatware.json não encontrado.");
            }

            using Stream stream = asm.GetManifestResourceStream(resName)!;
            return JsonSerializer.Deserialize<BloatwareCatalog>(stream, JsonOptions) ?? new BloatwareCatalog();
        }

        internal sealed class AppxScanPayload
        {
            public List<AppxEntry> U { get; set; } = new();
            public List<ProvisionedEntry> P { get; set; } = new();
        }

        internal sealed class AppxEntry
        {
            public string? Name { get; set; }
            public string? PackageFullName { get; set; }
            public string? PackageFamilyName { get; set; }
        }

        internal sealed class ProvisionedEntry
        {
            public string? DisplayName { get; set; }
            public string? PackageName { get; set; }
        }

        public async Task<List<BloatwareItemViewModel>> ScanAsync(bool includeProtected = true, CancellationToken ct = default)
        {
            const string script = """
                [Console]::OutputEncoding = [Text.Encoding]::UTF8
                $u = @(Get-AppxPackage | Select-Object Name, PackageFullName, PackageFamilyName)
                $p = @()
                try { $p = @(Get-AppxProvisionedPackage -Online -ErrorAction Stop | Select-Object DisplayName, PackageName) } catch { }
                ConvertTo-Json -InputObject @{ u = $u; p = $p } -Compress -Depth 3
                """;

            var (exitCode, stdout, stderr) = await RunPowerShellAsync(script, ct);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(stderr)
                        ? "Não foi possível consultar os pacotes UWP instalados."
                        : $"Falha ao listar apps do Windows: {stderr.Trim()}");
            }

            return ParseScanJson(stdout, includeProtected);
        }

        internal List<BloatwareItemViewModel> ParseScanJson(string rawJson, bool includeProtected = true)
        {
            string trimmed = rawJson.Trim();
            int jsonStart = trimmed.IndexOf('{');
            if (jsonStart > 0) trimmed = trimmed[jsonStart..];

            var payload = JsonSerializer.Deserialize<AppxScanPayload>(trimmed, JsonOptions) ?? new AppxScanPayload();
            var items = new List<BloatwareItemViewModel>();

            foreach (BloatwareDefinition def in Catalog)
            {
                if (!includeProtected && def.Level == BloatwareRecommendation.KeepProtected) continue;

                var userMatches = payload.U.Where(u => def.MatchesPackage(u.Name)).ToList();
                var provMatches = payload.P.Where(p => def.MatchesPackage(p.DisplayName)).ToList();

                if (userMatches.Count == 0 && provMatches.Count == 0) continue;

                var vm = new BloatwareItemViewModel(def)
                {
                    UserPackageFullNames = userMatches
                        .Select(m => m.PackageFullName ?? "")
                        .Where(s => s.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    ProvisionedPackageNames = provMatches
                        .Select(m => m.PackageName ?? "")
                        .Where(s => s.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    PrimaryPackageName = userMatches.FirstOrDefault()?.Name ?? provMatches.FirstOrDefault()?.DisplayName,
                    PackageFamilyName = userMatches.FirstOrDefault(m => !string.IsNullOrEmpty(m.PackageFamilyName))?.PackageFamilyName,
                    IsInstalled = userMatches.Count > 0,
                    IsProvisioned = provMatches.Count > 0
                };
                vm.StatusText = vm.IsInstalled ? "Instalado" : "Provisionado";
                items.Add(vm);
            }

            return items;
        }

        public async Task<(bool Ok, string Message)> RemoveAsync(
            BloatwareItemViewModel item,
            bool alsoNewUsers = false,
            CancellationToken ct = default)
        {
            if (!item.CanRemove)
            {
                return (false, "Protegido: o Windows precisa deste aplicativo.");
            }

            if (!item.IsInstalled && !alsoNewUsers)
            {
                return (false, "Não instalado neste usuário. Marque 'Também para novos usuários' para desprovisionar.");
            }

            item.IsBusy = true;
            item.StatusText = "Removendo...";
            try
            {
                string script = BuildRemovalScript(item.Definition.Pacotes, alsoNewUsers);
                var (_, stdout, stderr) = await RunPowerShellAsync(script, ct);

                string[] lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                int remainingUser = ParseTag(lines, "RESTA");
                int remainingProv = ParseTag(lines, "RESTAP");
                var errors = lines.Where(l => l.StartsWith("ERRO=", StringComparison.Ordinal)).Select(l => l[5..]).ToList();
                if (remainingUser < 0 && !string.IsNullOrWhiteSpace(stderr))
                {
                    errors.Add(stderr.Trim());
                }

                bool success = remainingUser == 0 && (!alsoNewUsers || remainingProv == 0);
                if (remainingUser == 0)
                {
                    item.IsInstalled = false;
                    if (!alsoNewUsers || remainingProv == 0) item.IsProvisioned = false;
                    item.IsSelected = false;

                    RecordRemoval(new RemovedBloatwareRecord
                    {
                        Data = DateTime.Now,
                        Id = item.Id,
                        Nome = item.Nome,
                        Pacote = item.PrimaryPackageName ?? item.Definition.Pacotes.FirstOrDefault() ?? "",
                        Familia = item.PackageFamilyName ?? "",
                        StoreId = item.StoreId
                    });
                }

                if (success)
                {
                    item.StatusText = alsoNewUsers ? "Removido (todos usuários)" : "Removido";
                    return (true, item.StatusText);
                }

                if (remainingUser == 0 && remainingProv > 0)
                {
                    item.StatusText = "Removido (usuário atual)";
                    return (true, $"Removido para você. Falhou ao desprovisionar para novas contas: {errors.FirstOrDefault() ?? "requer Admin"}");
                }

                string errMsg = errors.FirstOrDefault() ?? "erro desconhecido";
                item.StatusText = "Falha";
                return (false, $"Não removido: {errMsg}");
            }
            finally
            {
                item.IsBusy = false;
            }
        }

        public async Task<int> RemoveBatchAsync(
            IReadOnlyList<BloatwareItemViewModel> items,
            bool alsoNewUsers = false,
            Action<int, int, BloatwareItemViewModel>? progressCallback = null,
            CancellationToken ct = default)
        {
            var targets = items.Where(i => i.CanRemove && (i.IsInstalled || (alsoNewUsers && i.IsProvisioned))).ToList();
            if (targets.Count == 0) return 0;

            foreach (var t in targets)
            {
                t.IsBusy = true;
                t.StatusText = "Na fila...";
            }

            int removedCount = 0;
            try
            {
                string script = BuildBatchRemovalScript(targets, alsoNewUsers);
                var byId = targets.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
                int processed = 0;

                var (_, _, stderr) = await RunPowerShellStreamingAsync(script, line =>
                {
                    if (!line.StartsWith("APP=", StringComparison.Ordinal)) return;
                    var parts = line.Split('|', 4);
                    if (parts.Length < 3) return;
                    string id = parts[0][4..];
                    int remU = int.TryParse(parts[1].AsSpan(6), out int uVal) ? uVal : -1;
                    int remP = int.TryParse(parts[2].AsSpan(7), out int pVal) ? pVal : 0;
                    string err = parts.Length >= 4 && parts[3].StartsWith("ERR=", StringComparison.Ordinal) ? parts[3][4..] : "";

                    if (!byId.TryGetValue(id, out var vm)) return;

                    processed++;
                    if (remU == 0)
                    {
                        vm.IsInstalled = false;
                        if (!alsoNewUsers || remP == 0) vm.IsProvisioned = false;
                        vm.IsSelected = false;

                        RecordRemoval(new RemovedBloatwareRecord
                        {
                            Data = DateTime.Now,
                            Id = vm.Id,
                            Nome = vm.Nome,
                            Pacote = vm.PrimaryPackageName ?? vm.Definition.Pacotes.FirstOrDefault() ?? "",
                            Familia = vm.PackageFamilyName ?? "",
                            StoreId = vm.StoreId
                        });

                        vm.StatusText = alsoNewUsers && remP == 0 ? "Removido (todos usuários)" : "Removido";
                        removedCount++;
                    }
                    else
                    {
                        vm.StatusText = string.IsNullOrWhiteSpace(err) ? "Falha" : $"Falha: {err}";
                    }
                    vm.IsBusy = false;
                    progressCallback?.Invoke(processed, targets.Count, vm);
                }, ct, TimeSpan.FromMinutes(4));

                if (!string.IsNullOrWhiteSpace(stderr) && processed == 0)
                {
                    foreach (var t in targets) t.StatusText = "Falha";
                }
            }
            finally
            {
                foreach (var t in targets)
                {
                    t.IsBusy = false;
                    if (t.StatusText == "Na fila...") t.StatusText = t.IsInstalled ? "Instalado" : "Provisionado";
                }
            }

            return removedCount;
        }

        internal static string BuildBatchRemovalScript(IReadOnlyList<BloatwareItemViewModel> items, bool alsoNewUsers)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Console]::OutputEncoding = [Text.Encoding]::UTF8");
            sb.AppendLine("$allUser = @(Get-AppxPackage)");
            if (alsoNewUsers)
            {
                sb.AppendLine("$allProv = @()");
                sb.AppendLine("try { $allProv = @(Get-AppxProvisionedPackage -Online -ErrorAction Stop) } catch { }");
            }

            foreach (var item in items)
            {
                if (!item.CanRemove) continue;
                string safeId = EscapePsSingleQuote(item.Id);
                string patternList = string.Join(", ", item.Definition.Pacotes.Select(p => $"'{EscapePsSingleQuote(p)}'"));
                sb.AppendLine($"$erros = @(); $fam = @(); $nomes = @({patternList})");
                sb.AppendLine("foreach ($n in $nomes) {");
                sb.AppendLine("  $allUser | Where-Object { $_.Name -like $n } | ForEach-Object { $fam += $_.PackageFamilyName; try { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction Stop } catch { $erros += $_.Exception.Message } }");
                if (alsoNewUsers)
                {
                    sb.AppendLine("  $allProv | Where-Object { $_.DisplayName -like $n } | ForEach-Object { $p = $_.PackageName -split '_'; $fam += $p[0] + '_' + $p[-1]; try { Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -ErrorAction Stop | Out-Null } catch { $erros += $_.Exception.Message } }");
                }
                sb.AppendLine("}");
                if (alsoNewUsers)
                {
                    sb.AppendLine(@"foreach ($f in @($fam | Where-Object { $_ } | Sort-Object -Unique)) { try { New-Item -Path ""HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Deprovisioned\$f"" -Force -ErrorAction Stop | Out-Null } catch { $erros += $_.Exception.Message } }");
                }
                sb.AppendLine("$ru = @(foreach ($n in $nomes) { Get-AppxPackage -Name $n }).Count");
                sb.AppendLine(alsoNewUsers
                    ? "$rp = @($allProv | Where-Object { $d = $_.DisplayName; @($nomes | Where-Object { $d -like $_ }).Count -gt 0 } | Where-Object { try { $pkg = $_.PackageName; @(Get-AppxProvisionedPackage -Online | Where-Object PackageName -eq $pkg).Count -gt 0 } catch { $false } }).Count"
                    : "$rp = 0");
                sb.AppendLine($"$e = ($erros | Select-Object -First 1) -replace \"[`r`n|]\", ' '");
                sb.AppendLine($"\"APP={safeId}|RESTA=$ru|RESTAP=$rp|ERR=$e\"");
            }
            return sb.ToString();
        }

        internal static string BuildRemovalScript(IEnumerable<string> packagePatterns, bool alsoNewUsers)
        {
            string patternList = string.Join(", ", packagePatterns.Select(p => $"'{EscapePsSingleQuote(p)}'"));
            var sb = new StringBuilder();
            sb.AppendLine("[Console]::OutputEncoding = [Text.Encoding]::UTF8");
            sb.AppendLine("$erros = @()");
            sb.AppendLine("$fam = @()");
            sb.AppendLine($"$nomes = @({patternList})");
            sb.AppendLine("foreach ($n in $nomes) {");
            sb.AppendLine("  Get-AppxPackage -Name $n | ForEach-Object { $fam += $_.PackageFamilyName; try { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction Stop } catch { $erros += $_.Exception.Message } }");
            if (alsoNewUsers)
            {
                sb.AppendLine("  Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -like $n } | ForEach-Object { $p = $_.PackageName -split '_'; $fam += $p[0] + '_' + $p[-1]; try { Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -ErrorAction Stop | Out-Null } catch { $erros += $_.Exception.Message } }");
            }
            sb.AppendLine("}");
            if (alsoNewUsers)
            {
                sb.AppendLine(@"foreach ($f in @($fam | Where-Object { $_ } | Sort-Object -Unique)) { try { New-Item -Path ""HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Deprovisioned\$f"" -Force -ErrorAction Stop | Out-Null } catch { $erros += $_.Exception.Message } }");
            }
            sb.AppendLine("$resta = @(foreach ($n in $nomes) { Get-AppxPackage -Name $n }).Count");
            sb.AppendLine(alsoNewUsers
                ? "$restaP = @(Get-AppxProvisionedPackage -Online | Where-Object { $d = $_.DisplayName; @($nomes | Where-Object { $d -like $_ }).Count -gt 0 }).Count"
                : "$restaP = 0");
            sb.AppendLine("\"RESTA=$resta\"");
            sb.AppendLine("\"RESTAP=$restaP\"");
            sb.AppendLine("$erros | ForEach-Object { \"ERRO=$_\" }");
            return sb.ToString();
        }

        public async Task<(bool Ok, string Message)> ReinstallAsync(RemovedBloatwareRecord record, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(record.Familia))
            {
                string escFamily = EscapePsSingleQuote(record.Familia);
                string escPkg = EscapePsSingleQuote(record.Pacote);
                string script = $@"
Remove-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Deprovisioned\{escFamily}' -Force -ErrorAction SilentlyContinue
try {{ Add-AppxPackage -RegisterByFamilyName -MainPackage '{escFamily}' -ErrorAction Stop }} catch {{ }}
if (Get-AppxPackage -Name '{escPkg}') {{ 'OK' }}";

                var (_, stdout, _) = await RunPowerShellAsync(script, ct);
                if (stdout.Contains("OK", StringComparison.Ordinal))
                {
                    MarkReinstalled(record);
                    return (true, $"{record.Nome} reinstalado com sucesso.");
                }
            }

            if (!string.IsNullOrEmpty(record.StoreId) && Regex.IsMatch(record.StoreId, "^[A-Za-z0-9]{12}$"))
            {
                string wingetPath = SoftwareService.GetWingetPath();
                var psi = new ProcessStartInfo(wingetPath,
                    $"install --id {record.StoreId} --source msstore --accept-package-agreements --accept-source-agreements --silent --disable-interactivity")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var outTask = proc.StandardOutput.ReadToEndAsync(ct);
                    var errTask = proc.StandardError.ReadToEndAsync(ct);
                    await proc.WaitForExitAsync(ct);
                    await Task.WhenAll(outTask, errTask);
                    uint code = unchecked((uint)proc.ExitCode);
                    if (proc.ExitCode == 0 || code == 0x8A15002B || code == 0x8A150061)
                    {
                        MarkReinstalled(record);
                        return (true, $"{record.Nome} reinstalado via Microsoft Store.");
                    }
                }
            }

            return (false, "Não foi possível reinstalar automaticamente. Abra a Microsoft Store para baixar novamente.");
        }

        public List<RemovedBloatwareRecord> LoadHistory()
        {
            try
            {
                if (File.Exists(_historyPath))
                {
                    return JsonSerializer.Deserialize<List<RemovedBloatwareRecord>>(File.ReadAllText(_historyPath), JsonOptions)
                           ?? new List<RemovedBloatwareRecord>();
                }
            }
            catch { }
            return new List<RemovedBloatwareRecord>();
        }

        internal void RecordRemoval(RemovedBloatwareRecord entry)
        {
            var list = LoadHistory();
            list.RemoveAll(x => string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase) && !x.Reinstalado.HasValue);
            list.Insert(0, entry);
            SaveHistory(list);
        }

        internal void MarkReinstalled(RemovedBloatwareRecord entry)
        {
            var list = LoadHistory();
            var match = list.FirstOrDefault(x =>
                string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase) && !x.Reinstalado.HasValue);
            if (match != null)
            {
                match.Reinstalado = DateTime.Now;
                SaveHistory(list);
            }
        }

        private void SaveHistory(List<RemovedBloatwareRecord> records)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_historyPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_historyPath, JsonSerializer.Serialize(records, JsonOptions));
            }
            catch { }
        }

        private static int ParseTag(string[] lines, string key)
        {
            string? line = lines.FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal));
            return line != null && int.TryParse(line[(key.Length + 1)..], out int val) ? val : -1;
        }

        private static string EscapePsSingleQuote(string input) => input.Replace("'", "''");

        private static ProcessStartInfo CreatePowerShellStartInfo(string script)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        public static async Task<(int ExitCode, string Stdout, string Stderr)> RunPowerShellAsync(
            string script,
            CancellationToken ct = default,
            TimeSpan? timeout = null)
        {
            using var proc = Process.Start(CreatePowerShellStartInfo(script)) ?? throw new InvalidOperationException("Não foi possível iniciar powershell.exe.");
            using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(3));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linkedCts.Token.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            });

            var outTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            await proc.WaitForExitAsync(linkedCts.Token);
            await Task.WhenAll(outTask, errTask);

            return (proc.ExitCode, outTask.Result, errTask.Result);
        }

        public static async Task<(int ExitCode, string Stdout, string Stderr)> RunPowerShellStreamingAsync(
            string script,
            Action<string>? onOutputLine,
            CancellationToken ct = default,
            TimeSpan? timeout = null)
        {
            using var proc = Process.Start(CreatePowerShellStartInfo(script)) ?? throw new InvalidOperationException("Não foi possível iniciar powershell.exe.");
            using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(4));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linkedCts.Token.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            });

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            var readOut = Task.Run(async () =>
            {
                while (await proc.StandardOutput.ReadLineAsync() is { } line)
                {
                    stdoutBuilder.AppendLine(line);
                    onOutputLine?.Invoke(line);
                }
            }, CancellationToken.None);

            var readErr = Task.Run(async () =>
            {
                while (await proc.StandardError.ReadLineAsync() is { } line)
                {
                    stderrBuilder.AppendLine(line);
                }
            }, CancellationToken.None);

            await proc.WaitForExitAsync(linkedCts.Token);
            await Task.WhenAll(readOut, readErr);

            return (proc.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
        }
    }
}

