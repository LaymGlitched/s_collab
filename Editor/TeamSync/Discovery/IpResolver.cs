using System.Net.Http;
using System.Net.NetworkInformation;

namespace Editor.TeamSync;

/// <summary>
/// Helper to resolve local LAN IP addresses and public WAN IP addresses for frictionless hosting.
/// </summary>
public static class IpResolver
{
	private static string _cachedPublicIp;
	private static DateTime _lastPublicIpFetch = DateTime.MinValue;
	private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds( 3 ) };

	/// <summary>
	/// Returns the best local IPv4 address on the machine.
	/// </summary>
	public static string GetPrimaryLocalIp()
	{
		try
		{
			// Find active operational network interfaces (Ethernet / Wi-Fi)
			var interfaces = NetworkInterface.GetAllNetworkInterfaces()
				.Where( ni => ni.OperationalStatus == OperationalStatus.Up &&
				              ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
				              !ni.Description.Contains( "Virtual", StringComparison.OrdinalIgnoreCase ) &&
				              !ni.Description.Contains( "WSL", StringComparison.OrdinalIgnoreCase ) &&
				              !ni.Description.Contains( "Hyper-V", StringComparison.OrdinalIgnoreCase ) )
				.ToList();

			foreach ( var ni in interfaces )
			{
				var props = ni.GetIPProperties();
				foreach ( var addr in props.UnicastAddresses )
				{
					if ( addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback( addr.Address ) )
					{
						return addr.Address.ToString();
					}
				}
			}

			// Fallback: socket connect probe
			using var socket = new Socket( AddressFamily.InterNetwork, SocketType.Dgram, 0 );
			socket.Connect( "8.8.8.8", 65530 );
			if ( socket.LocalEndPoint is IPEndPoint endPoint )
			{
				return endPoint.Address.ToString();
			}
		}
		catch
		{
			// Safe fallback
		}

		return "127.0.0.1";
	}

	/// <summary>
	/// Asynchronously fetches the public IP address with caching and multiple fallbacks.
	/// </summary>
	public static async Task<string> GetPublicIpAsync()
	{
		if ( !string.IsNullOrEmpty( _cachedPublicIp ) && (DateTime.UtcNow - _lastPublicIpFetch).TotalMinutes < 15 )
		{
			return _cachedPublicIp;
		}

		string[] providers = {
			"https://api.ipify.org",
			"https://icanhazip.com",
			"https://ifconfig.me/ip"
		};

		foreach ( var url in providers )
		{
			try
			{
				var ip = (await _httpClient.GetStringAsync( url )).Trim();
				if ( IPAddress.TryParse( ip, out _ ) )
				{
					_cachedPublicIp = ip;
					_lastPublicIpFetch = DateTime.UtcNow;
					return ip;
				}
			}
			catch
			{
				// Try next provider
			}
		}

		return _cachedPublicIp ?? GetPrimaryLocalIp();
	}
}
