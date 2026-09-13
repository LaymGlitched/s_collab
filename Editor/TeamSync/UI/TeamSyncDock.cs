namespace Editor.TeamSync;

/// <summary>
/// Editor Dock Widget providing zero-friction session management, Steam friend invitations,
/// LAN auto-discovery, smart dual room codes, live collaborator listing, and diagnostics.
/// </summary>
[Dock( "Editor", "Team Sync", "groups" )]
public sealed class TeamSyncDock : Widget
{
	private Layout _content;

	private LineEdit _hostPortInput;
	private LineEdit _joinCodeOrAddressInput;

	private string _cachedStateHash = string.Empty;
	private string _primaryLocalIp = "127.0.0.1";
	private string _publicIp = "Resolving...";
	private string _lastJoinError = string.Empty;

	public TeamSyncDock( Widget parent ) : base( parent )
	{
		Layout = Layout.Column();
		Layout.Margin = 8;
		Layout.Spacing = 6;

		_primaryLocalIp = IpResolver.GetPrimaryLocalIp();
		_ = ResolvePublicIpAsync();

		LanDiscoveryService.Instance.OnSessionsChanged += () =>
		{
			_cachedStateHash = string.Empty;
		};

		BuildUi();
	}

	private async Task ResolvePublicIpAsync()
	{
		_publicIp = await IpResolver.GetPublicIpAsync();
		_cachedStateHash = string.Empty;
	}

	private void BuildUi()
	{
		Layout.Clear( true );

		// Header Bar
		var header = Layout.AddRow();
		header.Spacing = 6;
		header.Alignment = TextFlag.LeftCenter;

		var icon = new IconButton( "groups" );
		header.Add( icon );

		var title = new Label( "Team Sync" );
		title.Color = Theme.Text;
		header.Add( title, 1 );

		var manager = TeamSyncManager.Instance;
		var statusBadge = new Label( manager.StatusText );
		statusBadge.Color = manager.IsSessionActive ? Theme.Green : Theme.Text.WithAlpha( 0.5f );
		header.Add( statusBadge );

		Layout.AddSeparator();

		_content = Layout.AddColumn( 1 );
		_content.Spacing = 8;

		if ( !manager.IsSessionActive )
		{
			BuildDisconnectedView();
		}
		else
		{
			BuildConnectedView();
		}
	}

