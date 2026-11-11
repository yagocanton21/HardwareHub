using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    public interface IRegistryBackend
    {
        bool KeyExists(string fullPath);
        RegReadResult Read(string fullPath, string? valueName);
        void Write(string fullPath, string? valueName, RegistryValueKind kind, object data);
        void DeleteValue(string fullPath, string? valueName);
        void DeleteKeyTree(string fullPath);
        void DeleteEmptyKeysUpTo(string fullPath, string rootBoundary);
        string? FirstMissingAncestor(string fullPath);
        IReadOnlyList<(string Name, object? Data)> ReadAllValues(string fullPath);
        IReadOnlyList<string> GetSubKeyNames(string fullPath);
    }

    public sealed class WindowsRegistryBackend : IRegistryBackend
    {
        public static WindowsRegistryBackend Instance { get; } = new();

        public static (RegistryKey Root, string SubKey) ParsePath(string fullPath)
        {
            int sep = fullPath.IndexOf('\\');
            string hivePart = (sep < 0 ? fullPath : fullPath[..sep]).ToUpperInvariant();
            string subKey = sep < 0 ? "" : fullPath[(sep + 1)..];

            RegistryHive hive = hivePart switch
            {
                "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
                "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
                "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
                "HKU" or "HKEY_USERS" => RegistryHive.Users,
                _ => throw new ArgumentException($"Hive de registro desconhecida: {hivePart}")
            };

            return (RegistryKey.OpenBaseKey(hive, RegistryView.Registry64), subKey);
        }

        public bool KeyExists(string fullPath)
        {
            var (root, subKey) = ParsePath(fullPath);
            using (root)
            using (var key = root.OpenSubKey(subKey))
            {
                return key != null;
            }
        }

        public RegReadResult Read(string fullPath, string? valueName)
        {
            var (root, subKey) = ParsePath(fullPath);
            using (root)
            using (var key = root.OpenSubKey(subKey))
            {
                if (key == null) return RegReadResult.Missing;
                string name = valueName ?? "";
                object? val = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                return val == null
                    ? RegReadResult.Missing
                    : new RegReadResult(true, key.GetValueKind(name), val);
            }
        }

        public void Write(string fullPath, string? valueName, RegistryValueKind kind, object data)
        {
            var (root, subKey) = ParsePath(fullPath);
            using (root)
            using (var key = root.CreateSubKey(subKey, writable: true))
            {
                key.SetValue(valueName ?? "", data, kind);
            }
        }

        public void DeleteValue(string fullPath, string? valueName)
        {
            var (root, subKey) = ParsePath(fullPath);
            using (root)
            using (var key = root.OpenSubKey(subKey, writable: true))
            {
                key?.DeleteValue(valueName ?? "", throwOnMissingValue: false);
            }
        }

        public void DeleteKeyTree(string fullPath)
        {
            var (root, subKey) = ParsePath(fullPath);
            using (root)
            {
                root.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            }
        }

        public string? FirstMissingAncestor(string fullPath)
        {
            string[] parts = fullPath.Split('\\');
            for (int i = 2; i <= parts.Length; i++)
            {
                string candidate = string.Join('\\', parts[..i]);
                if (!KeyExists(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }

        public void DeleteEmptyKeysUpTo(string fullPath, string rootBoundary)
        {
            string current = fullPath.TrimEnd('\\');
            string boundary = rootBoundary.TrimEnd('\\');
            if (!current.Equals(boundary, StringComparison.OrdinalIgnoreCase) &&
                !current.StartsWith(boundary + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            while (true)
            {
                var (root, subKey) = ParsePath(current);
                using (root)
                {
                    using (var key = root.OpenSubKey(subKey))
                    {
                        if (key != null && (key.ValueCount > 0 || key.SubKeyCount > 0))
                        {
                            break;
                        }
                    }

                    if (KeyExists(current))
                    {
                        root.DeleteSubKey(subKey, throwOnMissingSubKey: false);
                    }
                }

                if (current.Equals(boundary, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                int lastSlash = current.LastIndexOf('\\');
                if (lastSlash <= 0) break;
                current = current[..lastSlash];
            }
        }

        public IReadOnlyList<(string Name, object? Data)> ReadAllValues(string fullPath)
        {
            var result = new List<(string Name, object? Data)>();
            try
            {
                var (root, subKey) = ParsePath(fullPath);
                using (root)
                using (var key = root.OpenSubKey(subKey))
                {
                    if (key == null) return result;
                    foreach (string name in key.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(name)) continue;
                        result.Add((name, key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)));
                    }
                }
            }
            catch { }
            return result;
        }

        public IReadOnlyList<string> GetSubKeyNames(string fullPath)
        {
            try
            {
                var (root, subKey) = ParsePath(fullPath);
                using (root)
                using (var key = root.OpenSubKey(subKey))
                {
                    return key?.GetSubKeyNames() ?? Array.Empty<string>();
                }
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    public static class RegistryValueConverter
    {
        public static RegistryValueKind ParseKind(string? kind) => (kind ?? "dword").ToLowerInvariant() switch
        {
            "dword" => RegistryValueKind.DWord,
            "qword" => RegistryValueKind.QWord,
            "string" or "sz" => RegistryValueKind.String,
            "expand_sz" => RegistryValueKind.ExpandString,
            "multi_sz" => RegistryValueKind.MultiString,
            "binary" => RegistryValueKind.Binary,
            _ => throw new ArgumentException($"Tipo de registro desconhecido: {kind}")
        };

        public static object ConvertData(RegistryValueKind kind, string? data) => kind switch
        {
            RegistryValueKind.DWord => unchecked((int)ParseNumber(data)),
            RegistryValueKind.QWord => unchecked((long)ParseNumber(data)),
            RegistryValueKind.Binary => Convert.FromHexString((data ?? "").Replace(" ", "").Replace(",", "")),
            RegistryValueKind.MultiString => string.IsNullOrEmpty(data) ? Array.Empty<string>() : data.Split('|'),
            _ => data ?? ""
        };

        private static ulong ParseNumber(string? s)
        {
            string trimmed = (s ?? "0").Trim();
            return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt64(trimmed[2..], 16)
                : ulong.Parse(trimmed);
        }

        public static bool Matches(RegReadResult current, RegistryValueKind kind, object expected)
        {
            if (!current.Exists) return false;
            return kind switch
            {
                RegistryValueKind.DWord => current.Data is int i && i == (int)expected,
                RegistryValueKind.QWord => current.Data is long l && l == (long)expected,
                RegistryValueKind.Binary => current.Data is byte[] curBytes && expected is byte[] expBytes && curBytes.AsSpan().SequenceEqual(expBytes),
                RegistryValueKind.MultiString => current.Data is string[] curArr && expected is string[] expArr && curArr.AsSpan().SequenceEqual(expArr),
                _ => string.Equals(current.Data?.ToString(), expected.ToString(), StringComparison.OrdinalIgnoreCase)
            };
        }
    }

    public sealed class ServiceStartupHelper
    {
        private readonly IRegistryBackend _registry;

        public ServiceStartupHelper(IRegistryBackend? registry = null)
        {
            _registry = registry ?? WindowsRegistryBackend.Instance;
        }

        private static string ServiceKey(string serviceName) => $@"HKLM\SYSTEM\CurrentControlSet\Services\{serviceName}";

        public bool Exists(string serviceName) => _registry.KeyExists(ServiceKey(serviceName));

        public (int? Start, int? Delayed) Get(string serviceName)
        {
            var start = _registry.Read(ServiceKey(serviceName), "Start");
            var delayed = _registry.Read(ServiceKey(serviceName), "DelayedAutostart");
            return (start.Data as int?, delayed.Data as int?);
        }

        public static (int Start, int Delayed) ParseStartupMode(string mode) => mode.ToLowerInvariant() switch
        {
            "boot" => (0, 0),
            "system" => (1, 0),
            "auto" => (2, 0),
            "delayed" => (2, 1),
            "manual" => (3, 0),
            "disabled" => (4, 0),
            _ => throw new ArgumentException($"Tipo de inicialização desconhecido: {mode}")
        };

        public bool Matches(string serviceName, string mode)
        {
            if (!Exists(serviceName)) return false;
            var (curStart, curDelayed) = Get(serviceName);
            var (expStart, expDelayed) = ParseStartupMode(mode);
            if (curStart != expStart) return false;
            return expStart != 2 || curDelayed.GetValueOrDefault() == expDelayed;
        }

        public void Set(string serviceName, int start, int? delayed)
        {
            if (!Exists(serviceName)) return;
            string key = ServiceKey(serviceName);
            _registry.Write(key, "Start", RegistryValueKind.DWord, start);
            if (delayed.HasValue)
            {
                _registry.Write(key, "DelayedAutostart", RegistryValueKind.DWord, delayed.Value);
            }
            else
            {
                _registry.DeleteValue(key, "DelayedAutostart");
            }
        }
    }
}

