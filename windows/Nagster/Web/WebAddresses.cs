using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Nagster.Web;

/// <summary>Lists the addresses other devices on the network can use to reach this PC's web interface.</summary>
public static class WebAddresses
{
    public static IReadOnlyList<string> For(int port)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => $"http://{a.Address}:{port}")
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException) { return []; }
    }
}
