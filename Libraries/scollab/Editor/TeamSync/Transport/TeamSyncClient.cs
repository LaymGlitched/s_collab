namespace Editor.TeamSync;

using NetWebSocket = System.Net.WebSockets.WebSocket;
using NetClientWebSocket = System.Net.WebSockets.ClientWebSocket;
using NetWebSocketState = System.Net.WebSockets.WebSocketState;
using NetWebSocketMessageType = System.Net.WebSockets.WebSocketMessageType;
using NetWebSocketCloseStatus = System.Net.WebSockets.WebSocketCloseStatus;

/// <summary>
/// WebSocket client connecting to a remote Team Sync host.
/// </summary>
public sealed class TeamSyncClient : ITeamSyncTransport
{
	public event Action<TeamSyncEnvelope> OnMessageReceived;
	public event Action<string> OnPeerConnected;
	public event Action<string> OnPeerDisconnected;
	public event Action<string> OnStatusChanged;
	public event Action<string> OnError;

	public bool IsRunning { get; private set; }
	public bool IsHost => false;
	public string LocalPeerId { get; private set; }
	public string HostAddress { get; }
	public int Port { get; }
	public string StatusText { get; private set; } = "Disconnected";

	private NetClientWebSocket _ws;
	private CancellationTokenSource _cts;
	private readonly SemaphoreSlim _sendLock = new( 1, 1 );

	public TeamSyncClient( string localPeerId, string hostAddress, int port = 29015 )
	{
		LocalPeerId = localPeerId;
		HostAddress = hostAddress;
		Port = port;
	}

	public async Task StartAsync()
	{
		if ( IsRunning ) return;

		_cts = new CancellationTokenSource();
		_ws = new NetClientWebSocket();

		string uriString = $"ws://{HostAddress}:{Port}/teamsync/";
		StatusText = $"Connecting to {HostAddress}:{Port}...";
		OnStatusChanged?.Invoke( StatusText );
		Log.Info( $"[TeamSync] 🚀 Client dialing: {uriString}" );

		try
		{
			var uri = new Uri( uriString );
			using var timeoutCts = new CancellationTokenSource( TimeSpan.FromSeconds( 8 ) );
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource( _cts.Token, timeoutCts.Token );

			await _ws.ConnectAsync( uri, linkedCts.Token );

			IsRunning = true;
			StatusText = $"Connected to {HostAddress}:{Port}";
			OnStatusChanged?.Invoke( StatusText );
			OnPeerConnected?.Invoke( "Host" );
			Log.Info( $"[TeamSync] 🎉 Client successfully connected to {HostAddress}:{Port}!" );

			_ = Task.Run( () => ReceiveLoopAsync( _cts.Token ) );
		}
		catch ( Exception ex )
		{
			Log.Error( $"[TeamSync] ❌ Client connection failed to {uriString}: {ex.Message}" );
			StatusText = $"Failed to connect: {ex.Message}";
			OnStatusChanged?.Invoke( StatusText );
			OnError?.Invoke( StatusText );
			await StopAsync();
		}
	}

	public async Task StopAsync()
	{
		if ( !IsRunning && _ws == null ) return;
		IsRunning = false;
		StatusText = "Disconnected";
		OnStatusChanged?.Invoke( StatusText );

		_cts?.Cancel();

		try
		{
			if ( _ws != null && _ws.State == NetWebSocketState.Open )
			{
				await _ws.CloseAsync( NetWebSocketCloseStatus.NormalClosure, "Client disconnected", CancellationToken.None );
			}
		}
		catch { }

		try
		{
			_ws?.Dispose();
			_ws = null;
		}
		catch { }
	}

	private async Task ReceiveLoopAsync( CancellationToken ct )
	{
		var buffer = new byte[64 * 1024];
		var ms = new MemoryStream();

		try
		{
			while ( _ws != null && _ws.State == NetWebSocketState.Open && !ct.IsCancellationRequested )
			{
				ms.SetLength( 0 );
				System.Net.WebSockets.WebSocketReceiveResult result;
				do
				{
					result = await _ws.ReceiveAsync( new ArraySegment<byte>( buffer ), ct );
					if ( result.MessageType == NetWebSocketMessageType.Close )
					{
						break;
					}
					ms.Write( buffer, 0, result.Count );
				}
				while ( !result.EndOfMessage );

				if ( result.MessageType == NetWebSocketMessageType.Close )
				{
					break;
				}

				if ( ms.Length > 0 )
				{
					string json = Encoding.UTF8.GetString( ms.ToArray() );
					var envelope = TeamSyncEnvelope.Deserialize( json );
					if ( envelope != null )
					{
						OnMessageReceived?.Invoke( envelope );
					}
				}
			}
		}
		catch ( Exception ex )
		{
			if ( !ct.IsCancellationRequested )
			{
				OnError?.Invoke( $"Client receive error: {ex.Message}" );
			}
		}
		finally
		{
			OnPeerDisconnected?.Invoke( "Host" );
			_ = StopAsync();
		}
	}

	public async Task SendAsync( TeamSyncEnvelope envelope )
	{
		if ( !IsRunning || _ws == null || _ws.State != NetWebSocketState.Open || envelope == null ) return;

		await _sendLock.WaitAsync();
		try
		{
			byte[] bytes = Encoding.UTF8.GetBytes( envelope.Serialize() );
			await _ws.SendAsync( new ArraySegment<byte>( bytes ), NetWebSocketMessageType.Text, true, CancellationToken.None );
		}
		catch ( Exception ex )
		{
			OnError?.Invoke( $"Client send error: {ex.Message}" );
		}
		finally
		{
			_sendLock.Release();
		}
	}

	public Task BroadcastAsync( TeamSyncEnvelope envelope, string exceptPeerId = null )
	{
		// In client mode, sending is sending to the host (who broadcasts to other peers)
		return SendAsync( envelope );
	}

	public void Dispose()
	{
		_ = StopAsync();
	}
}
