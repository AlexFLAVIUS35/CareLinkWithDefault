Imports System.Diagnostics
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Win32

Public Module DefaultBrowserOAuth
    Private Const ProtocolScheme As String = "com.medtronic.carepartner"
    Private Const CallbackScheme As String = ProtocolScheme & ":/sso"
    Private ReadOnly CallbackLock As New Object()
    Private ActiveCallback As TaskCompletionSource(Of Uri)
    Private ActiveState As String

    Public Async Function CaptureRedirectAsync(startUrl As String,
                                               redirectUri As Uri,
                                               cancellationToken As CancellationToken) As Task(Of Uri)
        If String.Equals(redirectUri.Scheme, ProtocolScheme, StringComparison.OrdinalIgnoreCase) Then
            RegisterWindowsProtocolHandler()
            Return Await CaptureProtocolRedirectAsync(startUrl, redirectUri, cancellationToken)
        End If

        If Not String.Equals(redirectUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) OrElse
           Not IPAddress.TryParse(redirectUri.Host, Nothing) OrElse
           Not IPAddress.IsLoopback(IPAddress.Parse(redirectUri.Host)) Then
            Throw New InvalidOperationException("The CareLink OAuth redirect URI must be a loopback HTTP URI or the CareLink callback URI.")
        End If

        Return Await CaptureLoopbackRedirectAsync(startUrl, redirectUri, cancellationToken)
    End Function

    Private Sub RegisterWindowsProtocolHandler()
        Dim executablePath As String = Environment.ProcessPath
        If String.IsNullOrWhiteSpace(executablePath) Then
            Throw New InvalidOperationException("CareLink could not determine its executable path for OAuth callback registration.")
        End If

        Using schemeKey As RegistryKey = Registry.CurrentUser.CreateSubKey($"Software\Classes\{ProtocolScheme}")
            schemeKey.SetValue("", "URL:CareLink OAuth Callback", RegistryValueKind.String)
            schemeKey.SetValue("URL Protocol", "", RegistryValueKind.String)

            Using commandKey As RegistryKey = schemeKey.CreateSubKey("shell\open\command")
                commandKey.SetValue("", String.Format("""{0}" "%1"", executablePath), RegistryValueKind.String)
            End Using
        End Using
    End Sub

    Private Async Function CaptureProtocolRedirectAsync(startUrl As String,
                                                        redirect As Uri,
                                                        cancellationToken As CancellationToken) As Task(Of Uri)
        Dim callbackTcs As TaskCompletionSource(Of Uri) = Nothing
        SyncLock CallbackLock
            callbackTcs = New TaskCompletionSource(Of Uri)(TaskCreationOptions.RunContinuationsAsynchronously)
            ActiveCallback = callbackTcs
            ActiveState = GetStateFromUrl(startUrl)
        End SyncLock

        Try
            Process.Start(New ProcessStartInfo With {
                .FileName = startUrl,
                .UseShellExecute = True
            })

            Using cancellationToken.Register(Sub() callbackTcs.TrySetCanceled(cancellationToken))
                Return Await callbackTcs.Task
            End Using
        Finally
            SyncLock CallbackLock
                If ReferenceEquals(ActiveCallback, callbackTcs) Then
                    ActiveCallback = Nothing
                    ActiveState = Nothing
                End If
            End SyncLock
        End Try
    End Function

    Public Sub HandleProtocolActivation(callbackUri As String)
        Dim uri As Uri = Nothing
        If Not Uri.TryCreate(callbackUri, UriKind.Absolute, uri) Then Return
        If Not String.Equals(uri.Scheme, ProtocolScheme, StringComparison.OrdinalIgnoreCase) Then Return

        Dim callbackTcs As TaskCompletionSource(Of Uri) = Nothing
        Dim expectedState As String = Nothing
        SyncLock CallbackLock
            callbackTcs = ActiveCallback
            expectedState = ActiveState
        End SyncLock

        If callbackTcs Is Nothing Then Return

        Dim state As String = GetQueryParameter(uri, "state")
        If Not String.IsNullOrEmpty(expectedState) AndAlso Not String.Equals(state, expectedState, StringComparison.Ordinal) Then
            callbackTcs.TrySetException(New InvalidOperationException("CareLink OAuth state validation failed."))
            Return
        End If

        callbackTcs.TrySetResult(uri)
    End Sub

    Private Async Function CaptureLoopbackRedirectAsync(startUrl As String,
                                                        redirect As Uri,
                                                        cancellationToken As CancellationToken) As Task(Of RedirectResult)
        Dim expectedPath As String = If(String.IsNullOrEmpty(redirect.AbsolutePath), "/", redirect.AbsolutePath)
        Dim listener As New HttpListener()
        Dim prefix As String = $"http://{redirect.Host}:{redirect.Port}{expectedPath.TrimEnd("/"c)}/"
        listener.Prefixes.Add(prefix)
        listener.Start()

        Try
            Process.Start(New ProcessStartInfo With {
                .FileName = startUrl,
                .UseShellExecute = True
            })

            Using cancellationToken.Register(Sub() listener.Stop())
                Dim context As HttpListenerContext = Await listener.GetContextAsync()
                Dim callbackUri As Uri = context.Request.Url
                Dim responseBytes As Byte() = Encoding.UTF8.GetBytes("You can close this window and return to CareLink.")
                context.Response.ContentLength64 = responseBytes.Length
                Await context.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length)
                context.Response.Close()
                Return New RedirectResult(callbackUri)
            End Using
        Finally
            listener.Close()
        End Try
    End Function

    Private Function GetStateFromUrl(url As String) As String
        Dim uri As Uri = Nothing
        If Not Uri.TryCreate(url, UriKind.Absolute, uri) Then Return Nothing
        Return GetQueryParameter(uri, "state")
    End Function

    Private Function GetQueryParameter(uri As Uri, name As String) As String
        For Each pair As String In uri.Query.TrimStart("?"c).Split("&"c, StringSplitOptions.RemoveEmptyEntries)
            Dim parts() As String = pair.Split("="c, 2)
            If parts.Length = 2 AndAlso String.Equals(Uri.UnescapeDataString(parts(0)), name, StringComparison.OrdinalIgnoreCase) Then
                Return Uri.UnescapeDataString(parts(1))
            End If
        Next
        Return Nothing
    End Function

    Private Class RedirectResult
        Public ReadOnly Uri As Uri
        Public Sub New(value As Uri)
            Me.Uri = value
        End Sub
    End Class
End Module
