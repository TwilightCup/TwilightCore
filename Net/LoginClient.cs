using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Networking;

namespace TwilightCore.Net;

/// <summary>
/// REST login: <c>POST /api/auth/login {username,password}</c> → JWT access token.
/// Runs as a Unity coroutine on the main thread (UnityWebRequest is main-thread-only).
/// </summary>
/// <remarks>
/// The public nginx entrypoint serves the backend REST under <c>/api/</c> and
/// strips that prefix (<c>proxy_pass http://127.0.0.1:8000/</c>), so the plugin
/// requests <c>/api/auth/login</c> while the backend app itself still sees
/// <c>/auth/login</c>. The WebSocket endpoint is <c>/ws/{token}</c> (no prefix).
/// </remarks>
internal static class LoginClient
{
    public static IEnumerator Login(
        string scheme, string host, int port,
        string username, string password,
        Action<string> onToken, Action<string> onError)
    {
        string url = $"{scheme}://{host}:{port}/api/auth/login";
        string body = JsonCodec.Serialize(new Dictionary<string, object>
        {
            { "username", username ?? "" },
            { "password", password ?? "" },
        });
        byte[] bytes = new UTF8Encoding(false).GetBytes(body);

        // NOTE: no `using` — Unity 2017's UnityWebRequest + IEnumerator+using
        // interacts badly with disposal timing. Dispose explicitly at the end.
        var req = new UnityWebRequest(url, "POST")
        {
            uploadHandler = new UploadHandlerRaw(bytes),
            downloadHandler = new DownloadHandlerBuffer(),
        };
        req.SetRequestHeader("Content-Type", "application/json");
        yield return req.SendWebRequest();

        try
        {
            if (req.isNetworkError || req.isHttpError)
            {
                string detail = req.downloadHandler != null ? req.downloadHandler.text : "";
                onError($"{req.error} (POST {url})" + (string.IsNullOrEmpty(detail) ? "" : " | " + detail));
            }
            else
            {
                string raw = req.downloadHandler != null ? req.downloadHandler.text : "";
                var resp = JsonCodec.Parse(raw);
                string token = resp.GetString("access_token");
                if (string.IsNullOrEmpty(token))
                    onError("login response missing access_token: " + raw);
                else
                    onToken(token);
            }
        }
        finally
        {
            req.Dispose();
        }
    }
}
