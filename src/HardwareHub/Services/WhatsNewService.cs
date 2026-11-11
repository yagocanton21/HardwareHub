using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HardwareHub.Services
{
    public class VersionReleaseNotes
    {
        [JsonPropertyName("versao")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public string Date { get; set; } = string.Empty;

        [JsonPropertyName("novidades")]
        public List<string> Novidades { get; set; } = new();

        [JsonPropertyName("melhorias")]
        public List<string> Melhorias { get; set; } = new();

        [JsonPropertyName("correcoes")]
        public List<string> Correcoes { get; set; } = new();

        [JsonIgnore]
        public bool IsPendingUpdate { get; set; }

        [JsonIgnore]
        public bool IsCurrentVersion { get; set; }

        [JsonIgnore]
        public string VersionLabel => $"v{Version}";

        [JsonIgnore]
        public string BadgeText =>
            IsPendingUpdate ? "DISPONÍVEL PARA ATUALIZAR" :
            IsCurrentVersion ? "VERSÃO INSTALADA" : string.Empty;

        [JsonIgnore]
        public bool HasBadge => !string.IsNullOrEmpty(BadgeText);

        [JsonIgnore]
        public bool HasNovidades => Novidades.Count > 0;

        [JsonIgnore]
        public bool HasMelhorias => Melhorias.Count > 0;

        [JsonIgnore]
        public bool HasCorrecoes => Correcoes.Count > 0;
    }

    public sealed class PostUpdateCheckResult
    {
        public bool WasUpdated { get; init; }
        public Version? PreviousVersion { get; init; }
        public Version CurrentVersion { get; init; } = new(1, 0, 0);
        public List<VersionReleaseNotes> VersionsToShow { get; init; } = new();
    }

    /// <summary>
    /// Gerencia o histórico estruturado de novidades (changelog embutido),
    /// leitura de release notes do GitHub antes de atualizar e detecção de atualização recém-aplicada.
    /// </summary>
    public class WhatsNewService
    {
        private const string ResourceName = "HardwareHub.Resources.Data.novidades.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static string DefaultVersionMarkerPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HardwareHub",
            "ultima-versao.txt");

        public Version GetCurrentVersion()
        {
            var asmVer = Assembly.GetExecutingAssembly().GetName().Version;
            if (asmVer != null && (asmVer.Major > 0 || asmVer.Minor > 0 || asmVer.Build > 0))
            {
                return NormalizeVersion(asmVer);
            }

            return NormalizeVersion(UpdateService.GetCurrentVersion());
        }

        public List<VersionReleaseNotes> LoadEmbeddedNotes()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(ResourceName);
                if (stream == null)
                    return new List<VersionReleaseNotes>();

                using var reader = new StreamReader(stream);
                string json = reader.ReadToEnd();
                var list = JsonSerializer.Deserialize<List<VersionReleaseNotes>>(json, JsonOptions)
                           ?? new List<VersionReleaseNotes>();

                var current = NormalizeVersion(GetCurrentVersion());
                foreach (var item in list)
                {
                    item.Novidades ??= new List<string>();
                    item.Melhorias ??= new List<string>();
                    item.Correcoes ??= new List<string>();
                    if (TryParseNormalizedVersion(item.Version, out var parsed))
                    {
                        item.IsCurrentVersion = parsed == current;
                    }
                }

                return list
                    .OrderByDescending(v => TryParseNormalizedVersion(v.Version, out var pv) ? pv : new Version(0, 0, 0))
                    .ToList();
            }
            catch
            {
                return new List<VersionReleaseNotes>();
            }
        }

        /// <summary>
        /// Verifica se a versão atual é superior à última versão executada em %LocalAppData%\HardwareHub\ultima-versao.txt.
        /// Sempre atualiza o marcador para a versão corrente.
        /// </summary>
        public PostUpdateCheckResult CheckAndMarkPostUpdate(
            string? versionFilePath = null,
            Version? currentVersionOverride = null,
            IReadOnlyList<VersionReleaseNotes>? notesOverride = null)
        {
            string markerPath = string.IsNullOrWhiteSpace(versionFilePath)
                ? DefaultVersionMarkerPath
                : versionFilePath;

            var current = NormalizeVersion(currentVersionOverride ?? GetCurrentVersion());
            Version? previous = null;

            try
            {
                if (File.Exists(markerPath))
                {
                    string raw = File.ReadAllText(markerPath).Trim();
                    if (TryParseNormalizedVersion(raw, out var parsed))
                    {
                        previous = parsed;
                    }
                }
            }
            catch
            {
                previous = null;
            }

            try
            {
                string? dir = Path.GetDirectoryName(markerPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(markerPath, current.ToString(3));
            }
            catch
            {
                // Ignora falhas de permissão ao gravar marcador
            }

            bool wasUpdated = previous != null && previous < current;
            var versionsToShow = new List<VersionReleaseNotes>();

            if (wasUpdated && previous != null)
            {
                var allNotes = (notesOverride ?? LoadEmbeddedNotes()).ToList();
                versionsToShow = allNotes
                    .Where(n => TryParseNormalizedVersion(n.Version, out var nv) && nv > previous && nv <= current)
                    .OrderByDescending(n => TryParseNormalizedVersion(n.Version, out var nv) ? nv : new Version(0, 0, 0))
                    .Take(3)
                    .ToList();

                if (versionsToShow.Count == 0)
                {
                    versionsToShow.Add(new VersionReleaseNotes
                    {
                        Version = current.ToString(3),
                        Date = DateTime.Now.ToString("dd/MM/yyyy"),
                        IsCurrentVersion = true,
                        Melhorias = new List<string>
                        {
                            $"Atualização para a versão v{current.ToString(3)} concluída com sucesso.",
                            "Melhorias gerais de estabilidade e desempenho aplicadas."
                        }
                    });
                }
            }

            return new PostUpdateCheckResult
            {
                WasUpdated = wasUpdated,
                PreviousVersion = previous,
                CurrentVersion = current,
                VersionsToShow = versionsToShow
            };
        }

        /// <summary>
        /// Converte o corpo Markdown de uma GitHub Release (antes de baixar o .exe) em seções organizadas.
        /// </summary>
        public VersionReleaseNotes ParseGitHubReleaseBody(string version, string? date, string? markdownBody)
        {
            string cleanVersion = string.IsNullOrWhiteSpace(version)
                ? GetCurrentVersion().ToString(3)
                : version.Trim().TrimStart('v', 'V');

            var notes = new VersionReleaseNotes
            {
                Version = cleanVersion,
                Date = string.IsNullOrWhiteSpace(date) ? DateTime.Now.ToString("dd/MM/yyyy") : date.Trim(),
                IsPendingUpdate = true
            };

            if (string.IsNullOrWhiteSpace(markdownBody))
            {
                notes.Melhorias.Add("Melhorias de desempenho, estabilidade e correções nesta versão.");
                return notes;
            }

            string currentBucket = "novidades";
            var lines = markdownBody.Replace("\r\n", "\n").Split('\n');

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("---"))
                    continue;

                if (line.StartsWith("#"))
                {
                    string heading = line.TrimStart('#').Trim().ToLowerInvariant();
                    if (heading.Contains("corre") || heading.Contains("fix") || heading.Contains("bug"))
                        currentBucket = "correcoes";
                    else if (heading.Contains("melhor") || heading.Contains("improv") || heading.Contains("altera") || heading.Contains("otimiz"))
                        currentBucket = "melhorias";
                    else
                        currentBucket = "novidades";
                    continue;
                }

                // Títulos em negrito sem '#' (ex: "**Correções:**")
                string normalizedHeading = line.Replace("*", "").Trim().TrimEnd(':').ToLowerInvariant();
                if (!line.StartsWith("-") && !line.StartsWith("*") && !line.StartsWith("•"))
                {
                    if (normalizedHeading is "novidades" or "o que há de novo" or "what's new" or "recursos")
                    {
                        currentBucket = "novidades";
                        continue;
                    }
                    if (normalizedHeading is "melhorias" or "improvements" or "otimizações")
                    {
                        currentBucket = "melhorias";
                        continue;
                    }
                    if (normalizedHeading is "correções" or "correcoes" or "bug fixes" or "fixes")
                    {
                        currentBucket = "correcoes";
                        continue;
                    }
                }

                string item = CleanMarkdownBullet(line);
                if (string.IsNullOrWhiteSpace(item))
                    continue;

                string targetBucket = currentBucket;
                string lowerItem = item.ToLowerInvariant();
                if (lowerItem.StartsWith("[fix]") || lowerItem.StartsWith("fix:") || lowerItem.StartsWith("correção:") || lowerItem.StartsWith("corrigido"))
                    targetBucket = "correcoes";
                else if (lowerItem.StartsWith("[melhoria]") || lowerItem.StartsWith("melhoria:"))
                    targetBucket = "melhorias";

                switch (targetBucket)
                {
                    case "correcoes":
                        notes.Correcoes.Add(item);
                        break;
                    case "melhorias":
                        notes.Melhorias.Add(item);
                        break;
                    default:
                        notes.Novidades.Add(item);
                        break;
                }
            }

            if (!notes.HasNovidades && !notes.HasMelhorias && !notes.HasCorrecoes)
            {
                notes.Melhorias.Add("Melhorias de desempenho, estabilidade e correções nesta versão.");
            }

            return notes;
        }

        private static string CleanMarkdownBullet(string line)
        {
            string cleaned = Regex.Replace(line, @"^\s*(?:[-*•]+|\d+\.)\s+", string.Empty);
            cleaned = cleaned.Replace("**", string.Empty).Replace("__", string.Empty).Trim();
            return cleaned;
        }

        public static bool TryParseNormalizedVersion(string? raw, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            string trimmed = raw.Trim().TrimStart('v', 'V');
            int plusIndex = trimmed.IndexOfAny(new[] { '+', '-' });
            if (plusIndex > 0)
                trimmed = trimmed[..plusIndex];

            if (!Version.TryParse(trimmed, out var parsed) || parsed == null)
                return false;

            version = NormalizeVersion(parsed);
            return true;
        }

        private static Version NormalizeVersion(Version v)
        {
            return new Version(
                Math.Max(0, v.Major),
                Math.Max(0, v.Minor),
                Math.Max(0, v.Build));
        }
    }
}

