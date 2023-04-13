﻿using System.ComponentModel;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WADH.Core;

namespace WADH
{
    public sealed class WebViewHelper : IWebViewHelper
    {
        private const string NotInitializedError = "This instance was not initialized. Please call the initialization method first.";

        private bool isInitialized = false;
        private WebView2? webView = null;
        private bool isRunning = false;
        private bool cancellationRequested = false;
        private int addonCount = 0;
        private ulong navigationId = 0;
        private int finishedDownloads = 0;

        private readonly Queue<string> addonUrls = new();

        private readonly IDebugWriter debugWriter;
        private readonly ICurseHelper curseHelper;

        public WebViewHelper(IDebugWriter debugWriter, ICurseHelper curseHelper)
        {
            this.debugWriter = debugWriter ?? throw new ArgumentNullException(nameof(debugWriter));
            this.curseHelper = curseHelper ?? throw new ArgumentNullException(nameof(curseHelper));
        }

        public event AsyncCompletedEventHandler? DownloadAddonsAsyncCompleted;
        public event ProgressChangedEventHandler? DownloadAddonsAsyncProgressChanged;

        public async Task InitAsync(WebView2 webView)
        {
            if (isInitialized)
            {
                return;
            }

            this.webView = webView;

            // The WebView2 user data folder (UDF) has to have write access and the default location is the executable´s folder.
            // Therefore some another folder (with write permissions) has to be specified here, used as the UDF for the WebView2.
            // The temp folder is used for the UDF here, since this match the temporary characteristic the UDF has in this case.
            // Also the application, when closed, do NOT try to delete the UDF temp folder, on purpose. Because the UDF contains
            // some .pma files, not accessible directly after the application has closed (Microsoft Edge doing some stuff there).
            // But in my opinion this is totally fine, since it´s the temp folder and this UDF is reused next time again anyway.

            var userDataFolder = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "MBODM-WADH-WebView2-UDF");

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, new CoreWebView2EnvironmentOptions());

            // The Microsoft WebView2 docs say: The CoreWebView2InitializationCompleted event is fired even before
            // the EnsureCoreWebView2Async() method ends. Therefore just awaiting that method is all we need here.

            await webView.EnsureCoreWebView2Async(env);

            // Completely disable JS execution, cause of our "prevent the Curse 5 sec JS timer" concept.

            webView.CoreWebView2.Settings.IsScriptEnabled = false;

