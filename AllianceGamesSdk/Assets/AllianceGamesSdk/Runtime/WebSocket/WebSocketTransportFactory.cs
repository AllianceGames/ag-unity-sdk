using AllianceGamesSdk.Common.Transport;
using Serilog;

namespace AllianceGamesSdk.Transport.Unity
{
    internal class WebSocketTransportFactory
    {
        public static ITransport Get(ILogger logger)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new WebSocketTransportNativeJS(logger);
#else
            return new WebSocketTransport(logger);
#endif
        }
    }
}
