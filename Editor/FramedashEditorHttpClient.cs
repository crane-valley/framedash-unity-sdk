using System;
using System.Collections.Generic;
using Framedash.Editor.Logic;
using UnityEngine;
using UnityEngine.Networking;

namespace Framedash.Editor
{
    internal sealed class FramedashEditorHttpClient
    {
        private readonly List<UnityWebRequest> _activeRequests = new List<UnityWebRequest>();
        private bool _shuttingDown;

        public void FetchMaps(
            FramedashHeatmapSettings settings,
            Action<bool, List<FramedashEditorLogic.MapInfo>, string> onComplete)
        {
            if (!PrepareRequest(
                settings,
                out string baseUrl,
                out string apiKey,
                out string projectId,
                out string error))
            {
                InvokeSafely(onComplete, false, null, error);
                return;
            }

            string url = baseUrl + "/api/v1/projects/" + Uri.EscapeDataString(projectId) + "/maps";
            StartGet(url, apiKey, (connected, statusCode, body, requestError) =>
            {
                if (!connected)
                {
                    InvokeSafely(
                        onComplete,
                        false,
                        null,
                        string.IsNullOrEmpty(requestError)
                            ? "Unable to reach the Framedash API."
                            : requestError);
                    return;
                }
                if (statusCode < 200 || statusCode > 299)
                {
                    InvokeSafely(
                        onComplete,
                        false,
                        null,
                        FramedashEditorLogic.ParseProblemMessage(body, HttpFallback(statusCode)));
                    return;
                }
                if (!FramedashEditorLogic.ParseMapsResponse(body, out var maps, out string parseError))
                {
                    InvokeSafely(onComplete, false, null, parseError);
                    return;
                }
                InvokeSafely(onComplete, true, maps, null);
            });
        }

        public void FetchHeatmap(
            FramedashHeatmapSettings settings,
            string mapSlug,
            Action<bool, List<FramedashEditorLogic.HeatmapCell>, string> onComplete)
        {
            if (!PrepareRequest(
                settings,
                out string baseUrl,
                out string apiKey,
                out string projectId,
                out string error))
            {
                InvokeSafely(onComplete, false, null, error);
                return;
            }
            if (string.IsNullOrEmpty(mapSlug))
            {
                InvokeSafely(onComplete, false, null, "Select a map before fetching heatmap data.");
                return;
            }
            if (!FramedashEditorLogic.IsAllowedDays(settings.Days))
            {
                InvokeSafely(onComplete, false, null, "Days must be one of 1, 7, 14, or 30.");
                return;
            }
            if (!FramedashEditorLogic.IsAllowedCellSize(settings.CellSize))
            {
                InvokeSafely(onComplete, false, null, "Cell size must be one of 5, 10, 25, or 50.");
                return;
            }

            string url = baseUrl
                + "/api/v1/projects/" + Uri.EscapeDataString(projectId)
                + "/heatmap?mapId=" + Uri.EscapeDataString(mapSlug)
                + "&cellSize=" + settings.CellSize
                + "&days=" + settings.Days;
            if (!string.IsNullOrEmpty(settings.EventNameFilter))
            {
                url += "&eventName=" + Uri.EscapeDataString(settings.EventNameFilter);
            }

            StartGet(url, apiKey, (connected, statusCode, body, requestError) =>
            {
                if (!connected)
                {
                    InvokeSafely(
                        onComplete,
                        false,
                        null,
                        string.IsNullOrEmpty(requestError)
                            ? "Unable to reach the Framedash API."
                            : requestError);
                    return;
                }
                if (statusCode < 200 || statusCode > 299)
                {
                    InvokeSafely(
                        onComplete,
                        false,
                        null,
                        FramedashEditorLogic.ParseProblemMessage(body, HttpFallback(statusCode)));
                    return;
                }
                if (!FramedashEditorLogic.ParseHeatmapResponse(body, out var cells, out string parseError))
                {
                    InvokeSafely(onComplete, false, null, parseError);
                    return;
                }
                InvokeSafely(onComplete, true, cells, null);
            });
        }

        public void Shutdown()
        {
            if (_shuttingDown)
            {
                return;
            }
            _shuttingDown = true;
            UnityWebRequest[] requests = _activeRequests.ToArray();
            _activeRequests.Clear();
            for (int i = 0; i < requests.Length; i++)
            {
                try
                {
                    requests[i].Abort();
                    requests[i].Dispose();
                }
                catch (Exception exception)
                {
                    Debug.LogError("[Framedash] Failed to stop an editor request: " + exception);
                }
            }
        }

        private bool PrepareRequest(
            FramedashHeatmapSettings settings,
            out string baseUrl,
            out string apiKey,
            out string projectId,
            out string error)
        {
            baseUrl = "";
            apiKey = "";
            projectId = "";
            error = "";
            if (_shuttingDown)
            {
                error = "Framedash editor client is shutting down.";
                return false;
            }
            if (settings == null)
            {
                error = "Framedash editor settings are unavailable.";
                return false;
            }

            baseUrl = (settings.ApiBaseUrl ?? "").Trim();
            while (baseUrl.EndsWith("/", StringComparison.Ordinal))
            {
                baseUrl = baseUrl.Substring(0, baseUrl.Length - 1);
            }
            apiKey = FramedashEditorLogic.ResolveReadApiKey(
                settings.ReadApiKey,
                Environment.GetEnvironmentVariable("FRAMEDASH_ANALYTICS_API_KEY"));
            projectId = (settings.ProjectId ?? "").Trim();
            if (baseUrl.Length == 0)
            {
                error = "Configure the Framedash API base URL.";
                return false;
            }
            if (!Framedash.EndpointSecurity.IsEndpointTransportSecure(baseUrl))
            {
                error = "The API base URL is not secure. Use HTTPS, or HTTP only for canonical localhost.";
                return false;
            }
            if (apiKey.Length == 0)
            {
                error = "Configure an analytics:read API key or set FRAMEDASH_ANALYTICS_API_KEY before launching Unity.";
                return false;
            }
            if (projectId.Length == 0)
            {
                error = "Configure a Framedash project ID.";
                return false;
            }
            return true;
        }

