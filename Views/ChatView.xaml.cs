using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Apathy.Services;

namespace Apathy.Views;

/// <summary>The ChatLive tab: conversation, who's online, server address and input.</summary>
public partial class ChatView : UserControl
{
    // Follow new messages only while the user is at the bottom (not reading history).
    private bool _stickToBottom = true;

    public ChatView()
    {
        InitializeComponent();
        var chat = ChatLiveClient.Instance;
        MessageList.ItemsSource = chat.Messages;
        UserList.ItemsSource = chat.Users;

        // The tab's content is unloaded when another tab is selected, so only listen while on screen.
        Loaded += (_, _) =>
        {
            chat.Messages.CollectionChanged += Messages_CollectionChanged;
            chat.Users.CollectionChanged += Users_CollectionChanged;
            ConnectionService.Instance.PropertyChanged += Connection_PropertyChanged;
            ServerBox.Text = SettingsStore.Current.ChatServerUrl;
            UpdateOnline();
            _stickToBottom = true;
            MessageScroll.ScrollToBottom();
            Input.Focus();
        };
        Unloaded += (_, _) =>
        {
            chat.Messages.CollectionChanged -= Messages_CollectionChanged;
            chat.Users.CollectionChanged -= Users_CollectionChanged;
            ConnectionService.Instance.PropertyChanged -= Connection_PropertyChanged;
        };
    }

    private void UpdateOnline()
    {
        int count = ChatLiveClient.Instance.Users.Count;
        OnlineHeader.Text = $"ONLINE  {count}";
        var status = ConnectionService.Instance;
        Subtitle.Text = status.State == ConnectionState.Connected
            ? $"{count} online"
            : status.Detail.Replace("ChatLive: ", "");
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) _stickToBottom = true;
        if (e.NewItems?.OfType<ChatMessage>().Any(m => m.IsOwn) == true) _stickToBottom = true;
        if (!_stickToBottom && e.Action == NotifyCollectionChangedAction.Add) NewMessagesButton.Visibility = Visibility.Visible;
    }

    private void MessageScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Content grew (new message, re-wrap after a resize): keep the newest line in view if following.
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            if (_stickToBottom) MessageScroll.ScrollToBottom();
            return;
        }
        // The user scrolled.
        _stickToBottom = MessageScroll.VerticalOffset >= MessageScroll.ScrollableHeight - 30;
        if (_stickToBottom) NewMessagesButton.Visibility = Visibility.Collapsed;
    }

    private void Users_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateOnline();

    private void Connection_PropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateOnline();

    private async void Send()
    {
        string text = Input.Text.Trim();
        if (text.Length == 0) return;
        if (await ChatLiveClient.Instance.SendAsync(text)) Input.Clear();
        else NotificationService.Show("ChatLive", "Not connected, the message wasn't sent.", NotificationKind.Error);
    }

    private void Reconnect()
    {
        SettingsStore.Current.ChatServerUrl = ServerBox.Text.Trim();
        ChatLiveClient.Instance.Start(SettingsStore.Current.ChatServerUrl, Session.Username);
        SettingsBar.Visibility = Visibility.Collapsed;
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Send();
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void NewMessages_Click(object sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        NewMessagesButton.Visibility = Visibility.Collapsed;
        MessageScroll.ScrollToBottom();
    }

    private void Mention_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not ChatMessage message) return;
        Input.Text = $"@{message.Name} {Input.Text}";
        Input.CaretIndex = Input.Text.Length;
        Input.Focus();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not ChatMessage message) return;
        try
        {
            Clipboard.SetText(message.Text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard busy (another app holds it); the user can try again.
        }
    }

    private void ServerBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Reconnect();
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => Reconnect();

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        SettingsBar.Visibility = SettingsBar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
}
