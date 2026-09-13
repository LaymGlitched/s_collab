namespace Editor.TeamSync;

using NetWebSocket = System.Net.WebSockets.WebSocket;
using NetWebSocketState = System.Net.WebSockets.WebSocketState;
using NetWebSocketMessageType = System.Net.WebSockets.WebSocketMessageType;
using NetWebSocketCloseStatus = System.Net.WebSockets.WebSocketCloseStatus;

/// <summary>
/// Embedded WebSocket server hosted in the Editor process.
/// Handles incoming peer connections, routes envelopes, and broadcasts updates.
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
	public int Port { get; }
	public string StatusText { get; private set; } = "Stopped";

	private HttpListener _httpListener;
	private CancellationTokenSource _cts;
	private readonly ConcurrentDictionary<string, NetWebSocket> _clients = new();

	public TeamSyncServer( string localPeerId, int port = 29015 )
	{
		LocalPeerId = localPeerId;
		Port = port;
	}

	public async Task StartAsync()
	{
		if ( IsRunning ) return;

		_cts = new CancellationTokenSource();
		_httpListener = new HttpListener();

		bool bound = false;
		try
		{
			// Try binding to all interfaces first
			_httpListener.Prefixes.Add( $"http://*:{Port}/teamsync/" );
			_httpListener.Start();
			bound = true;
		}
		catch ( Exception )
		{
			// Fallback to localhost/127.0.0.1 if wildcard requires admin permissions
			_httpListener.Close();
			_httpListener = new HttpListener();
			_httpListener.Prefixes.Add( $"http://localhost:{Port}/teamsync/" );
			_httpListener.Prefixes.Add( $"http://127.0.0.1:{Port}/teamsync/" );
			try
			{
				_httpListener.Start();
				bound = true;
			}
			catch ( Exception ex )
			{
				StatusText = $"Failed to bind port {Port}: {ex.Message}";
				OnStatusChanged?.Invoke( StatusText );
				OnError?.Invoke( StatusText );
				return;
			}
		}

		if ( !bound ) return;

		IsRunning = true;
		StatusText = $"Hosting on port {Port}";
		OnStatusChanged?.Invoke( StatusText );

		_ = Task.Run( () => ListenLoopAsync( _cts.Token ) );
	}

	public async Task StopAsync()
	{
		if ( !IsRunning ) return;
		IsRunning = false;
		StatusText = "Stopped";
		OnStatusChanged?.Invoke( StatusText );

		_cts?.Cancel();

		try
		{
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
			}
			_clients.Clear();
		}
		catch { }

		try
		{
			_httpListener?.Stop();
			_httpListener?.Close();
		}
		catch { }
	}

	private async Task ListenLoopAsync( CancellationToken ct )
	{
		while ( !ct.IsCancellationRequested && _httpListener != null && _httpListener.IsListening )
		{
			try
			{
				var context = await _httpListener.GetContextAsync();
				if ( context.Request.IsWebSocketRequest )
				{
					_ = Task.Run( () => HandleClientWebSocketAsync( context, ct ) );
				}
				else
				{
					context.Response.StatusCode = 400;
					context.Response.Close();
				}
			}
			catch ( HttpListenerException ) when ( ct.IsCancellationRequested )
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

	private async Task HandleClientWebSocketAsync( HttpListenerContext context, CancellationToken ct )
	{
		NetWebSocket ws = null;
		string clientPeerId = null;

		try
		{
			var wsContext = await context.AcceptWebSocketAsync( subProtocol: null );
			ws = wsContext.WebSocket;

			// Generate a temporary peer ID until Hello message
			clientPeerId = Guid.NewGuid().ToString( "N" ).Substring( 0, 8 );
			_clients[clientPeerId] = ws;

			var buffer = new byte[64 * 1024];
			var ms = new MemoryStream();

			while ( ws.State == NetWebSocketState.Open && !ct.IsCancellationRequested )
			{
				ms.SetLength( 0 );
				System.Net.WebSockets.WebSocketReceiveResult result;
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
						// If this is a Hello, update the peer mapping
						if ( envelope.Type == TeamSyncMessageType.Hello && !string.IsNullOrEmpty( envelope.SenderId ) )
						{
							string oldId = clientPeerId;
							clientPeerId = envelope.SenderId;
							_clients.TryRemove( oldId, out _ );
							_clients[clientPeerId] = ws;
							OnPeerConnected?.Invoke( clientPeerId );
						}

						// Dispatch locally
						OnMessageReceived?.Invoke( envelope );

						// Re-broadcast to all other clients
						await BroadcastAsync( envelope, exceptPeerId: clientPeerId );
					}
				}
			}
		}
		catch ( Exception ex )
		{
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
		}
	}

	public async Task SendAsync( TeamSyncEnvelope envelope )
	{
		if ( !IsRunning || envelope == null ) return;

		// Server sending to a specific client or processing locally
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
			// Broadcast
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
