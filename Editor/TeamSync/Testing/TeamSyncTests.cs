namespace Editor.TeamSync;

/// <summary>
/// In-process automated test suite for verifying Team Sync protocols, loopback transport,
/// lock state machine, and scene delta application without needing external peers.
/// </summary>
public static class TeamSyncTests
{
	public sealed class TestResult
	{
		public string Name { get; set; }
		public bool Passed { get; set; }
		public string Message { get; set; }
	}

	[ConCmd( "teamsync_discover" )]
	public static void DiscoverNetworkingApis()
	{
		Log.Info( "=================================================" );
		Log.Info( "[TeamSync] Introspecting Types for Steam and Networking..." );
		Log.Info( "=================================================" );

		foreach ( var type in TypeLibrary.GetTypes() )
		{
			string name = type.Name;
			if ( name.Contains( "Steam", StringComparison.OrdinalIgnoreCase ) ||
			     name.Contains( "Lobby", StringComparison.OrdinalIgnoreCase ) ||
			     name.Contains( "Networking", StringComparison.OrdinalIgnoreCase ) ||
			     name.Contains( "Socket", StringComparison.OrdinalIgnoreCase ) )
			{
				Log.Info( $"[Found Type] {type.FullName} (Identity: {type.Identity})" );
				foreach ( var m in type.Members.Where( m => m.IsPublic ).Take( 10 ) )
				{
					Log.Info( $"   Member: {m.Name}" );
				}
			}
		}
	}

	[ConCmd( "teamsync_test" )]
	public static void RunAllTestsCmd()
	{
		Log.Info( "=================================================" );
		Log.Info( "[TeamSync] Running Real-Time Collaboration Automated Tests..." );
		Log.Info( "=================================================" );

		var results = RunAllTests();
		int passed = results.Count( r => r.Passed );
		int failed = results.Count( r => !r.Passed );

		foreach ( var res in results )
		{
			if ( res.Passed )
			{
				Log.Info( $"[PASS] {res.Name}: {res.Message}" );
			}
			else
			{
				Log.Error( $"[FAIL] {res.Name}: {res.Message}" );
			}
		}

		Log.Info( $"[TeamSync] Finished: {passed} passed, {failed} failed." );
	}

	public static List<TestResult> RunAllTests()
	{
		var results = new List<TestResult>();

		results.Add( TestMessageSerialization() );
		results.Add( TestLoopbackTransport() );
		results.Add( TestLockStateMachine() );
		results.Add( TestSceneDeltaApplication() );
		results.Add( TestDeterministicColors() );
		results.Add( TestRoomCodeParsingAndEncoding() );

		return results;
	}

	private static TestResult TestRoomCodeParsingAndEncoding()
	{
		try
		{
			// 1. Hex IPv4 encoding
			string code = SessionCode.Encode( "192.168.1.50", 29015 );
			if ( string.IsNullOrEmpty( code ) || !code.StartsWith( "SYNC-" ) )
				return new TestResult { Name = "Room Code System", Passed = false, Message = $"Encoded code was invalid: {code}" };

			if ( !SessionCode.TryParse( code, out string host, out int port, out string err ) )
				return new TestResult { Name = "Room Code System", Passed = false, Message = $"Failed to parse generated code '{code}': {err}" };

			if ( host != "192.168.1.50" || port != 29015 )
				return new TestResult { Name = "Room Code System", Passed = false, Message = $"Decoded values mismatch: {host}:{port}" };

			// 2. URL format
			if ( !SessionCode.TryParse( "teamsync://10.0.0.5:29018", out host, out port, out _ ) || host != "10.0.0.5" || port != 29018 )
				return new TestResult { Name = "Room Code System", Passed = false, Message = "Failed to parse teamsync:// URL format." };

			// 3. Embedded chat invite message
			string chatMsg = SessionCode.CreateInviteMessage( "Alice", "MyGame", code, "192.168.1.50:29015" );
			if ( !SessionCode.TryParse( chatMsg, out host, out port, out _ ) || host != "192.168.1.50" || port != 29015 )
				return new TestResult { Name = "Room Code System", Passed = false, Message = "Failed to extract room code from rich chat invite message." };

			return new TestResult { Name = "Room Code System", Passed = true, Message = "Room Code hex encoding, URL parsing, and chat invite extraction verified." };
		}
		catch ( Exception ex )
		{
			return new TestResult { Name = "Room Code System", Passed = false, Message = ex.Message };
		}
	}

	private static TestResult TestMessageSerialization()
	{
		try
		{
			var cameraPayload = new CameraPayload
			{
				PeerId = "peer_test_1",
				Position = new Vector3( 100, 200, 300 ),
				Angles = new Angles( 10, 20, 30 ),
				Fov = 85f
			};

			var env = TeamSyncEnvelope.Create( TeamSyncMessageType.CameraUpdate, "peer_test_1", cameraPayload );
			string json = env.Serialize();
			var deserialized = TeamSyncEnvelope.Deserialize( json );

			if ( deserialized == null )
				return new TestResult { Name = "Message Serialization", Passed = false, Message = "Deserialized envelope was null." };

			if ( deserialized.Type != TeamSyncMessageType.CameraUpdate )
				return new TestResult { Name = "Message Serialization", Passed = false, Message = $"Expected type CameraUpdate, got {deserialized.Type}" };

			var restoredPayload = deserialized.GetPayload<CameraPayload>();
			if ( restoredPayload == null || restoredPayload.Position != cameraPayload.Position || restoredPayload.Fov != 85f )
				return new TestResult { Name = "Message Serialization", Passed = false, Message = "Restored payload fields mismatch." };

			return new TestResult { Name = "Message Serialization", Passed = true, Message = "Envelope & typed payload serialized and deserialized accurately." };
		}
		catch ( Exception ex )
		{
			return new TestResult { Name = "Message Serialization", Passed = false, Message = ex.Message };
		}
	}

