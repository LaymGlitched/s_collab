namespace Editor.TeamSync;

/// <summary>
/// Central coordinator for real-time collaboration session, networking, presence, scene sync, and selection locks.
/// </summary>
public sealed class TeamSyncManager
{
	private static TeamSyncManager _instance;
	public static TeamSyncManager Instance => _instance ??= new TeamSyncManager();

	public bool IsSessionActive => Transport != null && Transport.IsRunning;
	public bool IsHost => Transport != null && Transport.IsHost;
	public string LocalPeerId { get; private set; }
	public string LocalPersonaName { get; private set; } = "Local User";
	public ulong LocalSteamId { get; private set; }
	public Color LocalColor { get; private set; }
	public string StatusText => Transport?.StatusText ?? (IsSessionActive ? "Connected" : "Disconnected");

	public ITeamSyncTransport Transport { get; private set; }
	public ConcurrentDictionary<string, CollaboratorState> Collaborators { get; } = new();
	public SelectionLockSystem LockSystem { get; }
	public SceneSyncSystem SyncSystem { get; }

	public event Action OnSessionStateChanged;
	public event Action<CollaboratorState> OnCollaboratorJoined;
	public event Action<string> OnCollaboratorLeft;

	private readonly ConcurrentQueue<TeamSyncEnvelope> _inboundQueue = new();
	private Vector3 _lastBroadcastCamPos;
	private Angles _lastBroadcastCamAngles;
	private float _lastCamBroadcastTime;
	private HashSet<string> _lastLocalSelection = new();

	private TeamSyncManager()
	{
		LocalPeerId = Guid.NewGuid().ToString( "N" ).Substring( 0, 8 );
		LocalPersonaName = Environment.UserName ?? "Collaborator";
		LocalSteamId = (ulong)Math.Abs( (long)LocalPersonaName.GetHashCode() );
		LocalColor = CollaboratorState.GenerateDeterministicColor( LocalSteamId != 0 ? LocalSteamId : (ulong)LocalPeerId.GetHashCode() );

		LockSystem = new SelectionLockSystem( this );
		SyncSystem = new SceneSyncSystem( this );

		// Start listening for local network hosts
		LanDiscoveryService.Instance.StartListening();
	}

	[EditorEvent.Hotload]
	private static void OnHotload()
	{
		// Preserve singleton instance across hotloads
		if ( _instance != null )
		{
			_instance.SyncSystem?.RebuildBaseline();
		}
	}

	public async Task HostSessionAsync( int port = 29020 )
	{
		await LeaveSessionAsync();

		var server = new TeamSyncServer( LocalPeerId, port );
		AttachTransport( server );

		await server.StartAsync();

		int actualPort = server.Port;

		// Register self as host collaborator
		var self = new CollaboratorState( LocalPeerId, LocalPersonaName, LocalSteamId, LocalColor.Hex )
		{
			IsHost = true
		};
		Collaborators[LocalPeerId] = self;

		SyncSystem.Reset();

		// Broadcast session presence on LAN using actual bound port
		LanDiscoveryService.Instance.StopListening();
		LanDiscoveryService.Instance.StartBroadcasting( actualPort, Project.Current?.Config?.Title ?? "s_collab", LocalPersonaName, LocalSteamId, () => Collaborators.Count );

		// Attempt automatic UPnP router port forwarding in background
		_ = UpnpHelper.TryForwardPortAsync( actualPort );

		OnSessionStateChanged?.Invoke();
	}

	public async Task<bool> JoinSessionAsync( string hostAddress, int port = 29015 )
	{
		await LeaveSessionAsync();

		if ( string.IsNullOrWhiteSpace( hostAddress ) )
		{
			hostAddress = "127.0.0.1";
		}

		Log.Info( $"[TeamSync] Connecting to {hostAddress}:{port}..." );
		var client = new TeamSyncClient( LocalPeerId, hostAddress, port );
		AttachTransport( client );

		await client.StartAsync();

		if ( !client.IsRunning )
		{
			Log.Error( $"[TeamSync] ❌ Connection failed to {hostAddress}:{port}: {client.StatusText}" );
			await LeaveSessionAsync();
			return false;
		}

		// Register self ONLY after successful socket connection!
		var self = new CollaboratorState( LocalPeerId, LocalPersonaName, LocalSteamId, LocalColor.Hex )
		{
			IsHost = false
		};
		Collaborators[LocalPeerId] = self;

		// Send Hello
		await client.SendAsync( TeamSyncEnvelope.Create( TeamSyncMessageType.Hello, LocalPeerId, new HelloPayload
		{
			PeerId = LocalPeerId,
			PersonaName = LocalPersonaName,
			SteamId = LocalSteamId,
			ColorHex = LocalColor.Hex
		} ) );

		SyncSystem.Reset();
		LanDiscoveryService.Instance.StopListening();
		OnSessionStateChanged?.Invoke();
		Log.Info( $"[TeamSync] ✅ Successfully connected to host at {hostAddress}:{port}!" );
		return true;
	}

