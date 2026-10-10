using Basis.Network.Core;
using Steamworks;
using System;
using System.Globalization;
using System.Threading;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Network.Steam
{
    [AutoStaticsCleanup]
    public static partial class BasisSteamClient
    {
        public const string ConnectLobbyArgument = "+connect_lobby";
        public static uint AppIdOverride;
        public static event Action Initialized;
        public static event Action ShuttingDown;
        public static string LastError => BasisSteamHost.LastError;
        public static bool IsReady => SteamClient.IsValid;
        public static bool IsMainThread => BasisSteamHost.IsSteamThread;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Boot()
        {
            BasisSteamHost.ClaimThread();
            BasisSteamHost.Attach("client", null, EnsureInitialized, () => SteamClient.IsValid, () => SteamClient.SteamId.Value, () => SteamClient.IsValid && SteamClient.IsLoggedOn);
            BasisSteamTransport.Register();
            BasisNetworkStackRegistry.RegisterPump(Pump);
            Application.quitting -= Shutdown;
            Application.quitting += Shutdown;
            Dispatch.OnException = OnDispatchException;
            if (LaunchedBySteam())
            {
                EnsureInitialized();
            }
        }
        public static bool LaunchedBySteam()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(BasisSteamHost.AppIdEnvironmentVariable)))
            {
                return true;
            }
            return TryGetLaunchLobby(out _);
        }
        public static bool TryGetLaunchLobby(out ulong lobbyId)
        {
            lobbyId = 0;
            string[] args = Environment.GetCommandLineArgs();
            for (int index = 0; index < args.Length - 1; index++)
            {
                if (string.Equals(args[index], ConnectLobbyArgument, StringComparison.OrdinalIgnoreCase))
                {
                    return ulong.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out lobbyId) && lobbyId != 0;
                }
            }
            return false;
        }
        public static uint ResolveAppId()
        {
            return BasisSteamHost.ResolveAppId(AppIdOverride);
        }
        public static bool EnsureInitialized()
        {
            if (SteamClient.IsValid)
            {
                return true;
            }
            if (!BasisSteamHost.IsSteamThread)
            {
                BasisSteamHost.LastError = "Steam can only be started from the main thread.";
                return false;
            }
            uint appId = ResolveAppId();
            try
            {
                SteamClient.Init(appId, false);
            }
            catch (Exception ex)
            {
                BasisSteamHost.LastError = ex.Message;
                BasisDebug.LogWarning($"Steam could not start for app {appId}: {ex.Message}", BasisDebug.LogTag.Networking);
                return false;
            }
            BasisSteamHost.LastError = string.Empty;
            BasisSteamTransportConfig config = BasisSteamTransport.Config;
            SteamNetworkingUtils.ConnectionTimeout = config.ConnectTimeoutMs;
            SteamNetworkingUtils.Timeout = config.DisconnectTimeoutMs;
            SteamNetworkingUtils.SendBufferSize = config.SendBufferBytes;
            SteamNetworkingUtils.InitRelayNetworkAccess();
            BasisDebug.Log($"Steam started for app {appId} as {SteamClient.Name}", BasisDebug.LogTag.Networking);
            try
            {
                Initialized?.Invoke();
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Steam Initialized handler failed: {ex}", BasisDebug.LogTag.Networking);
            }
            return true;
        }
        public static void RunOnMainThread(Action action)
        {
            BasisSteamHost.Run(action);
        }
        public static void Pump()
        {
            BasisSteamHost.Drain();
            if (!SteamClient.IsValid)
            {
                return;
            }
            SteamClient.RunCallbacks();
            BasisSteamHost.Poll();
        }
        public static void Shutdown()
        {
            Application.quitting -= Shutdown;
            try
            {
                ShuttingDown?.Invoke();
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Steam ShuttingDown handler failed: {ex}", BasisDebug.LogTag.Networking);
            }
            BasisSteamHost.StopAll();
            BasisSteamHost.Drain();
            if (SteamClient.IsValid)
            {
                SteamClient.Shutdown();
            }
        }
        private static void OnDispatchException(Exception ex)
        {
            BasisDebug.LogError($"Steam callback failed: {ex}", BasisDebug.LogTag.Networking);
        }
    }
}