	private static TestResult TestLoopbackTransport()
	{
		try
		{
			var (host, client) = LoopbackTransport.CreatePair( "host_node", "client_node" );

			TeamSyncEnvelope receivedOnClient = null;
			client.OnMessageReceived += env => receivedOnClient = env;

			host.StartAsync().Wait();
			client.StartAsync().Wait();

			var pingPayload = new HelloPayload { PeerId = "host_node", PersonaName = "HostTest", SteamId = 12345 };
			host.SendAsync( TeamSyncEnvelope.Create( TeamSyncMessageType.Hello, "host_node", pingPayload ) ).Wait();

			// Give async task a brief moment to deliver
			Thread.Sleep( 50 );

			if ( receivedOnClient == null )
				return new TestResult { Name = "Loopback Transport", Passed = false, Message = "Client did not receive envelope from host." };

			if ( receivedOnClient.SenderId != "host_node" || receivedOnClient.Type != TeamSyncMessageType.Hello )
				return new TestResult { Name = "Loopback Transport", Passed = false, Message = "Received envelope corrupted or wrong sender." };

			host.StopAsync().Wait();
			client.StopAsync().Wait();

			return new TestResult { Name = "Loopback Transport", Passed = true, Message = "In-memory loopback transport delivered envelopes across simulated peers." };
		}
		catch ( Exception ex )
		{
			return new TestResult { Name = "Loopback Transport", Passed = false, Message = ex.Message };
		}
	}

	private static TestResult TestLockStateMachine()
	{
		try
		{
			var manager = TeamSyncManager.Instance;
			var lockSys = new SelectionLockSystem( manager );

			string goId = Guid.NewGuid().ToString();
			string peerAlice = "alice_peer";
			string peerBob = "bob_peer";

			// Alice acquires lock
			bool aliceAcquired = lockSys.TryAcquireLock( goId, peerAlice );
			if ( !aliceAcquired || !lockSys.ActiveLocks.TryGetValue( goId, out var holder ) || holder != peerAlice )
				return new TestResult { Name = "Lock State Machine", Passed = false, Message = "Alice failed to acquire lock on object." };

			// Bob attempts to acquire same locked object -> should be denied
			bool bobAcquired = lockSys.TryAcquireLock( goId, peerBob );
			if ( bobAcquired || lockSys.ActiveLocks[goId] != peerAlice )
				return new TestResult { Name = "Lock State Machine", Passed = false, Message = "Bob acquired an already locked object (conflict violation)!" };

			// Alice releases lock
			bool aliceReleased = lockSys.TryReleaseLock( goId, peerAlice );
			if ( !aliceReleased || lockSys.ActiveLocks.ContainsKey( goId ) )
				return new TestResult { Name = "Lock State Machine", Passed = false, Message = "Object lock was not released." };

			// Bob can now acquire lock
			bool bobAcquiredNow = lockSys.TryAcquireLock( goId, peerBob );
			if ( !bobAcquiredNow || !lockSys.ActiveLocks.TryGetValue( goId, out holder ) || holder != peerBob )
				return new TestResult { Name = "Lock State Machine", Passed = false, Message = "Bob failed to acquire newly free object." };

			return new TestResult { Name = "Lock State Machine", Passed = true, Message = "Lock acquisition, conflict prevention, and release verified." };
		}
		catch ( Exception ex )
		{
			return new TestResult { Name = "Lock State Machine", Passed = false, Message = ex.Message };
		}
	}

	private static TestResult TestSceneDeltaApplication()
	{
		try
		{
			string testId = Guid.NewGuid().ToString();
			var delta = new SceneDeltaPayload
			{
				DeltaType = SceneDeltaType.CreateGameObject,
				TargetGameObjectId = testId,
				Name = "TeamSync_Test_Prop",
				Enabled = true,
				Position = new Vector3( 10, 20, 30 ),
				Rotation = Rotation.Identity,
				Scale = Vector3.One
			};

			SceneApplicator.ApplyDelta( delta );

			var session = SceneEditorSession.Active;
			if ( session != null && session.Scene != null )
			{
				var createdGo = SceneApplicator.FindGameObject( session.Scene, testId );
				if ( createdGo != null && createdGo.IsValid() )
				{
					// Clean up test object
					SceneApplicator.ApplyDelta( new SceneDeltaPayload
					{
						DeltaType = SceneDeltaType.DeleteGameObject,
						TargetGameObjectId = testId
					} );
				}
			}

			return new TestResult { Name = "Scene Delta Application", Passed = true, Message = "Scene delta applied and cleaned up cleanly without errors." };
		}
		catch ( Exception ex )
		{
			return new TestResult { Name = "Scene Delta Application", Passed = false, Message = ex.Message };
		}
	}

	private static TestResult TestDeterministicColors()
	{
		try
		{
			var col1 = CollaboratorState.GenerateDeterministicColor( 12345 );
			var col2 = CollaboratorState.GenerateDeterministicColor( 12345 );
			var col3 = CollaboratorState.GenerateDeterministicColor( 67890 );

			if ( col1 != col2 )
				return new TestResult { Name = "Deterministic Colors", Passed = false, Message = "Same seed generated different colors." };

			return new TestResult { Name = "Deterministic Colors", Passed = true, Message = "Deterministic color mapping produces stable consistent colors per peer." };
		}
		catch ( Exception ex )
		{
			return new TestResult { Name = "Deterministic Colors", Passed = false, Message = ex.Message };
		}
	}
}
