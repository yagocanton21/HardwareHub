using HardwareHub.Models;
using HardwareHub.Services;
using Microsoft.Win32;
using Xunit;

namespace HardwareHub.Tests
{
    public sealed class InMemoryRegistryBackend : IRegistryBackend
    {
        private sealed class KeyNode
        {
            public Dictionary<string, KeyNode> SubKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, (RegistryValueKind Kind, object Data)> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private readonly Dictionary<string, KeyNode> _hives = new(StringComparer.OrdinalIgnoreCase)
        {
            ["HKCU"] = new KeyNode(),
            ["HKLM"] = new KeyNode(),
            ["HKCR"] = new KeyNode(),
            ["HKU"] = new KeyNode()
        };

        public HashSet<string> ThrowOnWriteKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        private static (string Hive, string[] Segments) Split(string fullPath)
        {
            string[] parts = fullPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) throw new ArgumentException("Caminho vazio.");
            string hive = parts[0].ToUpperInvariant() switch
            {
                "HKEY_CURRENT_USER" => "HKCU",
                "HKEY_LOCAL_MACHINE" => "HKLM",
                "HKEY_CLASSES_ROOT" => "HKCR",
                "HKEY_USERS" => "HKU",
                var h => h
            };
            return (hive, parts[1..]);
        }

        private KeyNode? FindNode(string fullPath)
        {
            var (hive, segs) = Split(fullPath);
            if (!_hives.TryGetValue(hive, out var cur)) return null;
            foreach (string seg in segs)
            {
                if (!cur.SubKeys.TryGetValue(seg, out cur)) return null;
            }
            return cur;
        }

        public bool KeyExists(string fullPath) => FindNode(fullPath) != null;

        public RegReadResult Read(string fullPath, string? valueName)
        {
            var node = FindNode(fullPath);
            if (node == null) return RegReadResult.Missing;
            string name = valueName ?? "";
            return node.Values.TryGetValue(name, out var entry)
                ? new RegReadResult(true, entry.Kind, entry.Data)
                : RegReadResult.Missing;
        }

        public void Write(string fullPath, string? valueName, RegistryValueKind kind, object data)
        {
            if (ThrowOnWriteKeys.Contains(fullPath))
                throw new UnauthorizedAccessException($"Simulated write failure on {fullPath}");

            var (hive, segs) = Split(fullPath);
            var cur = _hives[hive];
            foreach (string seg in segs)
            {
                if (!cur.SubKeys.TryGetValue(seg, out var next))
                {
                    next = new KeyNode();
                    cur.SubKeys[seg] = next;
                }
                cur = next;
            }
            cur.Values[valueName ?? ""] = (kind, data);
        }

        public void DeleteValue(string fullPath, string? valueName)
        {
            var node = FindNode(fullPath);
            node?.Values.Remove(valueName ?? "");
        }

        public void DeleteKeyTree(string fullPath)
        {
            var (hive, segs) = Split(fullPath);
            if (segs.Length == 0 || !_hives.TryGetValue(hive, out var cur)) return;
            for (int i = 0; i < segs.Length - 1; i++)
            {
                if (!cur.SubKeys.TryGetValue(segs[i], out cur)) return;
            }
            cur.SubKeys.Remove(segs[^1]);
        }

        public string? FirstMissingAncestor(string fullPath)
        {
            string[] parts = fullPath.Split('\\');
            for (int i = 2; i <= parts.Length; i++)
            {
                string candidate = string.Join('\\', parts[..i]);
                if (!KeyExists(candidate)) return candidate;
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
                var node = FindNode(current);
                if (node != null && (node.Values.Count > 0 || node.SubKeys.Count > 0))
                    break;

                if (node != null) DeleteKeyTree(current);
                if (current.Equals(boundary, StringComparison.OrdinalIgnoreCase)) break;

                int lastSlash = current.LastIndexOf('\\');
                if (lastSlash <= 0) break;
                current = current[..lastSlash];
            }
        }

        public IReadOnlyList<(string Name, object? Data)> ReadAllValues(string fullPath)
        {
            var node = FindNode(fullPath);
            if (node == null) return Array.Empty<(string Name, object? Data)>();
            return node.Values
                .Where(kv => !string.IsNullOrEmpty(kv.Key))
                .Select(kv => (kv.Key, (object?)kv.Value.Data))
                .ToList();
        }

        public IReadOnlyList<string> GetSubKeyNames(string fullPath)
        {
            var node = FindNode(fullPath);
            return node == null ? Array.Empty<string>() : node.SubKeys.Keys.ToList();
        }
    }
}