            isInitialized = true;
        }

        public void ShowStartPage()
        {
            if (webView == null) return; // Enforced by NRT

            var msg1 = "The addon download sites are loaded and rendered inside this web control, using Microsoft Edge.";
            var msg2 = "The app needs to do this, since https://www.curseforge.com is strictly protected by Cloudflare.";

            var html =
                "<html>" +
                    "<body style=\"" +
                        "margin: 0;" +
                        "padding: 0;" +
                        "font-family: verdana;" +
                        "font-size: small;" +
                        "color: white;" +
                        "background-color: lightskyblue;" +
                    "\">" +
                        "<div style=\"" +
                            "position: absolute;" +
                            "top: 50%;" +
                            "left: 50%;" +
                            "transform: translate(-50%, -50%);" +
                            "white-space: nowrap;" +
                            "background-color: steelblue;" +
                        "\">" +
                            $"<div style =\"margin: 10px;\">{msg1}</div>" +
                            $"<div style =\"margin: 10px;\">{msg2}</div>" +
                        "</div>" +
                    "</body>" +
                "</html>";

            webView.NavigateToString(html);
        }

        public void DownloadAddonsAsync(IEnumerable<string> addonUrls, string downloadFolder)
        {
            if (addonUrls is null)
            {
                throw new ArgumentNullException(nameof(addonUrls));
            }

            if (!addonUrls.Any())
            {
                throw new ArgumentException("Enumerable is empty.", nameof(addonUrls));
            }

            if (string.IsNullOrWhiteSpace(downloadFolder))
            {
                throw new ArgumentException($"'{nameof(downloadFolder)}' cannot be null or whitespace.", nameof(downloadFolder));
            }

            if (!isInitialized || webView == null)
            {
                throw new InvalidOperationException(NotInitializedError);
            }

            if (isRunning)
            {
                throw new InvalidOperationException("Download is already running.");
            }

            isRunning = true;
            cancellationRequested = false;

            this.addonUrls.Clear();
            addonUrls.ToList().ForEach(url => this.addonUrls.Enqueue(url));
            addonCount = addonUrls.Count();

            webView.CoreWebView2.Profile.DefaultDownloadFolderPath = Path.GetFullPath(downloadFolder);

            webView.NavigationStarting += WebView_NavigationStarting;
            webView.NavigationCompleted += WebView_NavigationCompleted;
            webView.CoreWebView2.DownloadStarting += CoreWebView2_DownloadStarting;

            var url = this.addonUrls.Dequeue();

            if (webView.Source.ToString() == url)
            {
                // If the site has already been loaded then the events are not raised without this.
                // Happens when there is only 1 url in queue. Important i.e. for Start button state.

                webView.Reload();
            }
            else
            {
                // Kick off the whole event chain process, by loading the first url.

                webView.Source = new Uri(addonUrls.First());
            }
        }

        public void CancelDownloadAddonsAsync()
        {
            if (!isInitialized || webView == null)
            {
                throw new InvalidOperationException(NotInitializedError);
            }

            cancellationRequested = true;
        }

        private void WebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            debugWriter.PrintEventHeader();
            debugWriter.PrintEventNavigationStarting(e);

            // JS is disabled (cause of the Curse 5 sec timer), to navigate to the parsed href manually.
            // Therefore the 1st request, after the Curse addon site url, is not a redirect any longer.

            if ((curseHelper.IsAddonUrl(e.Uri) && !e.IsRedirected) ||
                (curseHelper.IsRedirect1Url(e.Uri) && !e.IsRedirected && e.NavigationId != navigationId) ||
                (curseHelper.IsRedirect2Url(e.Uri) && e.IsRedirected && e.NavigationId == navigationId) ||
                (curseHelper.IsDownloadUrl(e.Uri) && e.IsRedirected && e.NavigationId == navigationId))
            {
                debugWriter.PrintInfo("Valid url, navigation-id and redirect-state. -- > Proceed with navigation.");
                var info = "Navigation continues, cause of valid url, navigation-id and redirect-state.";

                if (curseHelper.IsAddonUrl(e.Uri))
                {
                    OnDownloadAddonsAsyncProgressChanged(WebViewHelperProgressState.AddonStarting, e.Uri, info, curseHelper.GetAddonNameFromAddonUrl(e.Uri));
                }
                else
                {
                    OnDownloadAddonsAsyncProgressChanged(WebViewHelperProgressState.CursePreludeProgress, e.Uri, info);
                }

                navigationId = e.NavigationId;

                return;
            }

            debugWriter.PrintInfo("Invalid url, navigation-id or redirect-state. --> Cancel navigation now.");
            OnDownloadAddonsAsyncCompleted(false, "Navigation cancels, cause of invalid url, navigation-id or redirect-state.");

            e.Cancel = true; // Todo: Do we need webView.Stop() here, when e.Cancel = true ???
        }

        private async void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            debugWriter.PrintEventHeader();

            if (sender is WebView2 webViewLocal)
            {
                // At the time of writing this code the EventArgs e still not including the Uri.
                // Therefore using the WebView´s Source property value as some workaround here.
                // This should be no problem since the navigations are not running concurrently.
                // Have a look at https://github.com/MicrosoftEdge/WebView2Feedback/issues/580

                var url = webViewLocal.Source.ToString();

                if (!string.IsNullOrEmpty(url))
                {
                    debugWriter.PrintEventNavigationCompleted(e, url);

                    // Note: Redirects do not raise this event (in contrast to the starting event).
                    // Therefore only the initial url and the last redirect will raise this event.
                    // Also note: WebView2 does not change its Source property value, on redirects.
                    // And since there exists no easy way to get the actual redirect location url,
                    // it is not possible to get the url for the last/2nd occurrence of this event.

                    // Success #1 (addon site loaded)
                    if (curseHelper.IsAddonUrl(url) &&
                        e.NavigationId == navigationId &&
                        e.IsSuccess &&
                        e.HttpStatusCode == 200 &&
                        e.WebErrorStatus == CoreWebView2WebErrorStatus.Unknown)
                    {
                        debugWriter.PrintInfo("Navigation to Curse addon site successfully completed.");
                        debugWriter.PrintInfo("Execute script now, to fetch 'href' attribute from loaded Curse addon site ...");

                        await webViewLocal.ExecuteScriptAsync(curseHelper.AdjustPageAppearanceScript);
                        var href = await webViewLocal.ExecuteScriptAsync(curseHelper.GrabRedirectDownloadUrlScript);

                        href = href.Trim().Trim('"');
                        debugWriter.PrintInfo($"Script returned href as string. --> {href}");

                        if (curseHelper.IsRedirect1Url(href))
                        {
                            debugWriter.PrintInfo("That returned string is a valid Curse-Redirect1-Url. --> Manually navigate to that url now ...");
                            OnDownloadAddonsAsyncProgressChanged(
                                WebViewHelperProgressState.CurseAddonSiteLoaded,
                                url,
                                $"Fetched href ('{href}') from Curse addon site.",
                                curseHelper.GetAddonNameFromAddonUrl(url));

                            // This is the central logic part of the "prevent the 5 sec JS timer" concept.
                            // By disabling JS, fetching the 'href' manually and loading that url manually.

                            webViewLocal.Stop();
                            webViewLocal.Source = new Uri(href);

                            return;
                        }
                    }

                    // Success #2 (redirects/Cloudflare finished)
                    if (curseHelper.IsRedirect1Url(url) &&
                        e.NavigationId == navigationId &&
                        !e.IsSuccess &&
                        e.HttpStatusCode == 0 &&
                        e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted)
                    {
                        debugWriter.PrintInfo("Redirects/Cloudflare processing successfully finished. -- > Download should start now ...");
                        OnDownloadAddonsAsyncProgressChanged(
                            WebViewHelperProgressState.CursePreludeFinished,
                            "There is no easy way to show the real (redirected) url here.",
                            "Curse prelude finished. Download should start now.",
                            curseHelper.GetAddonNameFromRedirect1Url(url));

                        return;
                    }

                    // Error
                    debugWriter.PrintInfo("Invalid url, navigation-id or status-codes. --> Stop navigation now.");
                    webViewLocal.Stop();
                    OnDownloadAddonsAsyncCompleted(false, "Navigation stopped, cause of invalid url, navigation-id or status-codes.");
                }
            }
        }

        private void CoreWebView2_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            debugWriter.PrintEventHeader();
            debugWriter.PrintEventDownloadStarting(e);

            debugWriter.PrintInfo("Register event handler --> DownloadOperation.BytesReceivedChanged");
            debugWriter.PrintInfo("Register event handler --> DownloadOperation.StateChanged");

            e.DownloadOperation.BytesReceivedChanged += DownloadOperation_BytesReceivedChanged;
            e.DownloadOperation.StateChanged += DownloadOperation_StateChanged;

            var file = Path.GetFileName(e.ResultFilePath);
            var url = e.DownloadOperation.Uri;

            debugWriter.PrintInfo($"Starting file download --> {file}");
            OnDownloadAddonsAsyncProgressChanged(
                WebViewHelperProgressState.DownloadStarting,
                url,
                "Starting file download.",
                curseHelper.GetAddonNameFromDownloadUrl(url),
                file,
                0,
                e.DownloadOperation.TotalBytesToReceive ?? 0);

            e.Handled = true; // Do not show default download dialog
        }

        private void DownloadOperation_BytesReceivedChanged(object? sender, object e)
        {
            if (sender is CoreWebView2DownloadOperation downloadOperation)
            {
                var received = (ulong)downloadOperation.BytesReceived;
                var total = downloadOperation.TotalBytesToReceive ?? 0;

                // Only show real chunks and not just the final chunk, when there is only one.
                // This happens sometimes for mid-sized files. The very small ones create no
                // event at all. The very big ones create a bunch of events. But for all the
                // mid-sized files there is only 1 event with i.e. 12345/12345 byte progress.
                // Therefore it seems OK to ignore them, for better debug output readability.

                if (received < total)
                {
                    debugWriter.PrintEventHeader();
                    debugWriter.PrintEventReceivedChanged(downloadOperation);
                    debugWriter.PrintInfo($"Received {received} of {total} bytes.");

                    var url = downloadOperation.Uri;
                    var file = Path.GetFileName(downloadOperation.ResultFilePath);
                    var addon = curseHelper.GetAddonNameFromDownloadUrl(url);

                    OnDownloadAddonsAsyncProgressChanged(WebViewHelperProgressState.DownloadProgress, url, "Downloading file...", addon, file, received, total);

                    // Doing this inside above if clause, allows small file downloads to finish.

                    if (cancellationRequested)
                    {
                        downloadOperation.Cancel();
                    }
                }
            }
        }

        private void DownloadOperation_StateChanged(object? sender, object e)
        {
            debugWriter.PrintEventHeader();

            if (sender is CoreWebView2DownloadOperation downloadOperation)
            {
                debugWriter.PrintEventStateChanged(downloadOperation);

                if (downloadOperation.State == CoreWebView2DownloadState.InProgress)
                {
                    debugWriter.PrintInfo("Warning: State is 'InProgress' and this usually does not happen. --> Anyway, continue with download.");

                    return;
                }

                debugWriter.PrintInfo("Unregister event handler --> DownloadOperation.BytesReceivedChanged");
                debugWriter.PrintInfo("Unregister event handler --> DownloadOperation.StateChanged");

                downloadOperation.BytesReceivedChanged -= DownloadOperation_BytesReceivedChanged;
                downloadOperation.StateChanged -= DownloadOperation_StateChanged;

                var url = downloadOperation.Uri;
                var addon = curseHelper.GetAddonNameFromDownloadUrl(url);
                var file = Path.GetFileName(downloadOperation.ResultFilePath);
                var received = (ulong)downloadOperation.BytesReceived;
                var total = downloadOperation.TotalBytesToReceive ?? 0;

                if (downloadOperation.State == CoreWebView2DownloadState.Completed)
                {
                    finishedDownloads++;

                    debugWriter.PrintInfo($"Finished a file download --> {file}");
                    OnDownloadAddonsAsyncProgressChanged(WebViewHelperProgressState.DownloadFinished, url, "Finished file download.", addon, file, received, total);
                }

                if (webView == null) return; // Enforced by NRT

                if (!addonUrls.Any() || cancellationRequested)
                {
                    // No more addons in queue to download or cancellation occurred, so finish the process.

                    debugWriter.PrintInfo("Unregister event handler --> NavigationStarting");
                    debugWriter.PrintInfo("Unregister event handler --> NavigationCompleted");
                    debugWriter.PrintInfo("Unregister event handler --> DownloadStarting");

                    webView.NavigationStarting -= WebView_NavigationStarting;
                    webView.NavigationCompleted -= WebView_NavigationCompleted;
                    webView.CoreWebView2.DownloadStarting -= CoreWebView2_DownloadStarting;

                    debugWriter.PrintInfo(cancellationRequested ?
                        "Queue of urls is not empty yet, but cancellation occurred. --> !!! Stop and not proceed with next url !!!" :
                        "Queue of urls is empty, there is nothing else to download. --> !!! All addons successfully downloaded !!!");
                    OnDownloadAddonsAsyncCompleted(cancellationRequested);

                    isRunning = false;

                    return;
                }

                // Still some addons to download in queue and no cancellation occurred, so proceed with next url.

                var next = addonUrls.Dequeue();

                debugWriter.PrintInfo($"Queue of urls is not empty yet, so proceed with next url in queue. --> {next}");
                OnDownloadAddonsAsyncProgressChanged(
                    WebViewHelperProgressState.AddonFinished,
                    url,
                    $"Addon finished. Proceeding with next addon ('{curseHelper.GetAddonNameFromAddonUrl(next)}').",
                    addon,
                    file,
                    received,
                    total);

                webView.Source = new Uri(next);
            }
        }

        private void OnDownloadAddonsAsyncProgressChanged(
            WebViewHelperProgressState state,
            string url,
            string info = "",
            string addon = "",
            string file = "",
            ulong received = default,
            ulong total = default)
        {
            DownloadAddonsAsyncProgressChanged?.Invoke(
                this,
                new ProgressChangedEventArgs(CalcPercent(), new WebViewHelperProgress(state, url, info, addon, file, received, total)));
        }

        private void OnDownloadAddonsAsyncCompleted(bool cancelled = default, string error = "")
        {
            DownloadAddonsAsyncCompleted?.Invoke(
                this,
                new AsyncCompletedEventArgs(error != "" ? new InvalidOperationException(error) : null, cancelled, $"Completed {finishedDownloads}/{addonCount} addons."));
        }

        private int CalcPercent()
        {
            // Doing casts inside try/catch block, just to be sure.

            try
            {
                var percent = (double)(100 / addonCount) * finishedDownloads;

                return (int)percent;
            }
            catch
            {
                return 0;
            }
        }
    }
}
