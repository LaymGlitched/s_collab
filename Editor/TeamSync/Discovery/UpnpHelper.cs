using System.Net.Http;
using System.Xml.Linq;

namespace Editor.TeamSync;

/// <summary>
/// Lightweight UPnP IGD (Internet Gateway Device) helper for automatic port forwarding.
/// Enables seamless hosting over WAN without manual router configuration.
/// </summary>
public static class UpnpHelper
{
	private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds( 3 ) };

	public static async Task<bool> TryForwardPortAsync( int port, string description = "sbox_TeamSync" )
	{
		try
		{
			string controlUrl = await DiscoverUpnpControlUrlAsync();
			if ( string.IsNullOrEmpty( controlUrl ) )
			{
				Log.Info( "[TeamSync] UPnP: No UPnP gateway found on local network. (Manual port forward or Tailscale may be required for WAN)" );
				return false;
			}

			string localIp = IpResolver.GetPrimaryLocalIp();
			string soapBody =
				$"<?xml version=\"1.0\"?>\r\n" +
				$"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
				$"<s:Body>\r\n" +
				$"<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">\r\n" +
				$"  <NewRemoteHost></NewRemoteHost>\r\n" +
				$"  <NewExternalPort>{port}</NewExternalPort>\r\n" +
				$"  <NewProtocol>TCP</NewProtocol>\r\n" +
				$"  <NewInternalPort>{port}</NewInternalPort>\r\n" +
				$"  <NewInternalClient>{localIp}</NewInternalClient>\r\n" +
				$"  <NewEnabled>1</NewEnabled>\r\n" +
				$"  <NewPortMappingDescription>{description}</NewPortMappingDescription>\r\n" +
				$"  <NewLeaseDuration>0</NewLeaseDuration>\r\n" +
				$"</u:AddPortMapping>\r\n" +
				$"</s:Body>\r\n" +
				$"</s:Envelope>";

			var content = new StringContent( soapBody, Encoding.UTF8, "text/xml" );
			content.Headers.Add( "SOAPAction", "\"urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping\"" );

			var response = await _httpClient.PostAsync( controlUrl, content );
			if ( response.IsSuccessStatusCode )
			{
				Log.Info( $"[TeamSync] 🚀 UPnP: Port {port} forwarded successfully on router to {localIp}!" );
				return true;
			}
		}
		catch
		{
			// Safe fallback when router ignores or blocks UPnP
		}

		return false;
	}

	private static async Task<string> DiscoverUpnpControlUrlAsync()
	{
		using var udp = new UdpClient();
		udp.Client.ReceiveTimeout = 2000;

		string request =
			"M-SEARCH * HTTP/1.1\r\n" +
			"HOST: 239.255.255.250:1900\r\n" +
			"ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
			"MAN: \"ssdp:discover\"\r\n" +
			"MX: 2\r\n\r\n";

		byte[] reqBytes = Encoding.ASCII.GetBytes( request );
		var endpoint = new IPEndPoint( IPAddress.Parse( "239.255.255.250" ), 1900 );

		await udp.SendAsync( reqBytes, reqBytes.Length, endpoint );

		var cts = new CancellationTokenSource( 2000 );
		try
		{
			while ( !cts.IsCancellationRequested )
			{
				var result = await udp.ReceiveAsync( cts.Token );
				string response = Encoding.ASCII.GetString( result.Buffer );

				string location = null;
				foreach ( var line in response.Split( "\r\n" ) )
				{
					if ( line.StartsWith( "LOCATION:", StringComparison.OrdinalIgnoreCase ) )
					{
						location = line.Substring( "LOCATION:".Length ).Trim();
						break;
					}
				}

				if ( !string.IsNullOrEmpty( location ) )
				{
					// Fetch router device description XML to find WANIPConnection controlURL
					string descXml = await _httpClient.GetStringAsync( location );
					string controlSubUrl = ExtractControlUrl( descXml );
					if ( !string.IsNullOrEmpty( controlSubUrl ) )
					{
						var baseUri = new Uri( location );
						return new Uri( baseUri, controlSubUrl ).ToString();
					}
				}
			}
		}
		catch
		{
			// Timeout or no UPnP response
		}

		return null;
	}

	private static string ExtractControlUrl( string xml )
	{
		try
		{
			var doc = XDocument.Parse( xml );
			XNamespace ns = "urn:schemas-upnp-org:device-1-0";

			var service = doc.Descendants( ns + "service" )
				.FirstOrDefault( s => (string)s.Element( ns + "serviceType" ) == "urn:schemas-upnp-org:service:WANIPConnection:1" ||
				                      (string)s.Element( ns + "serviceType" ) == "urn:schemas-upnp-org:service:WANPPPConnection:1" );

			if ( service != null )
			{
				return (string)service.Element( ns + "controlURL" );
			}
		}
		catch { }

		return null;
	}
}
