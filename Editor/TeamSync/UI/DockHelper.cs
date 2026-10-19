using System;
using System.Collections;
using System.Reflection;
using Editor;

namespace Editor.TeamSync;

/// <summary>
/// Utility for opening and raising dockable editor panels within s&amp;box's native DockManager.
/// </summary>
public static class DockHelper
{
	public static bool OpenDock( string dockTitle )
	{
		try
		{
			var targetsField = typeof( DockAttribute ).GetField( "Targets", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static );
			var targets = targetsField?.GetValue( null ) as IDictionary;
			if ( targets != null && targets.Contains( "Editor" ) )
			{
				var editorDockWindow = targets["Editor"] as DockWindow;
				if ( editorDockWindow?.DockManager != null )
				{
					editorDockWindow.DockManager.SetDockState( dockTitle, true );
					editorDockWindow.DockManager.RaiseDock( dockTitle );
					return true;
				}
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[TeamSync] Failed to open dock '{dockTitle}': {ex.Message}" );
		}

		return false;
	}
}
