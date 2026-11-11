using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    public sealed class TweakEngine
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true
        };

        private readonly IRegistryBackend _registry;
        private readonly ServiceStartupHelper _services;
        private readonly string _backupFilePath;
        private readonly Dictionary<string, TweakBackupEntry> _backup;
        private readonly object _gate = new();

        public IReadOnlyList<TweakDefinition> Tweaks { get; }
        public int CurrentBuild { get; }

        public static string DefaultBackupFilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HardwareHub",
            "tweaks-backup.json");

        public TweakEngine(
            IRegistryBackend? registry = null,
            ServiceStartupHelper? services = null,
            string? backupFilePath = null,
            int? currentBuild = null,
            IReadOnlyList<TweakDefinition>? customTweaks = null)
        {
            _registry = registry ?? WindowsRegistryBackend.Instance;
            _services = services ?? new ServiceStartupHelper(_registry);
            _backupFilePath = backupFilePath ?? DefaultBackupFilePath;
            CurrentBuild = currentBuild ?? Environment.OSVersion.Version.Build;
            Tweaks = customTweaks ?? LoadEmbeddedTweaks();
            _backup = LoadBackup();
        }

        public static List<TweakDefinition> LoadEmbeddedTweaks()
        {
            Assembly asm = typeof(TweakEngine).Assembly;
            string? resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Resources.Data.tweaks.json", StringComparison.OrdinalIgnoreCase));
            if (resName == null)
            {
                throw new FileNotFoundException("Recurso embutido Resources.Data.tweaks.json não encontrado.");
            }

            using Stream stream = asm.GetManifestResourceStream(resName)!;
            return JsonSerializer.Deserialize<List<TweakDefinition>>(stream, JsonOptions) ?? new List<TweakDefinition>();
        }

        private Dictionary<string, TweakBackupEntry> LoadBackup()
        {
            try
            {
                if (File.Exists(_backupFilePath))
                {
                    string json = File.ReadAllText(_backupFilePath);
                    return JsonSerializer.Deserialize<Dictionary<string, TweakBackupEntry>>(json, JsonOptions)
                           ?? new Dictionary<string, TweakBackupEntry>();
                }
            }
            catch
            {
                try
                {
                    File.Copy(_backupFilePath, $"{_backupFilePath}.{DateTime.Now:yyyyMMdd-HHmmss}.corrompido", overwrite: true);
                }
                catch { }
            }

            return new Dictionary<string, TweakBackupEntry>();
        }

        private void SaveBackup()
        {
            string? dir = Path.GetDirectoryName(_backupFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmp = _backupFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_backup, JsonOptions));
            File.Move(tmp, _backupFilePath, overwrite: true);
        }

        public bool IsApplicable(TweakDefinition tweak)
        {
            if (CurrentBuild < tweak.MinBuild) return false;
            if (tweak.MaxBuild.HasValue && CurrentBuild > tweak.MaxBuild.Value) return false;
            return true;
        }

        public bool HasBackup(string tweakId)
        {
            lock (_gate)
            {
                return _backup.ContainsKey(tweakId);
            }
        }

        public DateTime? GetAppliedAt(string tweakId)
        {
            lock (_gate)
            {
                return _backup.TryGetValue(tweakId, out var entry) ? entry.Aplicado : null;
            }
        }

        public List<(TweakDefinition Tweak, DateTime AppliedAt)> GetAppliedBackups()
        {
            lock (_gate)
            {
                return Tweaks
                    .Where(t => _backup.ContainsKey(t.Id))
                    .Select(t => (Tweak: t, AppliedAt: _backup[t.Id].Aplicado))
                    .OrderByDescending(x => x.AppliedAt)
                    .ToList();
            }
        }

        public bool IsActive(TweakDefinition tweak)
        {
            if (tweak.Ativar.Count == 0) return false;
            return tweak.Ativar.All(action =>
            {
                try
                {
                    return action.Tipo switch
                    {
                        "reg" => MatchesRegistryAction(action),
                        "regDel" => !_registry.Read(action.Chave ?? "", action.Valor).Exists,
                        "regDelKey" => !_registry.KeyExists(action.Chave ?? ""),
                        "servico" => !_services.Exists(action.Servico ?? "") || _services.Matches(action.Servico ?? "", action.Inicio ?? ""),
                        _ => false
                    };
                }
                catch
                {
                    return false;
                }
            });
        }

        private bool MatchesRegistryAction(TweakActionModel action)
        {
            if (string.IsNullOrEmpty(action.Chave)) return false;
            RegistryValueKind kind = RegistryValueConverter.ParseKind(action.Kind);
            object expected = RegistryValueConverter.ConvertData(kind, action.Dados);
            RegReadResult current = _registry.Read(action.Chave, action.Valor);
            return RegistryValueConverter.Matches(current, kind, expected);
        }

        public void Apply(TweakDefinition tweak)
        {
            lock (_gate)
            {
                bool isNewBackup = !_backup.ContainsKey(tweak.Id);
                if (isNewBackup)
                {
                    _backup[tweak.Id] = new TweakBackupEntry
                    {
                        Aplicado = DateTime.Now,
                        Snapshots = tweak.Ativar
                            .Select(TakeSnapshot)
                            .OfType<TweakBackupSnapshot>()
                            .ToList()
                    };
                    SaveBackup();
                }

                try
                {
                    foreach (TweakActionModel action in tweak.Ativar)
                    {
                        ExecuteAction(action);
                    }
                }
                catch
                {
                    if (isNewBackup && _backup.TryGetValue(tweak.Id, out var entry))
                    {
                        foreach (TweakBackupSnapshot snapshot in Enumerable.Reverse(entry.Snapshots))
                        {
                            try { RestoreSnapshot(snapshot); } catch { }
                        }
                        _backup.Remove(tweak.Id);
                        SaveBackup();
                    }
                    throw;
                }
            }
        }

        public void Revert(TweakDefinition tweak)
        {
            lock (_gate)
            {
                if (_backup.TryGetValue(tweak.Id, out TweakBackupEntry? entry))
                {
                    foreach (TweakBackupSnapshot snapshot in Enumerable.Reverse(entry.Snapshots))
                    {
                        if (BelongsToTweak(tweak, snapshot))
                        {
                            RestoreSnapshot(snapshot);
                        }
                    }

                    _backup.Remove(tweak.Id);
                    SaveBackup();
                }
                else
                {
                    foreach (TweakActionModel action in tweak.Desativar)
                    {
                        ExecuteAction(action);
                    }
                }
            }
        }

        public int RevertAll()
        {
            List<TweakDefinition> toRevert;
            lock (_gate)
            {
                toRevert = Tweaks
                    .Where(t => _backup.ContainsKey(t.Id))
                    .OrderByDescending(t => _backup[t.Id].Aplicado)
                    .ToList();
            }

            int revertedCount = 0;
            foreach (TweakDefinition tweak in toRevert)
            {
                Revert(tweak);
                revertedCount++;
            }

            return revertedCount;
        }

        private void ExecuteAction(TweakActionModel action)
        {
            switch (action.Tipo)
            {
                case "reg":
                {
                    if (string.IsNullOrEmpty(action.Chave))
                        throw new InvalidOperationException("Chave de registro ausente.");
                    RegistryValueKind kind = RegistryValueConverter.ParseKind(action.Kind);
                    object data = RegistryValueConverter.ConvertData(kind, action.Dados);
                    _registry.Write(action.Chave, action.Valor, kind, data);
                    break;
                }
                case "regDel":
                    if (!string.IsNullOrEmpty(action.Chave))
                        _registry.DeleteValue(action.Chave, action.Valor);
                    break;
                case "regDelKey":
                    if (!string.IsNullOrEmpty(action.Chave))
                        _registry.DeleteKeyTree(action.Chave);
                    break;
                case "servico":
                {
                    if (string.IsNullOrEmpty(action.Servico) || string.IsNullOrEmpty(action.Inicio))
                        throw new InvalidOperationException("Parâmetros de serviço ausentes.");
                    var (start, delayed) = ServiceStartupHelper.ParseStartupMode(action.Inicio);
                    _services.Set(action.Servico, start, start == 2 ? delayed : null);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Tipo de ação desconhecido: {action.Tipo}");
            }
        }

        private TweakBackupSnapshot? TakeSnapshot(TweakActionModel action)
        {
            if (action.Tipo == "servico" && !string.IsNullOrEmpty(action.Servico))
            {
                var (start, delayed) = _services.Get(action.Servico);
                return new TweakBackupSnapshot
                {
                    Tipo = "servico",
                    Servico = action.Servico,
                    Start = start,
                    Delayed = delayed
                };
            }

            if (action.Tipo is not ("reg" or "regDel") || string.IsNullOrEmpty(action.Chave))
            {
                return null;
            }

            RegReadResult current = _registry.Read(action.Chave, action.Valor);
            var snapshot = new TweakBackupSnapshot
            {
                Tipo = "reg",
                Chave = action.Chave,
                Valor = action.Valor,
                Existia = current.Exists,
                CriadaRaiz = action.Tipo == "reg" ? _registry.FirstMissingAncestor(action.Chave) : null
            };

            if (current.Exists)
            {
                snapshot.Kind = current.Kind.ToString();
                switch (current.Data)
                {
                    case int i:
                        snapshot.Numero = i;
                        break;
                    case long l:
                        snapshot.Numero = l;
                        break;
                    case byte[] bytes:
                        snapshot.Hex = Convert.ToHexString(bytes);
                        break;
                    case string[] arr:
                        snapshot.Lista = arr;
                        break;
                    default:
                        snapshot.Texto = current.Data?.ToString();
                        break;
                }
            }

            return snapshot;
        }

        private static bool BelongsToTweak(TweakDefinition tweak, TweakBackupSnapshot snapshot)
        {
            if (snapshot.Tipo == "servico")
            {
                return snapshot.Start is null or >= 0 and <= 4 &&
                       tweak.Ativar.Any(a => a.Tipo == "servico" &&
                                             string.Equals(a.Servico, snapshot.Servico, StringComparison.OrdinalIgnoreCase));
            }

            if (snapshot.CriadaRaiz != null &&
                !(snapshot.Chave ?? "").StartsWith(snapshot.CriadaRaiz, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return tweak.Ativar.Any(a =>
                a.Tipo is "reg" or "regDel" &&
                string.Equals(a.Chave, snapshot.Chave, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Valor ?? "", snapshot.Valor ?? "", StringComparison.OrdinalIgnoreCase));
        }

        private void RestoreSnapshot(TweakBackupSnapshot snapshot)
        {
            if (snapshot.Tipo == "servico")
            {
                if (!string.IsNullOrEmpty(snapshot.Servico) && snapshot.Start.HasValue)
                {
                    _services.Set(snapshot.Servico, snapshot.Start.Value, snapshot.Delayed);
                }
                return;
            }

            if (string.IsNullOrEmpty(snapshot.Chave)) return;

            if (snapshot.CriadaRaiz != null)
            {
                _registry.DeleteValue(snapshot.Chave, snapshot.Valor);
                _registry.DeleteEmptyKeysUpTo(snapshot.Chave, snapshot.CriadaRaiz);
                return;
            }

            if (!snapshot.Existia)
            {
                _registry.DeleteValue(snapshot.Chave, snapshot.Valor);
                return;
            }

            RegistryValueKind kind = Enum.Parse<RegistryValueKind>(snapshot.Kind ?? nameof(RegistryValueKind.String));
            object data = kind switch
            {
                RegistryValueKind.DWord => unchecked((int)(snapshot.Numero ?? 0)),
                RegistryValueKind.QWord => snapshot.Numero ?? 0L,
                RegistryValueKind.Binary => Convert.FromHexString(snapshot.Hex ?? ""),
                RegistryValueKind.MultiString => snapshot.Lista ?? Array.Empty<string>(),
                _ => snapshot.Texto ?? ""
            };
            _registry.Write(snapshot.Chave, snapshot.Valor, kind, data);
        }

        public static void RestartExplorer()
        {
            foreach (Process proc in Process.GetProcessesByName("explorer"))
            {
                try { proc.Kill(); } catch { }
            }

            Thread.Sleep(1200);
            if (Process.GetProcessesByName("explorer").Length == 0)
            {
                string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                Process.Start(new ProcessStartInfo(explorerPath) { UseShellExecute = true });
            }
        }
    }

    public static class OptimizationProfiles
    {
        private static readonly string[] BalancedExtraTweaks =
        {
            "desativar-gamedvr",
            "desativar-cortana",
            "desativar-copilot",
            "desativar-clicktodo",
            "sem-widgets",
            "sem-noticias-win10",
            "sem-area-transferencia-nuvem",
            "finalizar-tarefa-barra",
            "sem-atraso-inicializacao"
        };

        private static readonly string[] ExtremeExtraTweaks =
        {
            "servico-sysmain",
            "servico-windows-search",
            "servico-relatorio-erros",
            "desativar-hibernacao"
        };

        private static readonly string[] BalancedExtraBloatware =
        {
            "teams",
            "copilot",
            "cortana",
            "obterajuda",
            "familia",
            "filmes",
            "todo"
        };

        private static readonly string[] ExtremeExtraBloatware =
        {
            "celular",
            "outlook",
            "sticky",
            "gravador",
            "widgets"
        };

        public static IReadOnlyList<OptimizationProfile> BuildProfiles(
            IReadOnlyList<TweakDefinition> tweaks,
            IReadOnlyList<BloatwareDefinition> bloatware)
        {
            var availableTweakIds = tweaks.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var protectedBloatIds = bloatware
                .Where(b => b.Level == BloatwareRecommendation.KeepProtected)
                .Select(b => b.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var availableBloatIds = bloatware.Select(b => b.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var lightTweaks = tweaks.Where(t => t.Recomendado).Select(t => t.Id).ToList();
            var lightBloat = bloatware
                .Where(b => b.Level == BloatwareRecommendation.CanRemove)
                .Select(b => b.Id)
                .ToList();

            var balancedTweaks = MergeIds(availableTweakIds, lightTweaks, BalancedExtraTweaks);
            var balancedBloat = MergeIds(availableBloatIds, lightBloat, BalancedExtraBloatware)
                .Where(id => !protectedBloatIds.Contains(id))
                .ToList();

            var extremeTweaks = MergeIds(availableTweakIds, balancedTweaks, ExtremeExtraTweaks);
            var extremeBloat = MergeIds(availableBloatIds, balancedBloat, ExtremeExtraBloatware)
                .Where(id => !protectedBloatIds.Contains(id))
                .ToList();

            return new List<OptimizationProfile>
            {
                new()
                {
                    Id = "leve",
                    Nome = "Leve",
                    Resumo = "Remove propagandas/jogos pré-instalados e aplica apenas ajustes seguros recomendados.",
                    Detalhes = "Ideal para qualquer PC. Preserva recursos visuais, pesquisa e serviços padrão do Windows, aplicando backup automático para reversão.",
                    AccentColor = "#10B981",
                    TweakIds = lightTweaks,
                    BloatwareIds = lightBloat
                },
                new()
                {
                    Id = "equilibrado",
                    Nome = "Equilibrado",
                    Resumo = "Leve + desativa GameDVR, Copilot, Cortana, Widgets e apps secundários.",
                    Detalhes = "Ótimo equilíbrio entre privacidade, fluidez e compatibilidade para a maioria dos desktops e notebooks.",
                    AccentColor = "#6366F1",
                    TweakIds = balancedTweaks,
                    BloatwareIds = balancedBloat
                },
                new()
                {
                    Id = "extremo",
                    Nome = "Extremo (PC fraco)",
                    Resumo = "Equilibrado + reduz serviços pesados (SysMain, Search, Relatório de Erros e Hibernação).",
                    Detalhes = "Indicado para máquinas com pouca RAM ou HD mecânico. Mantém Defender, Firewall e Windows Update sempre ativos. Tudo reversível na Central Desfazer.",
                    AccentColor = "#F59E0B",
                    TweakIds = extremeTweaks,
                    BloatwareIds = extremeBloat
                }
            };
        }

        private static List<string> MergeIds(HashSet<string> validIds, params IEnumerable<string>[] lists)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var list in lists)
            {
                foreach (string id in list)
                {
                    if (validIds.Contains(id) && seen.Add(id))
                    {
                        result.Add(id);
                    }
                }
            }
            return result;
        }
    }
}

