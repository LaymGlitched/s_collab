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
	}

	private readonly Dictionary<string, TrackedState> _trackedObjects = new();

	public SceneSyncSystem( TeamSyncManager manager )
	{
		_manager = manager;
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
			if ( go == null || !go.IsValid() ) continue;

			string id = go.Id.ToString();
			_trackedObjects[id] = new TrackedState
			{
				Name = go.Name,
				Enabled = go.Enabled,
				ParentId = go.Parent?.Id.ToString(),
				Position = go.WorldPosition,
				Rotation = go.WorldRotation,
				Scale = go.WorldScale
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

		// Scan for changes
		var currentSceneObjects = session.Scene.GetAllObjects( false ).Where( x => x.IsValid() ).ToList();
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
					Scale = go.WorldScale
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
					Scale = tracked.Scale
				} );
			}
			else
			{
				// Check for transform mutations (position, rotation, scale)
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

				// Check for parent changes
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

				// Check for enabled state changes
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

				// Check for name changes
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
