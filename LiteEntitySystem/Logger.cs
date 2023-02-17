namespace LiteEntitySystem
{
    /// <summary>
    /// Logger implementation for different situations (client/server/different engine)
    /// </summary>
    public interface ILogger
    {
        void Log(string log);
        void LogError(string log);
        void LogWarning(string log);
    }
    
    public static class Logger
    {
        public static ILogger LoggerImpl = null;
        
        internal static void Log(string log)
        {
            LoggerImpl?.Log(log);
        }
        
        internal static void LogError(string log)
        {
            LoggerImpl?.LogError(log);
        }
        
        internal static void LogWarning(string log)
        {
            LoggerImpl?.LogWarning(log);
        }
    }
}