	public async Task<bool> JoinByCodeAsync( string codeOrInput )
	{
		if ( !SessionCode.TryParse( codeOrInput, out var host, out var port, out var fallbackHost, out var err ) )
		{
			Log.Error( $"[TeamSync] Could not parse session code '{codeOrInput}': {err}" );
			return false;
		}

		bool success = await JoinSessionAsync( host, port );
		if ( !success && !string.IsNullOrEmpty( fallbackHost ) && fallbackHost != host )
		{
			Log.Info( $"[TeamSync] Local endpoint {host}:{port} unreachable. Retrying with internet WAN endpoint {fallbackHost}:{port}..." );
			success = await JoinSessionAsync( fallbackHost, port );
		}

		return success;
	}

	public string GetActiveRoomCode()
	{
		if ( !IsSessionActive ) return string.Empty;
		if ( IsHost && Transport is TeamSyncServer srv )
		{
			string lan = IpResolver.GetPrimaryLocalIp();
			string wan = IpResolver.GetCachedPublicIp();
			return SessionCode.EncodeDual( lan, wan, srv.Port );
		}
		if ( !IsHost && Transport is TeamSyncClient cli )
		{
			return SessionCode.Encode( cli.HostAddress, cli.Port );
		}
		return string.Empty;
	}

	public string GetPublicRoomCode()
	{
		if ( !IsSessionActive || !IsHost || Transport is not TeamSyncServer srv ) return string.Empty;
		string wan = IpResolver.GetCachedPublicIp() ?? IpResolver.GetPrimaryLocalIp();
		return SessionCode.Encode( wan, srv.Port );
	}

	public string GetLocalRoomCode()
	{
		if ( !IsSessionActive || !IsHost || Transport is not TeamSyncServer srv ) return string.Empty;
		return SessionCode.Encode( IpResolver.GetPrimaryLocalIp(), srv.Port );
	}

	[ConCmd( "teamsync_join" )]
	public static void JoinConsoleCmd( string target )
	{
		if ( string.IsNullOrWhiteSpace( target ) )
		{
			Log.Info( "Usage: teamsync_join <room_code_or_ip_port>" );
			return;
		}
		_ = Instance.JoinByCodeAsync( target );
	}

	public void AttachCustomTransport( ITeamSyncTransport customTransport )
	{
		AttachTransport( customTransport );

		var self = new CollaboratorState( LocalPeerId, LocalPersonaName, LocalSteamId, LocalColor.Hex )
		{
			IsHost = customTransport.IsHost
		};
		Collaborators[LocalPeerId] = self;

		SyncSystem.Reset();
		OnSessionStateChanged?.Invoke();
	}

	private void AttachTransport( ITeamSyncTransport transport )
	{
		Transport = transport;
		Transport.OnMessageReceived += msg =>
		{
			_inboundQueue.Enqueue( msg );
			// Immediately process connection and handshake envelopes
			if ( msg.Type == TeamSyncMessageType.Hello || msg.Type == TeamSyncMessageType.Welcome || msg.Type == TeamSyncMessageType.PeerJoined )
			{
				ProcessEnvelope( msg );
			}
		};
		Transport.OnPeerDisconnected += peerId =>
		{
			Collaborators.TryRemove( peerId, out _ );
			LockSystem.ReleaseLocksForPeer( peerId );
			OnCollaboratorLeft?.Invoke( peerId );
		};
		Transport.OnStatusChanged += _ => OnSessionStateChanged?.Invoke();
	}

	public async Task LeaveSessionAsync()
	{
		LanDiscoveryService.Instance.StopBroadcasting();

		if ( Transport != null )
		{
			try
			{
				if ( IsSessionActive )
				{
					await Transport.SendAsync( TeamSyncEnvelope.Create( TeamSyncMessageType.PeerLeft, LocalPeerId, LocalPeerId ) );
				}
				await Transport.StopAsync();
				Transport.Dispose();
			}
			catch { }
			Transport = null;
		}

		Collaborators.Clear();
		_lastLocalSelection.Clear();
		SyncSystem.Reset();

		// Resume listening for local sessions
		LanDiscoveryService.Instance.StartListening();

		OnSessionStateChanged?.Invoke();
	}

