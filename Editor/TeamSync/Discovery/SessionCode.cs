namespace Editor.TeamSync;

/// <summary>
/// Handles generating and parsing user-friendly Room Codes, invite links,
/// and formatted chat invite messages for frictionless session sharing.
/// </summary>
public static class SessionCode
{
	private const string Prefix = "SYNC";
	private const string UrlPrefix = "teamsync://";

	/// <summary>
	/// Encodes an IP and port into a compact, human-readable Room Code.
	/// Example: 192.168.1.50:29015 -> SYNC-192-168-1-50-29015 or Base64 token.
	/// </summary>
	public static string Encode( string host, int port )
	{
		if ( string.IsNullOrWhiteSpace( host ) ) host = "127.0.0.1";
		if ( port <= 0 ) port = 29015;

		// Clean up host string
		host = host.Trim();

		// Check if it's an IPv4
		if ( IPAddress.TryParse( host, out var ip ) && ip.AddressFamily == AddressFamily.InterNetwork )
		{
			var bytes = ip.GetAddressBytes();
			string hex = $"{bytes[0]:X2}{bytes[1]:X2}{bytes[2]:X2}{bytes[3]:X2}";
			return $"{Prefix}-{hex}-{port}";
		}

		// Fallback to base64 encoding for hostnames/domains
		byte[] plainTextBytes = Encoding.UTF8.GetBytes( $"{host}:{port}" );
		string b64 = Convert.ToBase64String( plainTextBytes ).TrimEnd( '=' ).Replace( '+', '-' ).Replace( '/', '_' );
		return $"{Prefix}-{b64}";
	}

	/// <summary>
	/// Attempts to parse any input string (Room Code, IP:Port, teamsync:// URL, or full chat message).
	/// </summary>
	public static bool TryParse( string input, out string host, out int port, out string error )
	{
		host = null;
		port = 29015;
		error = null;

		if ( string.IsNullOrWhiteSpace( input ) )
		{
			error = "Input is empty.";
			return false;
		}

		input = input.Trim();

		// 1. If it's a URL like teamsync://192.168.1.50:29015 or ws://...
		if ( input.StartsWith( UrlPrefix, StringComparison.OrdinalIgnoreCase ) )
		{
			input = input.Substring( UrlPrefix.Length ).Trim( '/' );
		}
		else if ( input.StartsWith( "ws://", StringComparison.OrdinalIgnoreCase ) )
		{
			input = input.Substring( 5 ).Trim( '/' );
		}
		else if ( input.StartsWith( "http://", StringComparison.OrdinalIgnoreCase ) )
		{
			input = input.Substring( 7 ).Trim( '/' );
		}

		// 2. If it contains a SYNC- code anywhere in the text (e.g. from a chat message)
		int codeIdx = input.IndexOf( "SYNC-", StringComparison.OrdinalIgnoreCase );
		if ( codeIdx >= 0 )
		{
			int endIdx = input.IndexOfAny( new[] { ' ', '\r', '\n', '\t', ')' }, codeIdx );
			string code = endIdx > codeIdx ? input.Substring( codeIdx, endIdx - codeIdx ) : input.Substring( codeIdx );
			return TryParseCode( code, out host, out port, out error );
		}

		// 3. Try standard IP:Port or Host:Port parsing
		return TryParseHostPort( input, out host, out port, out error );
	}

	private static bool TryParseCode( string code, out string host, out int port, out string error )
	{
		host = null;
		port = 29015;
		error = null;

		var parts = code.Split( '-' );
		if ( parts.Length < 2 )
		{
			error = "Invalid Room Code format.";
			return false;
		}

		// Format: SYNC-AABBCCDD-PORT (Hex IPv4)
		if ( parts.Length == 3 && parts[1].Length == 8 && int.TryParse( parts[2], out int p ) )
		{
			try
			{
				byte b0 = Convert.ToByte( parts[1].Substring( 0, 2 ), 16 );
				byte b1 = Convert.ToByte( parts[1].Substring( 2, 2 ), 16 );
				byte b2 = Convert.ToByte( parts[1].Substring( 4, 2 ), 16 );
				byte b3 = Convert.ToByte( parts[1].Substring( 6, 2 ), 16 );

				host = $"{b0}.{b1}.{b2}.{b3}";
				port = p;
				return true;
			}
			catch ( Exception ex )
			{
				error = $"Failed to decode hex IP: {ex.Message}";
				return false;
			}
		}

		// Format: SYNC-<Base64>
		if ( parts.Length == 2 )
		{
			try
			{
				string incoming = parts[1].Replace( '-', '+' ).Replace( '_', '/' );
				switch ( incoming.Length % 4 )
				{
					case 2: incoming += "=="; break;
					case 3: incoming += "="; break;
				}

				byte[] bytes = Convert.FromBase64String( incoming );
				string decoded = Encoding.UTF8.GetString( bytes );
				return TryParseHostPort( decoded, out host, out port, out error );
			}
			catch ( Exception ex )
			{
				error = $"Failed to decode base64 room code: {ex.Message}";
				return false;
			}
		}

		error = "Unrecognized Room Code format.";
		return false;
	}

	private static bool TryParseHostPort( string input, out string host, out int port, out string error )
	{
		host = null;
		port = 29015;
		error = null;

		input = input.Trim().Trim( '"', '\'', '<', '>', '[', ']' );

		if ( string.IsNullOrWhiteSpace( input ) )
		{
			error = "Address string is empty.";
			return false;
		}

		int colonIdx = input.LastIndexOf( ':' );
		if ( colonIdx > 0 && int.TryParse( input.Substring( colonIdx + 1 ), out int parsedPort ) )
		{
			host = input.Substring( 0, colonIdx ).Trim();
			port = parsedPort;
			return true;
		}

		// If no colon, treat entire input as host and use default port
		host = input;
		port = 29015;
		return true;
	}

	/// <summary>
	/// Generates a friendly, formatted invite message suitable for pasting into Steam / Discord / Slack.
	/// </summary>
	public static string CreateInviteMessage( string hostName, string projectName, string roomCode, string directEndpoint )
	{
		return $"🎮 Join {hostName}'s s&box Team Sync session for '{projectName}'!\n" +
		       $"Room Code: {roomCode}\n" +
		       $"Direct: {directEndpoint}\n" +
		       $"In s&box Editor: Open Team Sync dock and click 'Join from Clipboard'.";
	}
}
