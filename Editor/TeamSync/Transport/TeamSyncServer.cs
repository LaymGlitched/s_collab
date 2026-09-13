namespace Editor.TeamSync;

using NetWebSocket = System.Net.WebSockets.WebSocket;
using NetWebSocketState = System.Net.WebSockets.WebSocketState;
using NetWebSocketMessageType = System.Net.WebSockets.WebSocketMessageType;
using NetWebSocketCloseStatus = System.Net.WebSockets.WebSocketCloseStatus;

/// <summary>
/// Embedded WebSocket server hosted in the Editor process using pure TcpListener.
/// Binds to IPAddress.Any (0.0.0.0) without requiring Administrator privileges
/// or Windows HttpListener URL ACL registration.
/// </summary>
public sealed class TeamSyncServer : ITeamSyncTransport
{
	public event Action<TeamSyncEnvelope> OnMessageReceived;
	public event Action<string> OnPeerConnected;
	public event Action<string> OnPeerDisconnected;
	public event Action<string> OnStatusChanged;
	public event Action<string> OnError;

	public bool IsRunning { get; private set; }
	public bool IsHost => true;
	public string LocalPeerId { get; private set; }
	public int Port { get; private set; }
	public string StatusText { get; private set; } = "Stopped";

	private TcpListener _tcpListener;
	private CancellationTokenSource _cts;
	private readonly ConcurrentDictionary<string, NetWebSocket> _clients = new();

	public TeamSyncServer( string localPeerId, int port = 29020 )
	{
		LocalPeerId = localPeerId;
		Port = port;
	}

	public async Task StartAsync()
	{
		if ( IsRunning ) return;

		_cts = new CancellationTokenSource();

		int requestedPort = Port > 0 ? Port : 29020;
		// 29015 is often reserved by Windows Hyper-V; prefer 29020+
		var candidates = new List<int> { requestedPort, 29020, 29021, 29022, 29025, 29030 };
		bool bound = false;

		foreach ( int candidate in candidates.Distinct() )
		{
			try
			{
				_tcpListener = new TcpListener( IPAddress.Any, candidate );
				_tcpListener.ExclusiveAddressUse = false;
				_tcpListener.Server.SetSocketOption( SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true );
				_tcpListener.Start();

				Port = candidate;
				bound = true;
				break;
			}
			catch ( SocketException ex ) when ( ex.SocketErrorCode == SocketError.AccessDenied || ex.SocketErrorCode == SocketError.AddressAlreadyInUse )
			{
				try { _tcpListener?.Stop(); } catch { }
				_tcpListener = null;
				Log.Warning( $"[TeamSync] Port {candidate} is reserved by Windows or in use. Trying next available port..." );
			}
			catch ( Exception )
			{
				try { _tcpListener?.Stop(); } catch { }
				_tcpListener = null;
			}
		}

		if ( !bound )
		{
			// Final fallback: let OS assign any free ephemeral port
			try
			{
				_tcpListener = new TcpListener( IPAddress.Any, 0 );
				_tcpListener.Start();
				Port = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
				bound = true;
			}
			catch ( Exception ex )
			{
				StatusText = $"Failed to bind port: {ex.Message}";
				OnStatusChanged?.Invoke( StatusText );
				OnError?.Invoke( StatusText );
				Log.Error( $"[TeamSync] Server bind error: {ex.Message}" );
				return;
			}
		}

		IsRunning = true;
		StatusText = $"Hosting on port {Port} (All Interfaces)";
		OnStatusChanged?.Invoke( StatusText );
		Log.Info( $"[TeamSync] Server successfully listening on 0.0.0.0:{Port}." );

		_ = Task.Run( () => ListenLoopAsync( _cts.Token ) );
	}

