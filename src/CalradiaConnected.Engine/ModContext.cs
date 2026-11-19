using System;
using CalradiaConnected.Engine.Events;
using CalradiaConnected.Engine.Threading;
using CalradiaConnected.Network.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace CalradiaConnected.Engine
{
    public static class ModContext
    {
        public static IServiceProvider? ServiceProvider { get; private set; }
        public static IEngineDispatcher Dispatcher { get; private set; } = new GameThreadDispatcher();
        public static IEventBus Events { get; private set; } = new EventBus();

        public static INetworkServer? Server => ServiceProvider?.GetService<INetworkServer>();
        public static INetworkClient? Client => ServiceProvider?.GetService<INetworkClient>();

        public static bool IsServerRunning => Server != null && Server.IsRunning;
        public static bool IsClientConnected => Client != null && Client.IsConnected;
        public static string ActiveServerSaveName { get; set; } = "Calradia_Sunucusu";

        public static string SanitizeSaveFileName(string? serverTitle)
        {
            if (string.IsNullOrWhiteSpace(serverTitle))
                return "Calradia_Sunucusu";

            string normalized = serverTitle!.Trim()
                .Replace('ş', 's').Replace('Ş', 'S')
                .Replace('ı', 'i').Replace('İ', 'I')
                .Replace('ğ', 'g').Replace('Ğ', 'G')
                .Replace('ü', 'u').Replace('Ü', 'U')
                .Replace('ö', 'o').Replace('Ö', 'O')
                .Replace('ç', 'c').Replace('Ç', 'C');

            var sb = new System.Text.StringBuilder(normalized.Length);
            bool lastWasUnderscore = false;
            foreach (char c in normalized)
            {
                if (char.IsLetterOrDigit(c) || c == '-')
                {
                    sb.Append(c);
                    lastWasUnderscore = false;
                }
                else if (char.IsWhiteSpace(c) || c == '_')
                {
                    if (!lastWasUnderscore && sb.Length > 0)
                    {
                        sb.Append('_');
                        lastWasUnderscore = true;
                    }
                }
            }

            string result = sb.ToString().Trim('_', '-');
            if (string.IsNullOrWhiteSpace(result) ||
                result.Equals("Yeni_Oyun", StringComparison.OrdinalIgnoreCase) ||
                result.Equals("NEW_GAME", StringComparison.OrdinalIgnoreCase))
            {
                return "Calradia_Sunucusu";
            }

            return result.Length > 48 ? result.Substring(0, 48).TrimEnd('_') : result;
        }

        public static void Initialize(IServiceProvider provider, IEngineDispatcher dispatcher, IEventBus? eventBus = null)
        {
            ServiceProvider = provider;
            Dispatcher = dispatcher;
            Events = eventBus ?? provider.GetService<IEventBus>() ?? new EventBus();
        }

        public static void Shutdown()
        {
            try
            {
                Events.Clear();
                Server?.Stop();
                Client?.Disconnect();
            }
            catch { }
            ServiceProvider = null;
        }
    }
}