        private void StartGet(
            string url,
            string apiKey,
            Action<bool, long, string, string> onComplete)
        {
            if (_shuttingDown)
            {
                InvokeResponseSafely(onComplete, false, 0, "", "");
                return;
            }

            UnityWebRequest request = null;
            try
            {
                request = UnityWebRequest.Get(url);
                request.SetRequestHeader("X-API-Key", apiKey);
                request.SetRequestHeader("Accept", "application/json, application/problem+json");
                // Blocking every redirect avoids relying on an effective-URL property whose
                // behavior varies across Unity 2022.3 patches. Every 3xx therefore fails
                // closed before the analytics read key can be forwarded to another origin.
                request.redirectLimit = 0;
                // Runtime/TransportLayer.cs uses RequestTimeoutSeconds=10 for its hot
                // flush path; this manual fetch may return 10,000 cells, so 30s gives
                // it room while still releasing _busy instead of wedging the window.
                request.timeout = 30;
                _activeRequests.Add(request);
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                operation.completed += _ =>
                {
                    try
                    {
                        if (_shuttingDown)
                        {
                            return;
                        }

                        long statusCode = request.responseCode;
                        if (statusCode >= 300 && statusCode <= 399)
                        {
                            string location = request.GetResponseHeader("Location");
                            // A 3xx without a Location header yields null here, and
                            // Uri.TryCreate(null, ...) throws on some Mono profiles.
                            bool absoluteLocation = !string.IsNullOrEmpty(location)
                                && Uri.TryCreate(
                                    location,
                                    UriKind.Absolute,
                                    out Uri ignoredLocation);
                            bool crossOrigin = absoluteLocation
                                && FramedashEditorEndpointSecurity.IsCrossOriginRedirect(url, location);
                            InvokeResponseSafely(
                                onComplete,
                                false,
                                statusCode,
                                "",
                                crossOrigin
                                    ? "Framedash request was redirected across origins; the response was rejected because the analytics read key may have been exposed."
                                    : "Framedash request was redirected; the response was rejected.");
                            return;
                        }

                        string body = request.downloadHandler == null
                            ? ""
                            : request.downloadHandler.text;
                        if (request.result != UnityWebRequest.Result.Success && statusCode <= 0)
                        {
                            InvokeResponseSafely(
                                onComplete,
                                false,
                                0,
                                body,
                                string.IsNullOrEmpty(request.error)
                                    ? "Unable to reach the Framedash API."
                                    : request.error);
                            return;
                        }
                        InvokeResponseSafely(onComplete, true, statusCode, body, "");
                    }
                    catch (Exception exception)
                    {
                        Debug.LogError("[Framedash] Editor request completion failed: " + exception);
                        InvokeResponseSafely(
                            onComplete,
                            false,
                            0,
                            "",
                            "An unexpected Framedash editor request error occurred.");
                    }
                    finally
                    {
                        // Shutdown() already disposed every active request; disposing
                        // again here would be redundant and can raise on some profiles.
                        if (!_shuttingDown)
                        {
                            _activeRequests.Remove(request);
                            try
                            {
                                request.Dispose();
                            }
                            catch (Exception exception)
                            {
                                Debug.LogError("[Framedash] Failed to release an editor request: " + exception);
                            }
                        }
                    }
                };
                // A domain reload can abandon this managed callback; the request then has no
                // owner and cannot affect editor state, which is safe even though no status arrives.
            }
            catch (Exception exception)
            {
                if (request != null)
                {
                    _activeRequests.Remove(request);
                    request.Dispose();
                }
                Debug.LogError("[Framedash] Failed to start editor request: " + exception);
                InvokeResponseSafely(
                    onComplete,
                    false,
                    0,
                    "",
                    "Unable to reach the Framedash API.");
            }
        }

        private static string HttpFallback(long statusCode)
        {
            return statusCode > 0
                ? "Framedash request failed (HTTP " + statusCode + ")."
                : "Framedash request failed.";
        }

        private static void InvokeSafely<T>(
            Action<bool, List<T>, string> callback,
            bool success,
            List<T> values,
            string error)
        {
            try
            {
                callback?.Invoke(success, values, error);
            }
            catch (Exception exception)
            {
                Debug.LogError("[Framedash] Editor response callback failed: " + exception);
            }
        }

        private static void InvokeResponseSafely(
            Action<bool, long, string, string> callback,
            bool connected,
            long statusCode,
            string body,
            string error)
        {
            try
            {
                callback?.Invoke(connected, statusCode, body, error);
            }
            catch (Exception exception)
            {
                Debug.LogError("[Framedash] Editor HTTP callback failed: " + exception);
            }
        }
    }
}
