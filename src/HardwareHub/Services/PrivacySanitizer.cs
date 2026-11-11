using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace HardwareHub.Services
{
    /// <summary>
    /// Remove informações identificáveis (nome de usuário, nome do PC, domínio e caminhos pessoais)
    /// antes de copiar ou exportar relatórios técnicos.
    /// </summary>
    public static class PrivacySanitizer
    {
        private static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "user", "usuario", "usuário", "admin", "administrador", "administrator", "pc", "home",
            "windows", "default", "public", "owner", "proprietario", "proprietário", "dono",
            "computador", "desktop", "notebook", "teste", "test", "aluno", "cliente"
        };

        private static readonly Regex UserPathRegex = new(
            @"(?i)(\b[A-Z]:\\(?:Users|Usuários|Documents and Settings)\\)(?!\[)[^\\/""<>|\r\n]+",
            RegexOptions.Compiled);

        private static readonly Regex EscapedUserPathRegex = new(
            @"(?i)(\b[A-Z]:\\\\(?:Users|Usuários)\\\\)(?!\[)[^\\/""<>|\r\n]+",
            RegexOptions.Compiled);

        private static readonly Lazy<List<(Regex Pattern, string Replacement)>> SystemPatterns =
            new(BuildSystemPatterns);

        public static string Sanitize(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;

            foreach (var (pattern, replacement) in SystemPatterns.Value)
                text = pattern.Replace(text, replacement);

            return text;
        }

        public static string SanitizeWithTokens(string? text, string? userName, string? machineName, string? domainName = null)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;

            var patterns = BuildPatterns(new[]
            {
                (userName, "[usuário]"),
                (machineName, "[nome do PC]"),
                (domainName, "[domínio]")
            });

            foreach (var (pattern, replacement) in patterns)
                text = pattern.Replace(text, replacement);

            return text;
        }

        private static List<(Regex Pattern, string Replacement)> BuildSystemPatterns()
        {
            var tokens = new List<(string? Token, string Replacement)>();
            try { tokens.Add((Environment.UserName, "[usuário]")); } catch { }
            try
            {
                string profileName = Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'));
                tokens.Add((profileName, "[usuário]"));
            }
            catch { }
            try { tokens.Add((Environment.MachineName, "[nome do PC]")); } catch { }
            try { tokens.Add((Dns.GetHostName(), "[nome do PC]")); } catch { }
            try
            {
                if (!string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    tokens.Add((Environment.UserDomainName, "[domínio]"));
            }
            catch { }

            return BuildPatterns(tokens);
        }

        private static List<(Regex Pattern, string Replacement)> BuildPatterns(IEnumerable<(string? Token, string Replacement)> rawTokens)
        {
            var unique = new List<(string Token, string Replacement)>();
            foreach (var (raw, replacement) in rawTokens)
            {
                string? token = raw?.Trim();
                if (string.IsNullOrEmpty(token) || token.Length < 3 || GenericTokens.Contains(token))
                    continue;
                if (!unique.Any(x => x.Token.Equals(token, StringComparison.OrdinalIgnoreCase)))
                    unique.Add((token, replacement));
            }

            var list = new List<(Regex, string)>
            {
                (UserPathRegex, "$1[usuário]"),
                (EscapedUserPathRegex, "$1[usuário]")
            };

            foreach (var (token, replacement) in unique.OrderByDescending(x => x.Token.Length))
            {
                string expr = token.Length >= 5
                    ? Regex.Escape(token)
                    : $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(token)}(?![\p{{L}}\p{{N}}_])";

                list.Add((new Regex(expr, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), replacement));
            }

            return list;
        }
    }
}

