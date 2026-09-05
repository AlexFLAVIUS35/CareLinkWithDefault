Imports System.Threading

Public Class OAuthBrowserForm

    Private ReadOnly _password As String
    Private ReadOnly _redirectUri As String
    Private ReadOnly _startUrl As String
    Private ReadOnly _userName As String
    Private ReadOnly _cancellation As CancellationTokenSource

    Public Sub New(startUrl As String,
                   redirectUri As String,
                   userName As String,
                   password As String)
        InitializeComponent()

        _startUrl = startUrl
        _redirectUri = redirectUri
        _userName = userName
        _password = password
        _cancellation = New CancellationTokenSource()

        Text = "CareLink Login"
        Width = Math.Min(700, Screen.PrimaryScreen.WorkingArea.Width - 100)
        Height = Math.Min(220, Screen.PrimaryScreen.WorkingArea.Height - 100)
        StartPosition = FormStartPosition.CenterScreen
    End Sub

    Public Property Result As RedirectResult

    Private Async Sub OAuthBrowserForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Try
            ' The login page is opened by Windows using the user's default browser.
            ' DefaultBrowserOAuth listens for the registered loopback OAuth callback.
            Result = Await DefaultBrowserOAuth.CaptureRedirectAsync(_startUrl,
                                                                    _redirectUri,
                                                                    _cancellation.Token)
            DialogResult = DialogResult.OK
            Close()
        Catch ex As OperationCanceledException
            DialogResult = DialogResult.Cancel
            Close()
        Catch ex As Exception
            If Not IsDisposed Then
                MessageBox.Show(Me,
                                ex.Message,
                                "CareLink Login",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error)
                DialogResult = DialogResult.Cancel
                Close()
            End If
        End Try
    End Sub

    Protected Overrides Sub OnFormClosed(e As FormClosedEventArgs)
        _cancellation.Cancel()
        _cancellation.Dispose()
        MyBase.OnFormClosed(e)
    End Sub

End Class
