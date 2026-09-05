Imports System.Diagnostics
Imports System.IO
Imports System.Net
Imports System.Text
Imports System.Threading

Friend Module DefaultBrowserOAuth

    Friend Async Function CaptureRedirectAsync(startUrl As String,
                                               redirectUri As String,
                                               cancellationToken As CancellationToken) As Task(Of RedirectResult)
        Dim redirect As New Uri(redirectUri, UriKind.Absolute)

        If Not redirect.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) OrElse
           Not redirect.IsLoopback Then
            Throw New NotSupportedException(
                $"The CareLink OAuth redirect URI is not a loopback HTTP URI: {redirectUri}")
        End If

        Dim prefix As String = BuildListenerPrefix(redirect)

        Using listener As New HttpListener()
            listener.Prefixes.Add(prefix)
            listener.Start()

            Dim browserInfo As New ProcessStartInfo With {
                .FileName = startUrl,
                .UseShellExecute = True}
            Process.Start(browserInfo)

            Dim contextTask As Task(Of HttpListenerContext) = listener.GetContextAsync()
            Dim completedTask As Task = Await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, cancellationToken))

            If completedTask IsNot contextTask Then
                Throw New OperationCanceledException(cancellationToken)
            End If

            Dim context As HttpListenerContext = Await contextTask
            Dim callbackUri As Uri = context.Request.Url

            If Not callbackUri.IsLoopback OrElse
               Not callbackUri.AbsolutePath.Equals(redirect.AbsolutePath, StringComparison.OrdinalIgnoreCase) Then
                WriteResponse(context.Response,
                              "<html><body>Invalid OAuth callback. You can close this tab.</body></html>",
                              HttpStatusCode.BadRequest)
                Throw New InvalidDataException("Received an unexpected OAuth callback URI.")
            End If

            Dim query As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Dim queryText As String = callbackUri.Query.TrimStart("?"c)
            If queryText.Length > 0 Then
                For Each part As String In queryText.Split("&"c, StringSplitOptions.RemoveEmptyEntries)
                    Dim pieces As String() = part.Split("="c, 2)
                    Dim key As String = Uri.UnescapeDataString(pieces(0).Replace("+", " "))
                    Dim value As String = If(pieces.Length > 1,
                                             Uri.UnescapeDataString(pieces(1).Replace("+", " ")),
                                             String.Empty)
                    query(key) = value
                Next
            End If

            Dim code As String = Nothing
            query.TryGetValue("code", code)

            Dim state As String = Nothing
            query.TryGetValue("state", state)

            Dim oauthError As String = Nothing
            query.TryGetValue("error", oauthError)

            If String.IsNullOrWhiteSpace(code) Then
                Dim errorText As String =
                    If(String.IsNullOrWhiteSpace(oauthError),
                       "No authorization code was returned.",
                       $"Authorization failed: {oauthError}")
                WriteResponse(context.Response,
                              $"<html><body>{WebUtility.HtmlEncode(errorText)}<br>You can close this tab.</body></html>",
                              HttpStatusCode.BadRequest)
                Throw New Exception(errorText)
            End If

            WriteResponse(context.Response,
                          "<html><head><meta charset='utf-8'></head><body>CareLink login complete. You can return to CareLink and close this tab.</body></html>",
                          HttpStatusCode.OK)

            Return New RedirectResult With {
                .Code = code,
                .State = state}
        End Using
    End Function

    Private Function BuildListenerPrefix(redirect As Uri) As String
        Dim builder As New UriBuilder(redirect) With {
            .Query = String.Empty,
            .Fragment = String.Empty}

        Dim path As String = builder.Path
        If String.IsNullOrWhiteSpace(path) Then
            path = "/"
        ElseIf Not path.EndsWith("/", StringComparison.Ordinal) Then
            path &= "/"
        End If

        builder.Path = path
        Return builder.Uri.AbsoluteUri
    End Function

    Private Sub WriteResponse(response As HttpListenerResponse,
                              html As String,
                              statusCode As HttpStatusCode)
        Dim bytes As Byte() = Encoding.UTF8.GetBytes(html)
        response.StatusCode = CInt(statusCode)
        response.ContentType = "text/html; charset=utf-8"
        response.ContentLength64 = bytes.Length
        response.Close(bytes, willBlock:=True)
    End Sub

End Module
