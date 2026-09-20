namespace Editor.TeamSync;

/// <summary>
/// Small dockable Editor window providing real-time in-editor chat and session activity logging.
/// </summary>
[Dock( "Editor", "Chat & Activity", "chat" )]
public sealed class TeamSyncChatDock : Widget
{
	private ScrollArea _scrollArea;
	private Widget _feedContainer;
	private Layout _feedLayout;
	private LineEdit _chatInput;
	private Label _onlineBadge;

	public TeamSyncChatDock() : this( null )
	{
	}

	public TeamSyncChatDock( Widget parent ) : base( parent )
	{
		Layout = Layout.Column();
		Layout.Margin = 6;
		Layout.Spacing = 6;

		BuildUi();

		ActivityChatSystem.Instance.OnEntryAdded += HandleEntryAdded;
		ActivityChatSystem.Instance.OnEntriesCleared += HandleEntriesCleared;

		var manager = TeamSyncManager.Instance;
		if ( manager != null )
		{
			manager.OnSessionStateChanged += HandleSessionStateChanged;
			manager.OnCollaboratorJoined += HandleCollaboratorJoined;
			manager.OnCollaboratorLeft += HandleCollaboratorLeft;
		}
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();
		ActivityChatSystem.Instance.OnEntryAdded -= HandleEntryAdded;
		ActivityChatSystem.Instance.OnEntriesCleared -= HandleEntriesCleared;

		var manager = TeamSyncManager.Instance;
		if ( manager != null )
		{
			manager.OnSessionStateChanged -= HandleSessionStateChanged;
			manager.OnCollaboratorJoined -= HandleCollaboratorJoined;
			manager.OnCollaboratorLeft -= HandleCollaboratorLeft;
		}
	}

	private void HandleSessionStateChanged() => UpdateOnlineBadge();
	private void HandleCollaboratorJoined( CollaboratorState _ ) => UpdateOnlineBadge();
	private void HandleCollaboratorLeft( string _ ) => UpdateOnlineBadge();

	private void BuildUi()
	{
		Layout.Clear( true );

		// 1. Header Bar
		var header = Layout.AddRow();
		header.Spacing = 6;
		header.Alignment = TextFlag.LeftCenter;

		var icon = new IconButton( "chat" );
		header.Add( icon );

		var title = new Label( "Chat & Activity" );
		title.Color = Theme.Text;
		header.Add( title, 1 );

		var manager = TeamSyncManager.Instance;
		int onlineCount = manager != null && manager.IsSessionActive ? manager.Collaborators.Count : 1;
		_onlineBadge = new Label( $"● {onlineCount} online" );
		_onlineBadge.Color = manager != null && manager.IsSessionActive ? Theme.Green : Theme.Text.WithAlpha( 0.4f );
		header.Add( _onlineBadge );

		var clearBtn = new Button( null, "delete_sweep" );
		clearBtn.ToolTip = "Clear local activity history";
		clearBtn.Clicked = () => ActivityChatSystem.Instance.Clear();
		header.Add( clearBtn );

		Layout.AddSeparator();

		// 2. Scrollable Activity & Chat Feed
		_scrollArea = Layout.Add( new ScrollArea( this ), 1 );
		_feedContainer = new Widget( _scrollArea );
		_feedLayout = _feedContainer.Layout = Layout.Column();
		_feedLayout.Spacing = 3;
		_feedLayout.Margin = 4;
		_scrollArea.Canvas = _feedContainer;

		// Populate existing history
		foreach ( var entry in ActivityChatSystem.Instance.Entries )
		{
			AddEntryToUi( entry );
		}

		Layout.AddSeparator();

		// 3. Chat Input Bar
		var inputBar = Layout.AddRow();
		inputBar.Spacing = 6;
		inputBar.Alignment = TextFlag.LeftCenter;

		_chatInput = new LineEdit( "" );
		_chatInput.PlaceholderText = "Type a message... (Enter to send)";
		_chatInput.ReturnPressed += SendMessage;
		inputBar.Add( _chatInput, 1 );

		var sendBtn = new Button( null, "send" );
		sendBtn.ToolTip = "Send Message (Enter)";
		sendBtn.Clicked = SendMessage;
		inputBar.Add( sendBtn );

		ScrollToBottom();
	}