	public async Task StopAsync()
	{
		if ( !IsRunning && _tcpListener == null ) return;
		IsRunning = false;
		StatusText = "Stopped";
		OnStatusChanged?.Invoke( StatusText );

		_cts?.Cancel();

		foreach ( var kvp in _clients )
		{
			try
			{
				if ( kvp.Value.State == NetWebSocketState.Open )
				{
					await kvp.Value.CloseAsync( NetWebSocketCloseStatus.NormalClosure, "Host closed session", CancellationToken.None );
				}
			}
			catch { }
			try
			{
				kvp.Value.Dispose();
			}
			catch { }
		}
		_clients.Clear();

		try
		{
			_tcpListener?.Stop();
		}
		catch { }
		_tcpListener = null;
	}

	private async Task ListenLoopAsync( CancellationToken ct )
	{
		while ( !ct.IsCancellationRequested && _tcpListener != null )
		{
			try
			{
				var tcpClient = await _tcpListener.AcceptTcpClientAsync( ct );
				Log.Info( $"[TeamSync] 🔔 Inbound connection received from {tcpClient.Client.RemoteEndPoint}!" );
				_ = Task.Run( () => HandleClientConnectionAsync( tcpClient, ct ) );
			}
			catch ( OperationCanceledException )
			{
				break;
			}
			catch ( Exception ex )
			{
				if ( !ct.IsCancellationRequested )
				{
					OnError?.Invoke( $"Server accept error: {ex.Message}" );
				}
			}
		}
	}

