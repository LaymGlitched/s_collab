namespace Editor.TeamSync;

using System.Security.Cryptography;

/// <summary>
/// Monitors local project asset and source code changes and synchronizes them
/// in real time across collaborators over the TeamSync transport.
/// Prevents the need for manual Git commits/pushes during collaborative authoring.
/// </summary>
public sealed class ProjectFileSyncService
{
	private static ProjectFileSyncService _instance;
	public static ProjectFileSyncService Instance => _instance ??= new ProjectFileSyncService();

	private FileSystemWatcher _watcher;
	private bool _isRunning;
	private readonly Dictionary<string, string> _fileHashes = new( StringComparer.OrdinalIgnoreCase );
	private readonly ConcurrentDictionary<string, float> _suppressedPaths = new( StringComparer.OrdinalIgnoreCase );
	private readonly ConcurrentDictionary<string, float> _pendingChanges = new( StringComparer.OrdinalIgnoreCase );

	private static readonly string[] AllowedFolders = { "assets", "code", "projectsettings" };
	private const long MaxFileSizeBytes = 50 * 1024 * 1024; // 50 MB safety cap

	private ProjectFileSyncService()
	{
	}

	public void Start()
	{
		if ( _isRunning ) return;

		string root = GetProjectRoot();
		if ( string.IsNullOrEmpty( root ) || !Directory.Exists( root ) )
		{
			Log.Warning( "[TeamSync] Cannot start FileSyncService: project root directory not found." );
			return;
		}

		try
		{
			_watcher = new FileSystemWatcher( root )
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName
			};

			_watcher.Changed += OnFileSystemEvent;
			_watcher.Created += OnFileSystemEvent;
			_watcher.Deleted += OnFileSystemEvent;
			_watcher.Renamed += OnRenamedEvent;
			_watcher.EnableRaisingEvents = true;

			_isRunning = true;
			Log.Info( $"[TeamSync] 📁 Project FileSyncService active for: {root}" );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[TeamSync] Failed to initialize FileSystemWatcher: {ex.Message}" );
		}
	}

	public void Stop()
	{
		_isRunning = false;
		if ( _watcher != null )
		{
			try
			{
				_watcher.EnableRaisingEvents = false;
				_watcher.Dispose();
			}
			catch { }
			_watcher = null;
		}

		_fileHashes.Clear();
		_suppressedPaths.Clear();
		_pendingChanges.Clear();
	}

	public static string GetProjectRoot()
	{
		try
		{
			return Project.Current?.RootDirectory?.FullName;
		}
		catch
		{
			return null;
		}
	}

	private bool ShouldSyncPath( string fullPath, out string relativePath )
	{
		relativePath = null;
		string root = GetProjectRoot();
		if ( string.IsNullOrEmpty( root ) ) return false;

		try
		{
			fullPath = Path.GetFullPath( fullPath );
			if ( !fullPath.StartsWith( root, StringComparison.OrdinalIgnoreCase ) )
				return false;

			relativePath = Path.GetRelativePath( root, fullPath ).Replace( '\\', '/' );

			// Filter out internal/build/temp directories
			string lower = relativePath.ToLowerInvariant();
			if ( lower.StartsWith( ".git" ) ||
			     lower.StartsWith( ".vs" ) ||
			     lower.StartsWith( ".editor" ) ||
			     lower.StartsWith( "bin" ) ||
			     lower.StartsWith( "obj" ) ||
			     lower.StartsWith( ".source" ) ||
			     lower.EndsWith( ".scene" ) ||
			     lower.EndsWith( ".scene_c" ) ||
			     lower.EndsWith( ".tmp" ) ||
			     lower.EndsWith( ".cache" ) ||
			     lower.EndsWith( ".pdb" ) ||
			     lower.EndsWith( ".dll" ) )
			{
				return false;
			}

			// Ensure it is in an allowed folder (Assets, code, ProjectSettings)
			bool inAllowed = false;
			foreach ( var folder in AllowedFolders )
			{
				if ( lower.StartsWith( folder + "/" ) || lower.Equals( folder ) )
				{
					inAllowed = true;
					break;
				}
			}

			return inAllowed;
		}
		catch
		{
			return false;
		}
	}

	private void OnFileSystemEvent( object sender, FileSystemEventArgs e )
	{
		if ( !_isRunning || Game.IsPlaying ) return;

		if ( ShouldSyncPath( e.FullPath, out var relPath ) )
		{
			// Debounce 350ms to allow multi-write file flush to complete
			_pendingChanges[relPath] = RealTime.Now + 0.35f;
		}
	}

	private void OnRenamedEvent( object sender, RenamedEventArgs e )
	{
		if ( !_isRunning || Game.IsPlaying ) return;

		if ( ShouldSyncPath( e.OldFullPath, out var oldRel ) )
		{
			BroadcastFileDeletion( oldRel );
		}

		if ( ShouldSyncPath( e.FullPath, out var newRel ) )
		{
			_pendingChanges[newRel] = RealTime.Now + 0.35f;
		}
	}

	[EditorEvent.Frame]
	public static void FrameUpdate()
	{
		if ( _instance != null && _instance._isRunning )
		{
			_instance.ProcessPendingChanges();
		}
	}

	private void ProcessPendingChanges()
	{
		var manager = TeamSyncManager.Instance;
		if ( manager == null || !manager.IsSessionActive || Game.IsPlaying ) return;

		float now = RealTime.Now;

		// Clean expired suppressions
		foreach ( var kvp in _suppressedPaths )
		{
			if ( now > kvp.Value )
			{
				_suppressedPaths.TryRemove( kvp.Key, out _ );
			}
		}

		// Drain ready pending changes
		var readyKeys = _pendingChanges.Where( kvp => now >= kvp.Value ).Select( kvp => kvp.Key ).ToList();
		string root = GetProjectRoot();
		if ( string.IsNullOrEmpty( root ) ) return;

		foreach ( var relPath in readyKeys )
		{
			_pendingChanges.TryRemove( relPath, out _ );

			// If suppressed (because we just wrote it from a remote peer), don't echo back
			if ( _suppressedPaths.ContainsKey( relPath ) ) continue;

			string fullPath = Path.Combine( root, relPath );
			if ( !File.Exists( fullPath ) )
			{
				// File was deleted
				BroadcastFileDeletion( relPath );
				continue;
			}

			try
			{
				var fi = new FileInfo( fullPath );
				if ( fi.Length > MaxFileSizeBytes )
				{
					Log.Warning( $"[TeamSync] Skipping FileSync for '{relPath}': size exceeds 50MB safety limit." );
					continue;
				}

				byte[] data = File.ReadAllBytes( fullPath );
				string hash = ComputeHash( data );

				if ( _fileHashes.TryGetValue( relPath, out var prevHash ) && prevHash == hash )
				{
					// Content identical, skip
					continue;
				}

				_fileHashes[relPath] = hash;

				var payload = new FileChunkPayload
				{
					RelativePath = relPath,
					Base64Data = Convert.ToBase64String( data ),
					FileHash = hash,
					IsDeleted = false
				};

				_ = manager.Transport.BroadcastAsync( TeamSyncEnvelope.Create( TeamSyncMessageType.FileSync, manager.LocalPeerId, payload ) );
				Log.Info( $"[TeamSync] 📤 Broadcast file update: {relPath} ({data.Length} bytes)" );
			}
			catch ( IOException )
			{
				// File may still be locked by editor writer; retry slightly later
				_pendingChanges[relPath] = now + 0.5f;
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[TeamSync] File sync error for '{relPath}': {ex.Message}" );
			}
		}
	}

	private void BroadcastFileDeletion( string relPath )
	{
		var manager = TeamSyncManager.Instance;
		if ( manager == null || !manager.IsSessionActive ) return;

		_fileHashes.Remove( relPath );

		var payload = new FileChunkPayload
		{
			RelativePath = relPath,
			IsDeleted = true
		};

		_ = manager.Transport.BroadcastAsync( TeamSyncEnvelope.Create( TeamSyncMessageType.FileSync, manager.LocalPeerId, payload ) );
		Log.Info( $"[TeamSync] 🗑️ Broadcast file deletion: {relPath}" );
	}

	public void ApplyRemoteFile( FileChunkPayload payload )
	{
		if ( payload == null || string.IsNullOrWhiteSpace( payload.RelativePath ) ) return;

		string root = GetProjectRoot();
		if ( string.IsNullOrEmpty( root ) ) return;

		try
		{
			string fullPath = Path.GetFullPath( Path.Combine( root, payload.RelativePath ) );
			if ( !fullPath.StartsWith( root, StringComparison.OrdinalIgnoreCase ) )
			{
				Log.Warning( $"[TeamSync] Rejected unsafe file sync path: {payload.RelativePath}" );
				return;
			}

			// Suppress local watcher echo for 2 seconds
			_suppressedPaths[payload.RelativePath] = RealTime.Now + 2.0f;

			if ( payload.IsDeleted )
			{
				if ( File.Exists( fullPath ) )
				{
					File.Delete( fullPath );
					_fileHashes.Remove( payload.RelativePath );
					Log.Info( $"[TeamSync] 🗑️ Remote deleted file: {payload.RelativePath}" );
				}
				return;
			}

			byte[] data = Convert.FromBase64String( payload.Base64Data );
			string incomingHash = string.IsNullOrEmpty( payload.FileHash ) ? ComputeHash( data ) : payload.FileHash;

			if ( File.Exists( fullPath ) )
			{
				byte[] localBytes = File.ReadAllBytes( fullPath );
				if ( ComputeHash( localBytes ) == incomingHash )
				{
					// Already identical
					_fileHashes[payload.RelativePath] = incomingHash;
					return;
				}
			}

			string dir = Path.GetDirectoryName( fullPath );
			if ( !string.IsNullOrEmpty( dir ) && !Directory.Exists( dir ) )
			{
				Directory.CreateDirectory( dir );
			}

			File.WriteAllBytes( fullPath, data );
			_fileHashes[payload.RelativePath] = incomingHash;

			Log.Info( $"[TeamSync] 📥 Remote synchronized file: {payload.RelativePath} ({data.Length} bytes)" );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[TeamSync] Failed to apply remote file sync for '{payload.RelativePath}': {ex.Message}" );
		}
	}

	[ConCmd( "teamsync_sync_all_files" )]
	public static void SyncAllProjectFilesCmd()
	{
		var manager = TeamSyncManager.Instance;
		if ( manager == null || !manager.IsSessionActive )
		{
			Log.Info( "[TeamSync] No active collaboration session to sync files." );
			return;
		}

		string root = GetProjectRoot();
		if ( string.IsNullOrEmpty( root ) || !Directory.Exists( root ) ) return;

		int count = 0;
		foreach ( var folder in AllowedFolders )
		{
			string dir = Path.Combine( root, folder );
			if ( !Directory.Exists( dir ) ) continue;

			foreach ( var file in Directory.GetFiles( dir, "*.*", SearchOption.AllDirectories ) )
			{
				if ( Instance.ShouldSyncPath( file, out var relPath ) )
				{
					Instance._pendingChanges[relPath] = RealTime.Now;
					count++;
				}
			}
		}

		Log.Info( $"[TeamSync] Queued {count} project files for collaborative synchronization." );
	}

	private static string ComputeHash( byte[] data )
	{
		using var md5 = MD5.Create();
		byte[] hash = md5.ComputeHash( data );
		return Convert.ToHexString( hash );
	}
}
