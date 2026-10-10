using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;
#if UNITY_5_3_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Basis.Network.Steam
{
#if UNITY_5_3_OR_NEWER
    [AutoStaticsCleanup]
#endif
    public static partial class BasisSteamHost
    {
        public const uint BasisLabsAppId = 3157090;
        public const string AppIdEnvironmentVariable = "SteamAppId";
        public const string AppIdFileName = "steam_appid.txt";
        public static readonly ConcurrentQueue<Action> Actions = new ConcurrentQueue<Action>();
        public static string LastError = string.Empty;
        public static string Mode = string.Empty;
        private static int threadId = -1;
        private static Action prepare;
        private static Func<bool> start, valid, loggedOn;
        private static Func<ulong> steamId;
        public static bool IsSteamThread => Thread.CurrentThread.ManagedThreadId == threadId;
        public static bool IsValid => valid != null && valid();
        public static bool IsLoggedOn => loggedOn != null && loggedOn();
        public static ulong LocalSteamId => IsValid && steamId != null ? steamId() : 0;
        public static void Attach(string mode, Action prepare, Func<bool> start, Func<bool> valid, Func<ulong> steamId, Func<bool> loggedOn)
        {
            Mode = mode ?? string.Empty;
            BasisSteamHost.prepare = prepare;
            BasisSteamHost.start = start;
            BasisSteamHost.valid = valid;
            BasisSteamHost.steamId = steamId;
            BasisSteamHost.loggedOn = loggedOn;
        }
        public static void ClaimThread()
        {
            threadId = Thread.CurrentThread.ManagedThreadId;
        }
        public static void Prepare()
        {
            prepare?.Invoke();
        }
        public static bool EnsureStarted()
        {
            if (IsValid)
            {
                return true;
            }
            if (start == null)
            {
                LastError = "Steam is not set up in this process.";
                return false;
            }
            return start();
        }
        public static void Run(Action action)
        {
            if (IsSteamThread)
            {
                action();
            }
            else
            {
                Actions.Enqueue(action);
            }
        }
        public static void Drain()
        {
            while (Actions.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    BNL.LogError($"Steam action failed: {ex}");
                }
            }
        }
        public static void Poll()
        {
            BasisSteamNetManager.PollAll();
            BasisSteamProbe.PollAll();
        }
        public static void StopAll()
        {
            BasisSteamNetManager.StopAll();
            BasisSteamProbe.AbortAll();
        }
        public static uint ResolveAppId(uint preferred)
        {
            if (preferred != 0)
            {
                return preferred;
            }
            if (TryParseAppId(Environment.GetEnvironmentVariable(AppIdEnvironmentVariable), out uint fromEnvironment))
            {
                return fromEnvironment;
            }
            foreach (string folder in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                try
                {
                    string path = Path.Combine(folder, AppIdFileName);
                    if (File.Exists(path) && TryParseAppId(File.ReadAllText(path), out uint fromFile))
                    {
                        return fromFile;
                    }
                }
                catch (Exception ex)
                {
                    BNL.LogWarning($"Could not read {AppIdFileName}: {ex.Message}");
                }
            }
            return BasisLabsAppId;
        }
        private static bool TryParseAppId(string text, out uint appId)
        {
            appId = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            string firstLine = text.Trim().Split('\n')[0].Trim();
            return uint.TryParse(firstLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out appId) && appId != 0;
        }
    }
}