	private async Task HandleClientConnectionAsync( TcpClient tcpClient, CancellationToken ct )
	{
		string clientPeerId = null;
		NetWebSocket ws = null;

		try
		{
			tcpClient.NoDelay = true;
			var stream = tcpClient.GetStream();

			// 1. Perform RFC 6455 HTTP WebSocket Upgrade Handshake
			string secKey = await ReadWebSocketHandshakeKeyAsync( stream, ct );
			if ( string.IsNullOrEmpty( secKey ) )
			{
				tcpClient.Close();
				return;
			}

			string acceptKey = Convert.ToBase64String(
				SHA1.HashData( Encoding.UTF8.GetBytes( secKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11" ) )
			);

			string responseHeader = "HTTP/1.1 101 Switching Protocols\r\n" +
			                        "Upgrade: websocket\r\n" +
			                        "Connection: Upgrade\r\n" +
			                        $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";

			byte[] responseBytes = Encoding.UTF8.GetBytes( responseHeader );
			await stream.WriteAsync( responseBytes, 0, responseBytes.Length, ct );
			await stream.FlushAsync( ct );

			// 2. Wrap stream in managed WebSocket instance
			ws = NetWebSocket.CreateFromStream( stream, isServer: true, subProtocol: null, TimeSpan.FromSeconds( 30 ) );

			clientPeerId = Guid.NewGuid().ToString( "N" ).Substring( 0, 8 );
			_clients[clientPeerId] = ws;

			Log.Info( $"[TeamSync] Remote peer connected from {tcpClient.Client.RemoteEndPoint} (temp id: {clientPeerId})" );

			// 3. Receive Loop
			var buffer = new byte[64 * 1024];
			var ms = new MemoryStream();

			while ( ws.State == NetWebSocketState.Open && !ct.IsCancellationRequested )
			{
				ms.SetLength( 0 );
				WebSocketReceiveResult result;
				do
				{
					result = await ws.ReceiveAsync( new ArraySegment<byte>( buffer ), ct );
					if ( result.MessageType == NetWebSocketMessageType.Close )
					{
						break;
					}
					ms.Write( buffer, 0, result.Count );
				}
				while ( !result.EndOfMessage );

				if ( result.MessageType == NetWebSocketMessageType.Close )
				{
					await ws.CloseAsync( NetWebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None );
					break;
				}

				if ( ms.Length > 0 )
				{
					string json = Encoding.UTF8.GetString( ms.ToArray() );
					var envelope = TeamSyncEnvelope.Deserialize( json );
					if ( envelope != null )
					{
						Log.Info( $"[TeamSync] 📥 Server received envelope '{envelope.Type}' from peer '{envelope.SenderId}'" );

						// If this is a Hello, associate the official peer ID
						if ( envelope.Type == TeamSyncMessageType.Hello && !string.IsNullOrEmpty( envelope.SenderId ) )
						{
							string oldId = clientPeerId;
							clientPeerId = envelope.SenderId;
							_clients.TryRemove( oldId, out _ );
							_clients[clientPeerId] = ws;
							OnPeerConnected?.Invoke( clientPeerId );
						}

						// Dispatch locally on host
						OnMessageReceived?.Invoke( envelope );

						// Re-broadcast to all other connected peers
						await BroadcastAsync( envelope, exceptPeerId: clientPeerId );
					}
					else
					{
						Log.Warning( $"[TeamSync] Server received unparseable message: {json}" );
					}
				}
			}
		}
		catch ( Exception ex )
		{
			Log.Error( $"[TeamSync] ❌ Server client handler exception for '{clientPeerId}': {ex}" );
			if ( !ct.IsCancellationRequested && ws?.State != NetWebSocketState.Closed )
			{
				OnError?.Invoke( $"Client error ({clientPeerId}): {ex.Message}" );
			}
		}
		finally
		{
			if ( !string.IsNullOrEmpty( clientPeerId ) )
			{
				_clients.TryRemove( clientPeerId, out _ );
				OnPeerDisconnected?.Invoke( clientPeerId );
			}

			try
			{
				ws?.Dispose();
			}
			catch { }
			try
			{
				tcpClient.Close();
				tcpClient.Dispose();
			}
			catch { }
		}
	}

	private static async Task<string> ReadWebSocketHandshakeKeyAsync( NetworkStream stream, CancellationToken ct )
	{
		var buffer = new byte[4096];
		int totalRead = 0;

		while ( totalRead < buffer.Length )
		{
			int read = await stream.ReadAsync( buffer, totalRead, buffer.Length - totalRead, ct );
			if ( read <= 0 ) return null;
			totalRead += read;

			string text = Encoding.UTF8.GetString( buffer, 0, totalRead );
			if ( text.Contains( "\r\n\r\n" ) )
			{
				// Extract Sec-WebSocket-Key
				foreach ( var line in text.Split( "\r\n" ) )
				{
					if ( line.StartsWith( "Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase ) )
					{
						return line.Substring( "Sec-WebSocket-Key:".Length ).Trim();
					}
				}
				break;
			}
		}

		return null;
	}

	public async Task SendAsync( TeamSyncEnvelope envelope )
	{
		if ( !IsRunning || envelope == null ) return;

		if ( !string.IsNullOrEmpty( envelope.SenderId ) && _clients.TryGetValue( envelope.SenderId, out var ws ) )
		{
			if ( ws.State == NetWebSocketState.Open )
			{
				byte[] bytes = Encoding.UTF8.GetBytes( envelope.Serialize() );
				await ws.SendAsync( new ArraySegment<byte>( bytes ), NetWebSocketMessageType.Text, true, CancellationToken.None );
			}
		}
		else
		{
			await BroadcastAsync( envelope );
		}
	}

	public async Task BroadcastAsync( TeamSyncEnvelope envelope, string exceptPeerId = null )
	{
		if ( !IsRunning || envelope == null ) return;

		byte[] bytes = Encoding.UTF8.GetBytes( envelope.Serialize() );
		var segment = new ArraySegment<byte>( bytes );

		foreach ( var kvp in _clients )
		{
			if ( kvp.Key == exceptPeerId ) continue;

			if ( kvp.Value.State == NetWebSocketState.Open )
			{
				try
				{
					await kvp.Value.SendAsync( segment, NetWebSocketMessageType.Text, true, CancellationToken.None );
				}
				catch { }
			}
		}
	}

	public void Dispose()
	{
		_ = StopAsync();
	}
}