	private void BuildDisconnectedView()
	{
		_content.Clear( true );

		var discovered = LanDiscoveryService.Instance.DiscoveredSessions.Values.ToList();

		// 1. Discovered Local Sessions (Zero-Config LAN)
		var lanGroup = _content.AddColumn();
		lanGroup.Spacing = 4;

		var lanHeader = lanGroup.AddRow();
		lanHeader.Spacing = 6;
		lanHeader.Alignment = TextFlag.LeftCenter;
		lanHeader.Add( new Label( $"📡 Discovered LAN Sessions ({discovered.Count})" ), 1 );

		if ( discovered.Count == 0 )
		{
			var noLanLabel = lanGroup.Add( new Label( "No active sessions found on local network. Looking for LAN broadcasts..." ) );
			noLanLabel.Color = Theme.Text.WithAlpha( 0.5f );
		}
		else
		{
			foreach ( var session in discovered )
			{
				var card = lanGroup.AddRow();
				card.Spacing = 6;
				card.Alignment = TextFlag.LeftCenter;
				card.Margin = new Sandbox.UI.Margin( 4, 2 );

				var desc = card.Add( new Label( $"🎮 {session.ProjectTitle} ({session.HostPersonaName}) • {session.CollaboratorCount} active" ), 1 );
				desc.Color = Theme.Text;

				var joinLanBtn = new Button.Primary( "⚡ 1-Click Join", "login" );
				joinLanBtn.Clicked = () =>
				{
					_lastJoinError = string.Empty;
					_ = TeamSyncManager.Instance.JoinSessionAsync( session.HostAddress, session.Port );
				};
				card.Add( joinLanBtn );
			}
		}

		_content.AddSeparator();

		// 2. Quick Join via Clipboard or Room Code
		var joinGroup = _content.AddColumn();
		joinGroup.Spacing = 6;

		var joinHeader = joinGroup.AddRow();
		joinHeader.Add( new Label( "Join a Session" ), 1 );

		// Prominent Join from Clipboard Button
		var clipboardJoinBtn = new Button.Primary( "📋 Join from Clipboard", "content_paste" );
		clipboardJoinBtn.Clicked = async () =>
		{
			string clip = EditorUtility.Clipboard.Paste();
			if ( string.IsNullOrWhiteSpace( clip ) )
			{
				Log.Warning( "[TeamSync] Clipboard is empty!" );
				return;
			}

			_lastJoinError = string.Empty;
			bool joined = await TeamSyncManager.Instance.JoinByCodeAsync( clip );
			if ( !joined )
			{
				_lastJoinError = $"Could not connect to target in clipboard. Verify host is running and port is reachable.";
				_cachedStateHash = string.Empty;
			}
		};
		joinGroup.Add( clipboardJoinBtn );

		var joinControls = joinGroup.AddRow();
		joinControls.Spacing = 6;

		_joinCodeOrAddressInput = new LineEdit( "" );
		_joinCodeOrAddressInput.PlaceholderText = "Paste Room Code, Link, or Host:Port (e.g. SYNC-...)";
		joinControls.Add( _joinCodeOrAddressInput, 1 );

		var directJoinBtn = new Button( "Join", "arrow_forward" );
		directJoinBtn.Clicked = async () =>
		{
			string input = _joinCodeOrAddressInput.Text;
			if ( !string.IsNullOrWhiteSpace( input ) )
			{
				_lastJoinError = string.Empty;
				bool joined = await TeamSyncManager.Instance.JoinByCodeAsync( input );
				if ( !joined )
				{
					_lastJoinError = $"Connection failed to '{input}'. If connecting over the internet, verify host forwarded port 29015 or is on Tailscale.";
					_cachedStateHash = string.Empty;
				}
			}
		};
		joinControls.Add( directJoinBtn );

		// Last connection error banner if any
		if ( !string.IsNullOrEmpty( _lastJoinError ) )
		{
			var errorLabel = joinGroup.Add( new Label( $"⚠️ {_lastJoinError}" ) );
			errorLabel.Color = Theme.Red;
		}

		_content.AddSeparator();

		// 3. Host a Session Section
		var hostGroup = _content.AddColumn();
		hostGroup.Spacing = 6;

		var hostHeader = hostGroup.AddRow();
		hostHeader.Add( new Label( "Host a Session" ), 1 );

		// Network IPs row
		var ipInfoRow = hostGroup.AddRow();
		ipInfoRow.Spacing = 6;
		ipInfoRow.Alignment = TextFlag.LeftCenter;

		var lanBadge = new Label( $"LAN: {_primaryLocalIp}" );
		lanBadge.Color = Theme.Text.WithAlpha( 0.7f );
		ipInfoRow.Add( lanBadge );

		var wanBadge = new Label( $"Public: {_publicIp}" );
		wanBadge.Color = Theme.Blue;
		ipInfoRow.Add( wanBadge, 1 );

		var hostControls = hostGroup.AddRow();
		hostControls.Spacing = 6;
		hostControls.Add( new Label( "Port:" ) );

		_hostPortInput = new LineEdit( "29020" );
		_hostPortInput.FixedWidth = 65;
		hostControls.Add( _hostPortInput );

		var hostButton = new Button.Primary( "Start Hosting", "sensors" );
		hostButton.Clicked = () =>
		{
			int port = int.TryParse( _hostPortInput.Text, out var p ) ? p : 29020;
			_ = TeamSyncManager.Instance.HostSessionAsync( port );
		};
		hostControls.Add( hostButton, 1 );

		var hostHelp = hostGroup.Add( new Label( "Tip: For internet friends, port 29020 must be reachable (router forwarded or via Tailscale/ZeroTier)." ) );
		hostHelp.Color = Theme.Text.WithAlpha( 0.5f );

		_content.AddSeparator();

		// 4. Diagnostics & Testing
		var testGroup = _content.AddColumn();
		testGroup.Spacing = 4;

		var testHeader = testGroup.AddRow();
		testHeader.Add( new Label( "Diagnostics & Local Simulation" ) );

		var testButtons = testGroup.AddRow();
		testButtons.Spacing = 6;

		var runTestsBtn = new Button( "Run Self-Tests", "checklist" );
		runTestsBtn.Clicked = () =>
		{
			TeamSyncTests.RunAllTestsCmd();
		};
		testButtons.Add( runTestsBtn, 1 );

		var loopbackBtn = new Button( "Simulate 2-Peer Session", "swap_horiz" );
		loopbackBtn.Clicked = () =>
		{
			StartLoopbackSimulation();
		};
		testButtons.Add( loopbackBtn, 1 );
	}