	public void BroadcastSceneDelta( SceneDeltaPayload delta )
	{
		if ( !IsSessionActive || delta == null ) return;
		_ = Transport.BroadcastAsync( TeamSyncEnvelope.Create( TeamSyncMessageType.SceneDelta, LocalPeerId, delta ) );
	}

	[EditorEvent.Frame]
	public static void GlobalFrameUpdate()
	{
		_instance?.FrameUpdate();
	}

	public void FrameUpdate()
	{
		// 1. Drain incoming messages on the main UI/Editor thread
		while ( _inboundQueue.TryDequeue( out var envelope ) )
		{
			ProcessEnvelope( envelope );
		}

		if ( !IsSessionActive || Game.IsPlaying ) return;

		// 2. Track & broadcast local editor camera transform (~15Hz)
		float now = RealTime.Now;
		if ( now - _lastCamBroadcastTime > 0.06f )
		{
			_lastCamBroadcastTime = now;
			BroadcastLocalCamera();
		}

		// 3. Track & broadcast local selection + lock requests
		TrackLocalSelectionChanges();

		// 4. Host lock pruning
		if ( IsHost )
		{
			LockSystem.PruneExpiredLocks( TimeSpan.FromSeconds( 30 ) );
		}

		// 5. Render lock gizmos
		LockSystem.DrawLockGizmos();

		// 6. Run sync system change detection
		SyncSystem.FrameUpdate();
	}

	private void BroadcastLocalCamera()
	{
		Vector3 pos = Vector3.Zero;
		Angles angles = Angles.Zero;
		float fov = 80f;

		var editorCam = Application.Editor?.Camera;
		if ( editorCam.IsValid() )
		{
			pos = editorCam.WorldPosition;
			angles = editorCam.WorldRotation.Angles();
			fov = editorCam.FieldOfView;
		}
		else
		{
			try
			{
				if ( Gizmo.Camera != null )
				{
					pos = Gizmo.Camera.Position;
					angles = Gizmo.Camera.Angles;
					fov = Gizmo.Camera.FieldOfView;
				}
				else
				{
					return;
				}
			}
			catch
			{
				return;
			}
		}

		if ( (pos - _lastBroadcastCamPos).LengthSquared > 0.1f || angles != _lastBroadcastCamAngles )
		{
			_lastBroadcastCamPos = pos;
			_lastBroadcastCamAngles = angles;

			var payload = new CameraPayload
			{
				PeerId = LocalPeerId,
				Position = pos,
				Angles = angles,
				Fov = fov
			};

			_ = Transport.BroadcastAsync( TeamSyncEnvelope.Create( TeamSyncMessageType.CameraUpdate, LocalPeerId, payload ) );
		}
	}

	private void TrackLocalSelectionChanges()
	{
		var session = SceneEditorSession.Active;
		if ( session == null ) return;

		var currentSelected = session.Selection.OfType<GameObject>().Select( x => x.Id.ToString() ).ToHashSet();

		if ( !currentSelected.SetEquals( _lastLocalSelection ) )
		{
			// Newly selected -> acquire lock
			foreach ( var id in currentSelected.Except( _lastLocalSelection ) )
			{
				LockSystem.RequestLock( id, acquire: true );
			}

			// Deselected -> release lock
			foreach ( var id in _lastLocalSelection.Except( currentSelected ) )
			{
				LockSystem.RequestLock( id, acquire: false );
			}

			_lastLocalSelection = currentSelected;

			var payload = new SelectionPayload
			{
				PeerId = LocalPeerId,
				SelectedObjectIds = currentSelected.ToList()
			};
			_ = Transport.BroadcastAsync( TeamSyncEnvelope.Create( TeamSyncMessageType.SelectionUpdate, LocalPeerId, payload ) );
		}
	}

	private void ProcessEnvelope( TeamSyncEnvelope env )
	{
		if ( env == null || env.SenderId == LocalPeerId ) return;

		switch ( env.Type )
		{
			case TeamSyncMessageType.Hello:
				HandleHello( env );
				break;

			case TeamSyncMessageType.Welcome:
				HandleWelcome( env );
				break;

			case TeamSyncMessageType.PeerJoined:
			case TeamSyncMessageType.CameraUpdate:
				HandleCameraUpdate( env );
				break;

			case TeamSyncMessageType.SelectionUpdate:
				HandleSelectionUpdate( env );
				break;

			case TeamSyncMessageType.LockRequest:
				if ( IsHost )
				{
					var req = env.GetPayload<LockRequestPayload>();
					LockSystem.HandleLockRequestOnHost( req );
				}
				break;

			case TeamSyncMessageType.LockUpdate:
				var lockUpdate = env.GetPayload<LockUpdatePayload>();
				if ( lockUpdate != null )
				{
					LockSystem.ApplyLockUpdate( lockUpdate.Locks );
				}
				break;

			case TeamSyncMessageType.SceneDelta:
				var delta = env.GetPayload<SceneDeltaPayload>();
				if ( delta != null )
				{
					SceneApplicator.ApplyDelta( delta );
				}
				break;

			case TeamSyncMessageType.SceneSnapshot:
				var snapshot = env.GetPayload<SceneSnapshotPayload>();
				if ( snapshot != null )
				{
					SceneApplicator.RestoreSceneSnapshot( snapshot.SceneJson );
				}
				break;

			case TeamSyncMessageType.PeerLeft:
				string leftId = env.PayloadJson?.Trim( '"' ) ?? env.SenderId;
				Collaborators.TryRemove( leftId, out _ );
				LockSystem.ReleaseLocksForPeer( leftId );
				OnCollaboratorLeft?.Invoke( leftId );
				break;
		}
	}

