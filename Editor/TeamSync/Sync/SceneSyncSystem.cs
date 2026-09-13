namespace Editor.TeamSync;

/// <summary>
/// Monitors the active Editor scene graph for changes and broadcasts deltas to collaborators.
/// Prevents echo feedback loops by checking SceneApplicator suppression state.
/// </summary>
public sealed class SceneSyncSystem
{
	private readonly TeamSyncManager _manager;

	private sealed class TrackedState
	{
		public string Name;
		public bool Enabled;
		public string ParentId;
		public Vector3 Position;
		public Rotation Rotation;
		public Vector3 Scale;
		public bool IsStatic;
		public List<string> Tags = new();
		public NetworkMode NetworkMode;
		public bool Networked;
		public bool NetworkInterpolation;
		public string PrefabSource;
		public Dictionary<Guid, string> Components = new();
	}

	private readonly Dictionary<string, TrackedState> _trackedObjects = new();

	public SceneSyncSystem( TeamSyncManager manager )
	{
		_manager = manager;
	}

	public static bool ShouldIgnore( GameObject go )
	{
		if ( go == null || !go.IsValid() ) return true;
		if ( go.Flags.HasFlag( GameObjectFlags.Hidden ) ) return true;

		var name = go.Name;
		if ( string.IsNullOrEmpty( name ) ) return false;

		// Ignore internal editor viewport cameras and editor helper objects
		if ( name.Equals( "editor_camera", StringComparison.OrdinalIgnoreCase ) ||
		     name.StartsWith( "editor_", StringComparison.OrdinalIgnoreCase ) )
		{
			return true;
		}

		return false;
	}

	private static List<string> GetTagsList( GameObject go )
	{
		try
		{
			if ( go.Tags != null )
			{
				return go.Tags.TryGetAll()?.OrderBy( x => x ).ToList() ?? new List<string>();
			}
		}
		catch { }
		return new List<string>();
	}

	private static Dictionary<Guid, string> SnapshotComponents( GameObject go )
	{
		var dict = new Dictionary<Guid, string>();
		if ( go == null || !go.IsValid() ) return dict;

		try
		{
			foreach ( var comp in go.Components.GetAll() )
			{
				if ( comp == null || !comp.IsValid() ) continue;
				try
				{
					var node = comp.Serialize();
					if ( node != null )
					{
						dict[comp.Id] = node.ToJsonString();
					}
				}
				catch { }
			}
		}
		catch { }

		return dict;
	}

	public void RegisterRemoteObject( GameObject go )
	{
		if ( go == null || !go.IsValid() || ShouldIgnore( go ) ) return;
		string id = go.Id.ToString();
		_trackedObjects[id] = new TrackedState
		{
			Name = go.Name,
			Enabled = go.Enabled,
			ParentId = go.Parent?.Id.ToString(),
			Position = go.WorldPosition,
			Rotation = go.WorldRotation,
			Scale = go.WorldScale,
			IsStatic = go.IsStatic,
			Tags = GetTagsList( go ),
			NetworkMode = go.NetworkMode,
			Networked = go.Networked,
			NetworkInterpolation = go.NetworkInterpolation,
			PrefabSource = go.PrefabInstanceSource,
			Components = SnapshotComponents( go )
		};
	}

	public void RegisterRemoteComponent( string goId, Guid compId, string compJson )
	{
		if ( _trackedObjects.TryGetValue( goId, out var tracked ) )
		{
			tracked.Components[compId] = compJson;
		}
	}

	public void UnregisterRemoteComponent( string goId, Guid compId )
	{
		if ( _trackedObjects.TryGetValue( goId, out var tracked ) )
		{
			tracked.Components.Remove( compId );
		}
	}

	public void UnregisterRemoteObject( string id )
	{
		if ( !string.IsNullOrEmpty( id ) )
		{
			_trackedObjects.Remove( id );
		}
	}

	public void Reset()
	{
		_trackedObjects.Clear();
		RebuildBaseline();
	}

	public void RebuildBaseline()
	{
		_trackedObjects.Clear();
		var session = SceneEditorSession.Active;
		if ( session == null || session.Scene == null ) return;

		foreach ( var go in session.Scene.GetAllObjects( false ) )
		{
			if ( ShouldIgnore( go ) ) continue;

			string id = go.Id.ToString();
			_trackedObjects[id] = new TrackedState
			{
				Name = go.Name,
				Enabled = go.Enabled,
				ParentId = go.Parent?.Id.ToString(),
				Position = go.WorldPosition,
				Rotation = go.WorldRotation,
				Scale = go.WorldScale,
				IsStatic = go.IsStatic,
				Tags = GetTagsList( go ),
				NetworkMode = go.NetworkMode,
				Networked = go.Networked,
				NetworkInterpolation = go.NetworkInterpolation,
				PrefabSource = go.PrefabInstanceSource,
				Components = SnapshotComponents( go )
			};
		}
	}