	private void BuildConnectedView()
	{
		_content.Clear( true );
		var manager = TeamSyncManager.Instance;

		string roomCode = manager.GetActiveRoomCode();
		string publicRoomCode = manager.GetPublicRoomCode();
		string localRoomCode = manager.GetLocalRoomCode();
		int port = (manager.Transport as TeamSyncServer)?.Port ?? 29015;
		string publicEndpoint = $"{_publicIp}:{port}";
		string lanEndpoint = $"{_primaryLocalIp}:{port}";

		// 1. Session Bar
		var sessionBar = _content.AddRow();
		sessionBar.Spacing = 6;
		sessionBar.Alignment = TextFlag.LeftCenter;

		string mode = manager.IsHost ? "Hosting" : "Connected as Client";
		var sessionTitle = new Label( $"Session: {mode}" );
		sessionTitle.Color = Theme.Green;
		sessionBar.Add( sessionTitle, 1 );

		var leaveBtn = new Button( "Leave Session", "logout" );
		leaveBtn.Clicked = () =>
		{
			_ = manager.LeaveSessionAsync();
		};
		sessionBar.Add( leaveBtn );

		// 2. Share & Invite Box (for Host)
		if ( manager.IsHost )
		{
			var inviteGroup = _content.AddColumn();
			inviteGroup.Spacing = 6;

			var codeRow = inviteGroup.AddRow();
			codeRow.Spacing = 6;
			codeRow.Alignment = TextFlag.LeftCenter;
			codeRow.Add( new Label( "Room Code:" ) );

			var codeBadge = new Label( string.IsNullOrEmpty( roomCode ) ? lanEndpoint : roomCode );
			codeBadge.Color = Theme.Blue;
			codeRow.Add( codeBadge, 1 );

			var inviteButtonsRow = inviteGroup.AddRow();
			inviteButtonsRow.Spacing = 6;

			// Steam Invite Button (uses public WAN / Smart Code so internet friends can connect)
			var steamInviteBtn = new Button.Primary( "🎮 Invite via Steam", "sports_esports" );
			steamInviteBtn.ToolTip = "Copies formatted invite with public IP to clipboard and opens Steam Friends list";
			steamInviteBtn.Clicked = () =>
			{
				string projectTitle = Project.Current?.Config?.Title ?? "s_collab";
				string shareCode = !string.IsNullOrEmpty( publicRoomCode ) ? publicRoomCode : roomCode;
				SteamInviteHelper.InviteViaSteam( manager.LocalPersonaName, projectTitle, shareCode, publicEndpoint );
			};
			inviteButtonsRow.Add( steamInviteBtn, 1 );

			// Copy Internet Code Button
			var copyWanCodeBtn = new Button( "🌐 Copy Internet Code", "public" );
			copyWanCodeBtn.ToolTip = "Room Code for friends across the internet (Public WAN IP)";
			copyWanCodeBtn.Clicked = () =>
			{
				string codeToCopy = !string.IsNullOrEmpty( publicRoomCode ) ? publicRoomCode : roomCode;
				EditorUtility.Clipboard.Copy( codeToCopy );
				Log.Info( $"[TeamSync] Internet Room Code copied to clipboard: {codeToCopy}" );
			};
			inviteButtonsRow.Add( copyWanCodeBtn, 1 );

			// Copy LAN Code Button
			var copyLanCodeBtn = new Button( "🏠 Copy LAN Code", "home" );
			copyLanCodeBtn.ToolTip = "Room Code for friends on the same local Wi-Fi / network";
			copyLanCodeBtn.Clicked = () =>
			{
				string codeToCopy = !string.IsNullOrEmpty( localRoomCode ) ? localRoomCode : lanEndpoint;
				EditorUtility.Clipboard.Copy( codeToCopy );
				Log.Info( $"[TeamSync] Local LAN Room Code copied to clipboard: {codeToCopy}" );
			};
			inviteButtonsRow.Add( copyLanCodeBtn, 1 );

			_content.AddSeparator();
		}

		// 3. Collaborators Section
		var collabHeader = _content.AddRow();
		collabHeader.Add( new Label( $"Collaborators ({manager.Collaborators.Count})" ), 1 );

		var scrollArea = _content.Add( new ScrollArea( this ), 1 );
		var list = new Widget( scrollArea );
		var listLayout = list.Layout = Layout.Column();
		listLayout.Spacing = 4;
		scrollArea.Canvas = list;

		foreach ( var peer in manager.Collaborators.Values.OrderByDescending( x => x.IsHost ) )
		{
			var row = listLayout.AddRow();
			row.Spacing = 6;
			row.Alignment = TextFlag.LeftCenter;
			row.Margin = new Sandbox.UI.Margin( 4, 4 );

			// Color Dot
			var dot = new Label( "●" );
			dot.Color = peer.Color;
			row.Add( dot );

			// Name & Badges
			string nameText = peer.PersonaName;
			if ( peer.PeerId == manager.LocalPeerId ) nameText += " (You)";
			if ( peer.IsHost ) nameText += " ★";

			var nameLabel = new Label( nameText );
			row.Add( nameLabel, 1 );

			// Status / Selection
			string status = "Idle";
			if ( peer.SelectedObjectIds.Count > 0 )
			{
				status = $"Editing ({peer.SelectedObjectIds.Count})";
			}
			var statusLabel = new Label( status );
			statusLabel.Color = Theme.Text.WithAlpha( 0.6f );
			row.Add( statusLabel );
		}

		_content.AddSeparator();

		// 4. Active Locks Section
		var lockHeader = _content.AddRow();
		lockHeader.Add( new Label( $"Active Object Locks ({manager.LockSystem.ActiveLocks.Count})" ), 1 );

		if ( manager.LockSystem.ActiveLocks.Count == 0 )
		{
			var emptyLabel = new Label( "No objects are currently locked." );
			emptyLabel.Color = Theme.Text.WithAlpha( 0.5f );
			_content.Add( emptyLabel );
		}
		else
		{
			foreach ( var kvp in manager.LockSystem.ActiveLocks )
			{
				var lockRow = _content.AddRow();
				lockRow.Spacing = 6;

				string holderName = manager.Collaborators.TryGetValue( kvp.Value, out var holder ) ? holder.PersonaName : kvp.Value;
				lockRow.Add( new Label( $"🔒 {kvp.Key.Substring( 0, Math.Min( 8, kvp.Key.Length ) )}... held by {holderName}" ) );
			}
		}
	}

