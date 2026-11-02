using System.Net;
using System.Net.Sockets;

namespace TokenView.Wpf.Services;

/// <summary>只允许私网/回环地址访问快照服务，避免把监听暴露到公网接口。</summary>
public static class LanAddress
{
    public static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork && bytes.Length == 4)
        {
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && bytes.Length == 16)
        {
            return address.IsIPv6LinkLocal ||
                   address.IsIPv6SiteLocal ||
                   (bytes[0] & 0xFE) == 0xFC;
        }
        return false;
    }
}
