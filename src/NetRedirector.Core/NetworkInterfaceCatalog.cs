using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetRedirector.Core;

public sealed record NetworkInterfaceInfo(string Name, string Address, bool IsLoopback)
{
    public override string ToString() => IsLoopback ? $"{Name} - {Address} (loopback)" : $"{Name} - {Address}";
}

public static class NetworkInterfaceCatalog
{
    public static IReadOnlyList<NetworkInterfaceInfo> GetIpv4Interfaces()
    {
        var result = new List<NetworkInterfaceInfo>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            var isLoopback = networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback;
            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                result.Add(new NetworkInterfaceInfo(
                    networkInterface.Name,
                    address.Address.ToString(),
                    isLoopback || IPAddress.IsLoopback(address.Address)));
            }
        }

        return result
            .GroupBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.IsLoopback)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<IPAddress> ResolveSelected(IEnumerable<string>? selectedAddresses)
    {
        var all = GetIpv4Interfaces();
        var selected = (selectedAddresses ?? [])
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (selected.Count == 0)
        {
            var nonLoopback = all
                .Where(item => !item.IsLoopback)
                .Select(item => IPAddress.Parse(item.Address))
                .ToArray();

            return nonLoopback.Length > 0
                ? nonLoopback
                : all.Select(item => IPAddress.Parse(item.Address)).ToArray();
        }

        return all
            .Where(item => selected.Contains(item.Address))
            .Select(item => IPAddress.Parse(item.Address))
            .ToArray();
    }
}
