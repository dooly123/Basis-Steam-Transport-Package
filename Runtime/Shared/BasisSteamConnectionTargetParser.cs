using Basis.Network.Core;
using System;
using System.Globalization;

namespace Basis.Network.Steam
{
    public sealed class BasisSteamConnectionTargetParser : IConnectionTargetParser
    {
        public const string Scheme = "steam:";
        public static bool IsSteamAddress(string raw)
        {
            return !string.IsNullOrWhiteSpace(raw) && raw.Trim().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);
        }
        public static bool TryParseSteamId(string text, out ulong steamId)
        {
            steamId = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            string value = StripScheme(StripPassword(text, out _));
            int separator = value.IndexOf(':');
            if (separator >= 0)
            {
                value = value.Substring(0, separator);
            }
            return ulong.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out steamId) && steamId != 0;
        }
        public static string Format(ulong steamId, int virtualPort)
        {
            string id = steamId.ToString(CultureInfo.InvariantCulture);
            return virtualPort == 0 ? Scheme + id : Scheme + id + ":" + virtualPort.ToString(CultureInfo.InvariantCulture);
        }
        public void Parse(ConnectionTarget target)
        {
            if (target == null)
            {
                return;
            }
            string value = StripScheme(StripPassword(target.Raw, out string password));
            string port = "0";
            int separator = value.IndexOf(':');
            if (separator >= 0)
            {
                port = value.Substring(separator + 1).Trim();
                value = value.Substring(0, separator);
            }
            target.Set(ConnectionTarget.Keys.Address, value.Trim());
            target.Set(ConnectionTarget.Keys.Port, int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0 && parsed <= ushort.MaxValue ? parsed.ToString(CultureInfo.InvariantCulture) : "0");
            target.Set(ConnectionTarget.Keys.Password, password);
        }
        public string Format(ConnectionTarget target)
        {
            if (target == null)
            {
                return string.Empty;
            }
            if (!TryParseSteamId(target.Get(ConnectionTarget.Keys.Address, string.Empty), out ulong steamId))
            {
                return target.Raw ?? string.Empty;
            }
            int.TryParse(target.Get(ConnectionTarget.Keys.Port, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port);
            string password = target.Get(ConnectionTarget.Keys.Password, string.Empty);
            string value = Format(steamId, port);
            return string.IsNullOrEmpty(password) ? value : value + "#" + password;
        }
        private static string StripPassword(string text, out string password)
        {
            password = string.Empty;
            string value = text ?? string.Empty;
            int hash = value.IndexOf('#');
            if (hash < 0)
            {
                return value;
            }
            password = value.Substring(hash + 1);
            return value.Substring(0, hash);
        }
        private static string StripScheme(string text)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(Scheme.Length).TrimStart('/');
            }
            return value;
        }
    }
}