	[EditorEvent.Frame]
	public void FrameUpdate()
	{
		if ( !_manager.IsSessionActive || Game.IsPlaying ) return;
		if ( SceneApplicator.IsApplyingRemoteChange ) return;

		var session = SceneEditorSession.Active;
		if ( session == null || session.Scene == null ) return;

		// Scan for changes - ignore editor_camera, hidden, or editor-internal objects
		var currentSceneObjects = session.Scene.GetAllObjects( false )
			.Where( x => !ShouldIgnore( x ) )
			.ToList();
		var currentIds = new HashSet<string>();

		foreach ( var go in currentSceneObjects )
		{
			string id = go.Id.ToString();
			currentIds.Add( id );

			if ( !_trackedObjects.TryGetValue( id, out var tracked ) )
			{
				// New GameObject created locally!
				tracked = new TrackedState
				{
					Name = go.Name,
					Enabled = go.Enabled,
					ParentId = go.Parent?.Id.ToString(),
					Position = go.WorldPosition,
					Rotation = go.WorldRotation,
					Scale = go.WorldScale,
					IsStatic = go.IsStatic,
					Tags = GetTagsList( go ),
					NetworkMode = go.NetworkMode,
					Networked = go.Networked,
					NetworkInterpolation = go.NetworkInterpolation,
					PrefabSource = go.PrefabInstanceSource,
					Components = SnapshotComponents( go )
				};
				_trackedObjects[id] = tracked;

				_manager.BroadcastSceneDelta( new SceneDeltaPayload
				{
					DeltaType = SceneDeltaType.CreateGameObject,
					TargetGameObjectId = id,
					TargetParentId = tracked.ParentId,
					Name = tracked.Name,
					Enabled = tracked.Enabled,
					Position = tracked.Position,
					Rotation = tracked.Rotation,
					Scale = tracked.Scale,
					IsStatic = tracked.IsStatic,
					Tags = tracked.Tags,
					NetworkMode = (int)tracked.NetworkMode,
					Networked = tracked.Networked,
					NetworkInterpolation = tracked.NetworkInterpolation,
					PrefabSource = tracked.PrefabSource
				} );

				// If it already has components (e.g. spawned model renderer, collider, or prefab components)
				foreach ( var comp in go.Components.GetAll() )
				{
					if ( comp == null || !comp.IsValid() ) continue;
					string json = null;
					try
					{
						json = comp.Serialize()?.ToJsonString();
					}
					catch { }

					if ( json != null )
					{
						_manager.BroadcastSceneDelta( new SceneDeltaPayload
						{
							DeltaType = SceneDeltaType.AddComponent,
							TargetGameObjectId = id,
							ComponentId = comp.Id.ToString(),
							ComponentType = comp.GetType().FullName,
							ComponentJson = json
						} );
					}
				}
			}
			else
			{
				// 1. Check for transform mutations (position, rotation, scale)
				if ( (tracked.Position - go.WorldPosition).LengthSquared > 0.0001f ||
				     tracked.Rotation != go.WorldRotation ||
				     (tracked.Scale - go.WorldScale).LengthSquared > 0.0001f )
				{
					tracked.Position = go.WorldPosition;
					tracked.Rotation = go.WorldRotation;
					tracked.Scale = go.WorldScale;

					_manager.BroadcastSceneDelta( new SceneDeltaPayload
					{
						DeltaType = SceneDeltaType.SetTransform,
						TargetGameObjectId = id,
						Position = tracked.Position,
						Rotation = tracked.Rotation,
						Scale = tracked.Scale
					} );
				}

				// 2. Check for parent changes
				string currentParentId = go.Parent?.Id.ToString();
				if ( tracked.ParentId != currentParentId )
				{
					tracked.ParentId = currentParentId;
					_manager.BroadcastSceneDelta( new SceneDeltaPayload
					{
						DeltaType = SceneDeltaType.SetParent,
						TargetGameObjectId = id,
						TargetParentId = currentParentId
					} );
				}

				// 3. Check for enabled state changes
				if ( tracked.Enabled != go.Enabled )
				{
					tracked.Enabled = go.Enabled;
					_manager.BroadcastSceneDelta( new SceneDeltaPayload
					{
						DeltaType = SceneDeltaType.SetEnabled,
						TargetGameObjectId = id,
						Enabled = tracked.Enabled
					} );
				}

				// 4. Check for name changes
				if ( tracked.Name != go.Name )
				{
					tracked.Name = go.Name;
					_manager.BroadcastSceneDelta( new SceneDeltaPayload
					{
						DeltaType = SceneDeltaType.SetName,
						TargetGameObjectId = id,
						Name = tracked.Name
					} );
				}

				// 5. Check for IsStatic changes
				if ( tracked.IsStatic != go.IsStatic )
				{
					tracked.IsStatic = go.IsStatic;
					_manager.BroadcastSceneDelta( new SceneDeltaPayload
					{
						DeltaType = SceneDeltaType.SetStatic,
						TargetGameObjectId = id,
						IsStatic = tracked.IsStatic
					} );
				}

				// 6. Check for Tags changes
				var currentTags = GetTagsList( go );
				if ( !currentTags.SequenceEqual( tracked.Tags ) )
				{
					tracked.Tags = currentTags;
					_manager.BroadcastSceneDelta( new SceneDeltaPayload
					{
						DeltaType = SceneDeltaType.SetTags,
						TargetGameObjectId = id,
						Tags = currentTags
					} );
				}

				// 7. Check for NetworkMode / Networked / NetworkInterpolation changes
				if ( tracked.NetworkMode != go.NetworkMode ||
				     tracked.Networked != go.Networked ||
				     tracked.NetworkInterpolation != go.NetworkInterpolation )
				{
					tracked.NetworkMode = go.NetworkMode;
					tracked.Networked = go.Networked;
					tracked.NetworkInterpolation = go.NetworkInterpolation;

					_manager.BroadcastSceneDelta( new SceneDeltaPayload
					{
						DeltaType = SceneDeltaType.SetNetworkMode,
						TargetGameObjectId = id,
						NetworkMode = (int)tracked.NetworkMode,
						Networked = tracked.Networked,
						NetworkInterpolation = tracked.NetworkInterpolation
					} );
				}

				// 8. Check for Component changes (Add, Update properties/variables, Remove)
				var currentComps = go.Components.GetAll().Where( c => c != null && c.IsValid() ).ToList();
				var currentCompGuids = new HashSet<Guid>();

				foreach ( var comp in currentComps )
				{
					currentCompGuids.Add( comp.Id );
					string json = null;
					try
					{
						json = comp.Serialize()?.ToJsonString();
					}
					catch { }

					if ( json == null ) continue;

					if ( !tracked.Components.TryGetValue( comp.Id, out var prevJson ) )
					{
						// New component added locally
						tracked.Components[comp.Id] = json;
						_manager.BroadcastSceneDelta( new SceneDeltaPayload
						{
							DeltaType = SceneDeltaType.AddComponent,
							TargetGameObjectId = id,
							ComponentId = comp.Id.ToString(),
							ComponentType = comp.GetType().FullName,
							ComponentJson = json
						} );
					}
					else if ( prevJson != json )
					{
						// Component variables/properties updated locally
						tracked.Components[comp.Id] = json;
						_manager.BroadcastSceneDelta( new SceneDeltaPayload
						{
							DeltaType = SceneDeltaType.UpdateComponent,
							TargetGameObjectId = id,
							ComponentId = comp.Id.ToString(),
							ComponentType = comp.GetType().FullName,
							ComponentJson = json
						} );
					}
				}

				// Check for removed components
				var removedGuids = tracked.Components.Keys.Where( g => !currentCompGuids.Contains( g ) ).ToList();
				foreach ( var guid in removedGuids )
				{
					tracked.Components.Remove( guid );
					_manager.BroadcastSceneDelta( new SceneDeltaPayload
					{
						DeltaType = SceneDeltaType.RemoveComponent,
						TargetGameObjectId = id,
						ComponentId = guid.ToString()
					} );
				}
			}
		}

		// Check for deleted GameObjects
		var deletedIds = _trackedObjects.Keys.Where( k => !currentIds.Contains( k ) ).ToList();
		foreach ( var deletedId in deletedIds )
		{
			_trackedObjects.Remove( deletedId );
			_manager.BroadcastSceneDelta( new SceneDeltaPayload
			{
				DeltaType = SceneDeltaType.DeleteGameObject,
				TargetGameObjectId = deletedId
			} );
		}
	}
}