	private void HandleHello( TeamSyncEnvelope env )
	{
		var hello = env.GetPayload<HelloPayload>();
		if ( hello == null ) return;

		Log.Info( $"[TeamSync] 🤝 Remote collaborator joined: {hello.PersonaName} (PeerId: {hello.PeerId})!" );

		var peer = new CollaboratorState( hello.PeerId, hello.PersonaName, hello.SteamId, hello.ColorHex )
		{
			IsHost = false
		};
		Collaborators[hello.PeerId] = peer;
		OnCollaboratorJoined?.Invoke( peer );

		if ( IsHost )
		{
			// Send full Welcome snapshot to new peer
			var welcome = new WelcomePayload
			{
				AssignedPeerId = hello.PeerId,
				HostPeerId = LocalPeerId,
				Peers = Collaborators.Values.Select( p => p.ToPeerInfo() ).ToList(),
				ActiveLocks = new Dictionary<string, string>( LockSystem.ActiveLocks ),
				SceneJson = SceneApplicator.CreateSceneSnapshot()
			};

			_ = Transport.SendAsync( TeamSyncEnvelope.Create( TeamSyncMessageType.Welcome, LocalPeerId, welcome ) );
			Log.Info( $"[TeamSync] 📤 Sent Welcome snapshot to {hello.PersonaName}." );
		}

		OnSessionStateChanged?.Invoke();
	}

	private void HandleWelcome( TeamSyncEnvelope env )
	{
		var welcome = env.GetPayload<WelcomePayload>();
		if ( welcome == null ) return;

		Log.Info( $"[TeamSync] 📥 Processed Welcome snapshot from host with {welcome.Peers.Count} peers." );

		foreach ( var p in welcome.Peers )
		{
			if ( p.PeerId == LocalPeerId ) continue;
			var peer = new CollaboratorState( p.PeerId, p.PersonaName, p.SteamId, p.ColorHex )
			{
				IsHost = p.IsHost
			};
			peer.UpdateFromPeerInfo( p );
			Collaborators[p.PeerId] = peer;
		}

		if ( welcome.ActiveLocks != null )
		{
			LockSystem.ApplyLockUpdate( welcome.ActiveLocks );
		}

		if ( !string.IsNullOrEmpty( welcome.SceneJson ) )
		{
			SceneApplicator.RestoreSceneSnapshot( welcome.SceneJson );
			SyncSystem.Reset();
		}

		OnSessionStateChanged?.Invoke();
	}

	private void HandleCameraUpdate( TeamSyncEnvelope env )
	{
		var cam = env.GetPayload<CameraPayload>();
		if ( cam == null ) return;

		if ( !Collaborators.TryGetValue( cam.PeerId, out var peer ) )
		{
			peer = new CollaboratorState( cam.PeerId, "Collaborator", 0 );
			Collaborators[cam.PeerId] = peer;
			OnCollaboratorJoined?.Invoke( peer );
		}

		peer.CameraPosition = cam.Position;
		peer.CameraAngles = cam.Angles;
		peer.CameraFov = cam.Fov;
		peer.LastSeen = DateTime.UtcNow;
	}

	private void HandleSelectionUpdate( TeamSyncEnvelope env )
	{
		var sel = env.GetPayload<SelectionPayload>();
		if ( sel == null ) return;

		if ( Collaborators.TryGetValue( sel.PeerId, out var peer ) )
		{
			peer.SelectedObjectIds.Clear();
			if ( sel.SelectedObjectIds != null )
			{
				foreach ( var id in sel.SelectedObjectIds )
				{
					peer.SelectedObjectIds.Add( id );
				}
			}
			peer.LastSeen = DateTime.UtcNow;
		}
	}
}
