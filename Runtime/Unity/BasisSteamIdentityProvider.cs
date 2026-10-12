using Basis.Network.Core;
using Steamworks;
using System.Globalization;

namespace Basis.Network.Steam
{
    public sealed class BasisSteamIdentityProvider : IPlayerIdentityProvider
    {
        public string ProviderId => BasisSteamTransport.IdentityProviderId;
        public PlayerIdentity GetOrCreate()
        {
            if (!BasisSteamClient.EnsureInitialized())
            {
                return null;
            }
            return new PlayerIdentity
            {
                Uuid = BasisSteamTransport.IdentityPrefix + SteamClient.SteamId.Value.ToString(CultureInfo.InvariantCulture),
                Provider = ProviderId,
            };
        }
    }
}