	private void StartLoopbackSimulation()
	{
		var (host, client) = LoopbackTransport.CreatePair( "host_editor", "client_editor" );
		TeamSyncManager.Instance.AttachCustomTransport( host );
		_ = host.StartAsync();
		_ = client.StartAsync();

		Vector3 spawnPos = new Vector3( 0, 0, 50 );
		Angles spawnAngles = Angles.Zero;

		var editorCam = Application.Editor?.Camera;
		if ( editorCam.IsValid() )
		{
			spawnPos = editorCam.WorldPosition + editorCam.WorldRotation.Forward * 180f;
			spawnAngles = new Angles( -editorCam.WorldRotation.Angles().pitch, editorCam.WorldRotation.Angles().yaw + 180f, 0 );
		}
		else
		{
			try
			{
				if ( Gizmo.Camera != null )
				{
					spawnPos = Gizmo.Camera.Position + Gizmo.Camera.Rotation.Forward * 180f;
					spawnAngles = new Angles( -Gizmo.Camera.Angles.pitch, Gizmo.Camera.Angles.yaw + 180f, 0 );
				}
			}
			catch
			{
				// Safe fallback
			}
		}

		// Add simulated collaborator
		var simPeer = new CollaboratorState( "client_editor", "Simulated Peer (Alice)", 99999, "#10B981" )
		{
			CameraPosition = spawnPos,
			CameraAngles = spawnAngles
		};
		TeamSyncManager.Instance.Collaborators["client_editor"] = simPeer;

		Log.Info( $"[TeamSync] Loopback simulation started with 'Simulated Peer (Alice)' in front of viewport at {spawnPos}!" );
		BuildUi();
	}

	[EditorEvent.Frame]
	private void FrameUpdate()
	{
		if ( !Visible ) return;

		var manager = TeamSyncManager.Instance;
		int discoveredCount = LanDiscoveryService.Instance.DiscoveredSessions.Count;
		string currentStateHash = $"{manager.IsSessionActive}_{manager.Collaborators.Count}_{manager.LockSystem.ActiveLocks.Count}_{manager.StatusText}_{discoveredCount}_{_publicIp}_{_lastJoinError}";

		if ( currentStateHash != _cachedStateHash )
		{
			_cachedStateHash = currentStateHash;
			BuildUi();
		}
	}
}
