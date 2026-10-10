using Basis.Network.Core;
using Basis.Network.Steam;
using Steamworks;
using System;
using System.Reflection;
using System.Threading;

[assembly: BasisNetworkPackage(typeof(BasisSteamServerPackage))]

namespace Basis.Network.Steam
{
    public sealed class BasisSteamServerPackage : IBasisNetworkPackage
    {
        public void Initialize()
        {
            BasisSteamTransport.Register();
            BasisSteamServer.Attach();
        }
    }

    public static class BasisSteamServer
    {
        private const int StartTimeoutMs = 15000;
        private static readonly object gate = new object();
        private static readonly ManualResetEventSlim started = new ManualResetEventSlim(false);
        private static Thread thread;
        private static volatile bool running;
        public static void Attach()
        {
            BasisSteamHost.Attach("game server", Prepare, EnsureStarted, () => SteamServer.IsValid, () => SteamServer.SteamId.Value, () => SteamServer.IsValid && SteamServer.LoggedOn);
        }
        public static void Prepare()
        {
            lock (gate)
            {
                if (thread != null)
                {
                    return;
                }
                running = true;
                thread = new Thread(Loop) { IsBackground = true, Name = "Basis Steam" };
                thread.Start();
                AppDomain.CurrentDomain.ProcessExit += (sender, args) => Stop();
            }
        }
        public static void Stop()
        {
            Thread current;
            lock (gate)
            {
                current = thread;
                running = false;
            }
            if (current != null && current != Thread.CurrentThread)
            {
                current.Join(5000);
            }
        }
        private static bool EnsureStarted()
        {
            if (SteamServer.IsValid)
            {
                return true;
            }
            if (!BasisSteamHost.IsSteamThread)
            {
                Prepare();
                started.Wait(StartTimeoutMs);
            }
            return SteamServer.IsValid;
        }
        private static void Loop()
        {
            BasisSteamHost.ClaimThread();
            try
            {
                Start();
            }
            finally
            {
                started.Set();
            }
            while (running)
            {
                BasisSteamHost.Drain();
                if (SteamServer.IsValid)
                {
                    try
                    {
                        SteamServer.RunCallbacks();
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"Steam game server callbacks failed: {ex}");
                    }
                    BasisSteamHost.Poll();
                }
                Thread.Sleep(Math.Max(0, BasisSteamTransport.Config.ServerPollIntervalMs));
            }
            BasisSteamHost.StopAll();
            BasisSteamHost.Drain();
            if (SteamServer.IsValid)
            {
                SteamServer.LogOff();
                SteamServer.Shutdown();
            }
        }
        private static void Start()
        {
            BasisSteamTransportConfig config = BasisSteamTransport.Config;
            uint appId = BasisSteamHost.ResolveAppId(config.AppId);
            Dispatch.OnException = ex => BNL.LogError($"Steam callback failed: {ex}");
            SteamServer.OnSteamServersConnected -= OnLoggedOn;
            SteamServer.OnSteamServersConnected += OnLoggedOn;
            SteamServer.OnSteamServerConnectFailure -= OnLogOnFailed;
            SteamServer.OnSteamServerConnectFailure += OnLogOnFailed;
            SteamServer.OnSteamServersDisconnected -= OnLoggedOff;
            SteamServer.OnSteamServersDisconnected += OnLoggedOff;
            try
            {
                SteamServerInit init = new SteamServerInit("basis", "Basis") { DedicatedServer = true, Secure = false };
                SteamServer.Init(appId, init.WithQueryShareGamePort(), false);
            }
            catch (Exception ex)
            {
                BasisSteamHost.LastError = ex.Message;
                BNL.LogError($"The Steam game server could not start for app {appId}: {ex.Message}. It needs Steam's game server files: steamclient.so in ~/.steam/sdk64 on Linux, or steamclient64.dll, tier0_s64.dll and vstdlib_s64.dll next to the server on Windows without Steam installed (SteamCMD app 1007).");
                return;
            }
            BasisSteamHost.LastError = string.Empty;
            SteamServer.AdvertiseServer = false;
            SteamNetworkingUtils.ConnectionTimeout = config.ConnectTimeoutMs;
            SteamNetworkingUtils.Timeout = config.DisconnectTimeoutMs;
            SteamNetworkingUtils.SendBufferSize = config.SendBufferBytes;
            SteamNetworkingUtils.InitRelayNetworkAccess();
            LogOn(config.GameServerToken);
            BNL.Log($"Steam game server started for app {appId}; logging in.");
        }
        private static void LogOn(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                SteamServer.LogOnAnonymous();
                BNL.Log("Logging the Steam game server in anonymously, so its Steam ID changes every start. Set GameServerToken in config/transports/steam.xml to keep one.");
                return;
            }
            try
            {
                object gameServer = null;
                for (Type type = typeof(SteamServer); type != null && gameServer == null; type = type.BaseType)
                {
                    gameServer = type.GetProperty("Internal", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)?.GetValue(null);
                }
                MethodInfo logOn = gameServer?.GetType().GetMethod("LogOn", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (logOn == null)
                {
                    throw new MissingMethodException("ISteamGameServer.LogOn");
                }
                logOn.Invoke(gameServer, new object[] { token.Trim() });
            }
            catch (Exception ex)
            {
                BNL.LogError($"Could not log the Steam game server in with GameServerToken ({ex.GetBaseException().Message}); logging in anonymously instead.");
                SteamServer.LogOnAnonymous();
            }
        }
        private static void OnLoggedOn()
        {
            ulong steamId = SteamServer.SteamId.Value;
            BNL.Log($"Steam game server logged in as {steamId}. Players join with {BasisSteamConnectionTargetParser.Format(steamId, BasisSteamTransport.Config.VirtualPort)}");
        }
        private static void OnLogOnFailed(Result result, bool stillRetrying)
        {
            BNL.LogWarning($"Steam game server login failed: {result}{(stillRetrying ? ", retrying" : string.Empty)}");
        }
        private static void OnLoggedOff(Result result)
        {
            BNL.LogWarning($"Steam game server lost its connection to Steam: {result}");
        }
    }
}