	private void HandleEntryAdded( ChatActivityEntry entry )
	{
		if ( !this.IsValid() ) return;
		AddEntryToUi( entry );
		ScrollToBottom();
		UpdateOnlineBadge();
	}

	private void HandleEntriesCleared()
	{
		if ( !this.IsValid() ) return;
		_feedLayout.Clear( true );
	}

	private void AddEntryToUi( ChatActivityEntry entry )
	{
		var row = _feedLayout.AddRow();
		row.Spacing = 4;
		row.Alignment = TextFlag.LeftTop;

		// 1. Timestamp: "03:15: "
		var timeLabel = new Label( $"{entry.Timestamp:HH:mm}: " );
		timeLabel.Color = Theme.Text.WithAlpha( 0.4f );
		row.Add( timeLabel );

		// 2. Sender Name & Colon
		Color senderColor;
		try
		{
			senderColor = Color.Parse( entry.SenderColor ) ?? Theme.Blue;
		}
		catch
		{
			senderColor = Theme.Blue;
		}

		if ( entry.IsChat )
		{
			// Format: "03:21: Yyk: This good?"
			var nameLabel = new Label( $"{entry.SenderName}: " );
			nameLabel.Color = senderColor;
			row.Add( nameLabel );

			var textLabel = new Label( entry.Text );
			textLabel.Color = Theme.Text;
			row.Add( textLabel, 1 );
		}
		else
		{
			// Format: "03:15: Laym created 'Light_Ceiling'"
			var nameLabel = new Label( $"{entry.SenderName} " );
			nameLabel.Color = senderColor;
			row.Add( nameLabel );

			var textLabel = new Label( entry.Text );
			textLabel.Color = Theme.Text.WithAlpha( 0.75f );
			row.Add( textLabel, 1 );
		}
	}

	private void SendMessage()
	{
		if ( _chatInput == null ) return;
		string text = _chatInput.Text?.Trim();
		if ( string.IsNullOrEmpty( text ) ) return;

		ActivityChatSystem.Instance.SendChat( text );
		_chatInput.Text = string.Empty;
		_chatInput.Focus();
		ScrollToBottom();
	}

	private void ScrollToBottom()
	{
		if ( _scrollArea == null ) return;
		try
		{
			var method = typeof( ScrollArea ).GetMethod( "ScrollToBottom" );
			if ( method != null )
			{
				method.Invoke( _scrollArea, null );
				return;
			}
			var prop = typeof( ScrollArea ).GetProperty( "ScrollOffset" ) ?? typeof( ScrollArea ).GetProperty( "VerticalScroll" );
			if ( prop != null && prop.CanWrite )
			{
				if ( prop.PropertyType == typeof( Vector2 ) )
					prop.SetValue( _scrollArea, new Vector2( 0, 999999f ) );
				else if ( prop.PropertyType == typeof( float ) )
					prop.SetValue( _scrollArea, 999999f );
			}
		}
		catch { }
	}

	private void UpdateOnlineBadge()
	{
		if ( !this.IsValid() ) return;

		var manager = TeamSyncManager.Instance;
		if ( _onlineBadge != null && manager != null )
		{
			int count = manager.IsSessionActive ? manager.Collaborators.Count : 1;
			_onlineBadge.Text = $"● {count} online";
			_onlineBadge.Color = manager.IsSessionActive ? Theme.Green : Theme.Text.WithAlpha( 0.4f );
		}
	}

	[ConCmd( "teamsync_chat" )]
	public static void OpenWindow()
	{
		if ( DockHelper.OpenDock( "Chat & Activity" ) )
			return;

		var win = new TeamSyncChatDock();
		win.WindowTitle = "Chat & Activity";
		win.SetWindowIcon( "chat" );
		win.Show();
		win.Focus();
	}
}
