namespace Editor.TeamSync;

/// <summary>
/// Safely applies remote scene deltas to the local Editor Scene graph
/// without causing feedback loops or polluting the local undo stack.
/// </summary>
public static class SceneApplicator
{
	public static bool IsApplyingRemoteChange { get; private set; }

	// Maps remote GameObject ID string to local GameObject reference
	private static readonly Dictionary<string, GameObject> _remoteToLocalMap = new();

	public static void ResetMapping()
	{
		_remoteToLocalMap.Clear();
	}

	public static void ApplyDelta( SceneDeltaPayload delta )
	{
		if ( delta == null ) return;

		var session = SceneEditorSession.Active;
		if ( session == null || session.Scene == null ) return;

		IsApplyingRemoteChange = true;
		try
		{
			switch ( delta.DeltaType )
			{
				case SceneDeltaType.CreateGameObject:
					ApplyCreateGameObject( session.Scene, delta );
					break;

				case SceneDeltaType.DeleteGameObject:
					ApplyDeleteGameObject( session.Scene, delta );
					break;

				case SceneDeltaType.SetTransform:
					ApplySetTransform( session.Scene, delta );
					break;

				case SceneDeltaType.SetParent:
					ApplySetParent( session.Scene, delta );
					break;

				case SceneDeltaType.SetEnabled:
					ApplySetEnabled( session.Scene, delta );
					break;

				case SceneDeltaType.SetName:
					ApplySetName( session.Scene, delta );
					break;

				case SceneDeltaType.SetStatic:
					ApplySetStatic( session.Scene, delta );
					break;

				case SceneDeltaType.SetTags:
					ApplySetTags( session.Scene, delta );
					break;

				case SceneDeltaType.SetNetworkMode:
					ApplySetNetworkMode( session.Scene, delta );
					break;

				case SceneDeltaType.AddComponent:
					ApplyAddComponent( session.Scene, delta );
					break;

				case SceneDeltaType.UpdateComponent:
					ApplyUpdateComponent( session.Scene, delta );
					break;

				case SceneDeltaType.RemoveComponent:
					ApplyRemoveComponent( session.Scene, delta );
					break;

				case SceneDeltaType.SetComponentProperty:
					ApplySetComponentProperty( session.Scene, delta );
					break;
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[TeamSync] Failed to apply remote scene delta ({delta.DeltaType}): {ex.Message}" );
		}
		finally
		{
			IsApplyingRemoteChange = false;
		}
	}

	public static GameObject FindGameObject( Scene scene, string idStr )
	{
		if ( string.IsNullOrEmpty( idStr ) || scene == null ) return null;

		// 1. Check local ID mapping
		if ( _remoteToLocalMap.TryGetValue( idStr, out var mappedGo ) && mappedGo.IsValid() )
		{
			return mappedGo;
		}

		// 2. Check direct Guid lookup in Scene Directory
		if ( Guid.TryParse( idStr, out var guid ) )
		{
			var found = scene.Directory.FindByGuid( guid );
			if ( found != null && found.IsValid() )
			{
				_remoteToLocalMap[idStr] = found;
				return found;
			}
		}

		return null;
	}

	private static void ApplyCreateGameObject( Scene scene, SceneDeltaPayload delta )
	{
		if ( delta.Name != null && (delta.Name.Equals( "editor_camera", StringComparison.OrdinalIgnoreCase ) || delta.Name.StartsWith( "editor_", StringComparison.OrdinalIgnoreCase )) )
		{
			return; // Never replicate internal editor cameras
		}

		var existing = FindGameObject( scene, delta.TargetGameObjectId );
		if ( existing != null && existing.IsValid() ) return; // Already exists

		GameObject parent = null;
		if ( !string.IsNullOrEmpty( delta.TargetParentId ) )
		{
			parent = FindGameObject( scene, delta.TargetParentId );
		}

		var go = scene.CreateObject();
		go.Name = string.IsNullOrEmpty( delta.Name ) ? "GameObject" : delta.Name;
		go.Enabled = delta.Enabled;

		if ( parent != null )
		{
			go.SetParent( parent );
		}

		go.WorldPosition = delta.Position;
		go.WorldRotation = delta.Rotation;
		go.WorldScale = delta.Scale;

		if ( delta.IsStatic.HasValue )
		{
			go.IsStatic = delta.IsStatic.Value;
		}

		if ( delta.Tags != null )
		{
			go.Tags.RemoveAll();
			foreach ( var tag in delta.Tags )
			{
				if ( !string.IsNullOrWhiteSpace( tag ) )
					go.Tags.Add( tag );
			}
		}

		if ( delta.NetworkMode.HasValue )
		{
			go.NetworkMode = (NetworkMode)delta.NetworkMode.Value;
		}
		if ( delta.Networked.HasValue )
		{
			go.Networked = delta.Networked.Value;
		}
		if ( delta.NetworkInterpolation.HasValue )
		{
			go.NetworkInterpolation = delta.NetworkInterpolation.Value;
		}

		if ( !string.IsNullOrEmpty( delta.PrefabSource ) )
		{
			try
			{
				go.SetPrefabSource( delta.PrefabSource );
				go.UpdateFromPrefab();
			}
			catch { }
		}

		if ( !string.IsNullOrEmpty( delta.TargetGameObjectId ) )
		{
			_remoteToLocalMap[delta.TargetGameObjectId] = go;
		}

		// Register in SceneSyncSystem so local change detection does NOT echo it back
		TeamSyncManager.Instance?.SyncSystem?.RegisterRemoteObject( go );
	}

	private static void ApplyDeleteGameObject( Scene scene, SceneDeltaPayload delta )
	{
		TeamSyncManager.Instance?.SyncSystem?.UnregisterRemoteObject( delta.TargetGameObjectId );
		var go = FindGameObject( scene, delta.TargetGameObjectId );
		if ( go != null && go.IsValid() )
		{
			TeamSyncManager.Instance?.SyncSystem?.UnregisterRemoteObject( go.Id.ToString() );
			if ( !string.IsNullOrEmpty( delta.TargetGameObjectId ) )
			{
				_remoteToLocalMap.Remove( delta.TargetGameObjectId );
			}
			go.Destroy();
		}
	}

	private static void ApplySetTransform( Scene scene, SceneDeltaPayload delta )
	{
		var go = FindGameObject( scene, delta.TargetGameObjectId );
		if ( go == null || !go.IsValid() ) return;

		go.WorldPosition = delta.Position;
		go.WorldRotation = delta.Rotation;
		go.WorldScale = delta.Scale;

		TeamSyncManager.Instance?.SyncSystem?.RegisterRemoteObject( go );
	}

	private static void ApplySetParent( Scene scene, SceneDeltaPayload delta )
	{
		var go = FindGameObject( scene, delta.TargetGameObjectId );
		if ( go == null || !go.IsValid() ) return;

		if ( string.IsNullOrEmpty( delta.TargetParentId ) )
		{
			go.SetParent( null );
		}
		else
		{
			var parent = FindGameObject( scene, delta.TargetParentId );
			if ( parent != null && parent.IsValid() )
			{
				go.SetParent( parent );
			}
		}

		TeamSyncManager.Instance?.SyncSystem?.RegisterRemoteObject( go );
	}

	private static void ApplySetEnabled( Scene scene, SceneDeltaPayload delta )
	{
		var go = FindGameObject( scene, delta.TargetGameObjectId );
		if ( go == null || !go.IsValid() ) return;

		go.Enabled = delta.Enabled;

		TeamSyncManager.Instance?.SyncSystem?.RegisterRemoteObject( go );
	}

	private static void ApplySetName( Scene scene, SceneDeltaPayload delta )
	{
		var go = FindGameObject( scene, delta.TargetGameObjectId );
		if ( go == null || !go.IsValid() ) return;

		go.Name = delta.Name;

		TeamSyncManager.Instance?.SyncSystem?.RegisterRemoteObject( go );
	}

	private static void ApplySetStatic( Scene scene, SceneDeltaPayload delta )
	{
		var go = FindGameObject( scene, delta.TargetGameObjectId );
		if ( go == null || !go.IsValid() || !delta.IsStatic.HasValue ) return;

		go.IsStatic = delta.IsStatic.Value;
		TeamSyncManager.Instance?.SyncSystem?.RegisterRemoteObject( go );
	}

	private static void ApplySetTags( Scene scene, SceneDeltaPayload delta )
	{
		var go = FindGameObject( scene, delta.TargetGameObjectId );
		if ( go == null || !go.IsValid() ) return;

		go.Tags.RemoveAll();
		if ( delta.Tags != null )
		{
			foreach ( var tag in delta.Tags )
			{
				if ( !string.IsNullOrWhiteSpace( tag ) )
					go.Tags.Add( tag );
			}
		}
		TeamSyncManager.Instance?.SyncSystem?.RegisterRemoteObject( go );
	}

	private static void ApplySetNetworkMode( Scene scene, SceneDeltaPayload delta )
	{
		var go = FindGameObject( scene, delta.TargetGameObjectId );
		if ( go == null || !go.IsValid() ) return;

		if ( delta.NetworkMode.HasValue )
		{
			go.NetworkMode = (NetworkMode)delta.NetworkMode.Value;
		}
		if ( delta.Networked.HasValue )
		{
			go.Networked = delta.Networked.Value;
		}
		if ( delta.NetworkInterpolation.HasValue )
		{
			go.NetworkInterpolation = delta.NetworkInterpolation.Value;
		}
		TeamSyncManager.Instance?.SyncSystem?.RegisterRemoteObject( go );
	}

	private static void ApplyAddComponent( Scene scene, SceneDeltaPayload delta )
	{
		var go = FindGameObject( scene, delta.TargetGameObjectId );
		if ( go == null || !go.IsValid() || string.IsNullOrEmpty( delta.ComponentType ) ) return;

		Guid compGuid = Guid.Empty;
		if ( !string.IsNullOrEmpty( delta.ComponentId ) )
		{
			Guid.TryParse( delta.ComponentId, out compGuid );
		}

		// If already exists with this Id, update it
		if ( compGuid != Guid.Empty )
		{
			var existing = go.Components.GetAll().FirstOrDefault( c => c.Id == compGuid );
			if ( existing != null && existing.IsValid() )
			{
				ApplyUpdateComponent( scene, delta );
				return;
			}
		}

		var typeDesc = TypeLibrary.GetType( delta.ComponentType );
		if ( typeDesc != null && typeof( Component ).IsAssignableFrom( typeDesc.TargetType ) )
		{
			var comp = go.Components.Create( typeDesc );
			if ( comp != null && comp.IsValid() )
			{
				if ( !string.IsNullOrEmpty( delta.ComponentJson ) )
				{
					try
					{
						var node = System.Text.Json.Nodes.JsonNode.Parse( delta.ComponentJson ) as System.Text.Json.Nodes.JsonObject;
						if ( node != null )
						{
							comp.Deserialize( node );
						}
					}
					catch { }
				}

				TeamSyncManager.Instance?.SyncSystem?.RegisterRemoteComponent( delta.TargetGameObjectId, comp.Id, delta.ComponentJson );
			}
		}
	}

	private static void ApplyUpdateComponent( Scene scene, SceneDeltaPayload delta )
	{
		var go = FindGameObject( scene, delta.TargetGameObjectId );
		if ( go == null || !go.IsValid() ) return;

		Component comp = null;
		if ( Guid.TryParse( delta.ComponentId, out var compGuid ) )
		{
			comp = go.Components.GetAll().FirstOrDefault( c => c.Id == compGuid );
		}
		else if ( !string.IsNullOrEmpty( delta.ComponentType ) )
		{
			comp = go.Components.GetAll().FirstOrDefault( c => c.GetType().FullName.Equals( delta.ComponentType, StringComparison.OrdinalIgnoreCase ) ||
			                                                   c.GetType().Name.Equals( delta.ComponentType, StringComparison.OrdinalIgnoreCase ) );
		}

		if ( comp == null || !comp.IsValid() )
		{
			ApplyAddComponent( scene, delta );
			return;
		}

		if ( !string.IsNullOrEmpty( delta.ComponentJson ) )
		{
			try
			{
				var node = System.Text.Json.Nodes.JsonNode.Parse( delta.ComponentJson ) as System.Text.Json.Nodes.JsonObject;
				if ( node != null )
				{
					comp.Deserialize( node );
					TeamSyncManager.Instance?.SyncSystem?.RegisterRemoteComponent( delta.TargetGameObjectId, comp.Id, delta.ComponentJson );
				}
			}
			catch { }
		}
	}

	private static void ApplyRemoveComponent( Scene scene, SceneDeltaPayload delta )
	{
		var go = FindGameObject( scene, delta.TargetGameObjectId );
		if ( go == null || !go.IsValid() ) return;

		if ( Guid.TryParse( delta.ComponentId, out var compGuid ) )
		{
			var comp = go.Components.GetAll().FirstOrDefault( c => c.Id == compGuid );
			if ( comp != null && comp.IsValid() )
			{
				TeamSyncManager.Instance?.SyncSystem?.UnregisterRemoteComponent( delta.TargetGameObjectId, compGuid );
				comp.Destroy();
			}
		}
		else if ( !string.IsNullOrEmpty( delta.ComponentType ) )
		{
			var comp = go.Components.GetAll().FirstOrDefault( c => c.GetType().FullName.Equals( delta.ComponentType, StringComparison.OrdinalIgnoreCase ) ||
			                                                   c.GetType().Name.Equals( delta.ComponentType, StringComparison.OrdinalIgnoreCase ) );
			if ( comp != null && comp.IsValid() )
			{
				TeamSyncManager.Instance?.SyncSystem?.UnregisterRemoteComponent( delta.TargetGameObjectId, comp.Id );
				comp.Destroy();
			}
		}
	}

	private static void ApplySetComponentProperty( Scene scene, SceneDeltaPayload delta )
	{
		var go = FindGameObject( scene, delta.TargetGameObjectId );
		if ( go == null || !go.IsValid() ) return;

		Component comp = null;
		if ( Guid.TryParse( delta.ComponentId, out var compGuid ) )
		{
			comp = go.Components.GetAll().FirstOrDefault( c => c.Id == compGuid );
		}
		else if ( !string.IsNullOrEmpty( delta.ComponentType ) )
		{
			comp = go.Components.GetAll().FirstOrDefault( c => c.GetType().Name.Equals( delta.ComponentType, StringComparison.OrdinalIgnoreCase ) );
		}

		if ( comp == null || !comp.IsValid() || string.IsNullOrEmpty( delta.PropertyName ) ) return;

		try
		{
			var prop = comp.GetType().GetProperty( delta.PropertyName );
			if ( prop != null && prop.CanWrite && !string.IsNullOrEmpty( delta.PropertyValueJson ) )
			{
				var value = JsonSerializer.Deserialize( delta.PropertyValueJson, prop.PropertyType );
				prop.SetValue( comp, value );
			}
		}
		catch { }
	}

	/// <summary>
	/// Generates a full scene snapshot JSON string.
	/// </summary>
	public static string CreateSceneSnapshot()
	{
		var session = SceneEditorSession.Active;
		if ( session == null || session.Scene == null ) return null;

		try
		{
			var jobj = session.Scene.Serialize();
			if ( jobj != null && jobj.TryGetPropertyValue( "Objects", out var objectsNode ) && objectsNode is System.Text.Json.Nodes.JsonArray arr )
			{
				for ( int i = arr.Count - 1; i >= 0; i-- )
				{
					if ( arr[i] is System.Text.Json.Nodes.JsonObject obj &&
					     obj.TryGetPropertyValue( "Name", out var nameVal ) &&
					     nameVal != null )
					{
						string n = nameVal.ToString();
						if ( n.Equals( "editor_camera", StringComparison.OrdinalIgnoreCase ) || n.StartsWith( "editor_", StringComparison.OrdinalIgnoreCase ) )
						{
							arr.RemoveAt( i );
						}
					}
				}
			}
			return jobj?.ToJsonString();
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Restores a full scene snapshot for late-joining clients.
	/// </summary>
	public static void RestoreSceneSnapshot( string sceneJson )
	{
		if ( string.IsNullOrWhiteSpace( sceneJson ) ) return;

		var session = SceneEditorSession.Active;
		if ( session == null || session.Scene == null ) return;

		IsApplyingRemoteChange = true;
		try
		{
			ResetMapping();
			var node = System.Text.Json.Nodes.JsonNode.Parse( sceneJson );
			if ( node is System.Text.Json.Nodes.JsonObject jobj )
			{
				session.Scene.Deserialize( jobj );
			}
			TeamSyncManager.Instance?.SyncSystem?.RebuildBaseline();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[TeamSync] Failed to restore scene snapshot: {ex.Message}" );
		}
		finally
		{
			IsApplyingRemoteChange = false;
		}
	}
}
