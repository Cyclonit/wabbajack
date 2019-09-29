using CG.Web.MegaApiClient;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Wabbajack.Common;
using Wabbajack.NexusApi;

namespace Wabbajack
{
    public class Downloader
    {
        private readonly string _downloadFolder;

        private NexusApiClient _nexusClient;
        private NexusApiClient NexusClient
        {
            get
            {
                if (_nexusClient == null)
                    _nexusClient = InitializeNexusClient();

                return _nexusClient;
            }
        }

        private MegaApiClient _megaClient;
        private MegaApiClient MegaClient
        {
            get
            {
                if (_megaClient == null)
                {
                    _megaClient = new MegaApiClient();
                    _megaClient.LoginAnonymous();
                }

                return _megaClient;
            }
        }

        private HttpClient _httpClient;


        public Downloader(string downloadFolder) {
            _downloadFolder = downloadFolder;

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", Consts.UserAgent);
        }

        private NexusApiClient InitializeNexusClient()
        {
            var nexusClient = new NexusApiClient();

            if (!nexusClient.IsAuthenticated)
            {
//                Error($"Authenticating for the Nexus failed. A nexus account is required to automatically download mods.");
                return null;
            }
            else if (!nexusClient.IsPremium)
            {
//                Error($"Automated installs with Wabbajack requires a premium nexus account. {nexusClient.Username} is not a premium account.");
                return null;
            }

            return nexusClient;
        }


        public bool DownloadArchive(Archive archive)
        {
            try
            {
                switch (archive)
                {
                    case NexusMod a:
                        return DownloadNexusArchive(a);
                    case MEGAArchive a:
                        return DownloadMegaArchive(a);
                    case GoogleDriveMod a:
                        return DownloadGoogleDriveArchive(a);
                    case MODDBArchive a:
                        return DownloadModDbArchive(a);
                    case MediaFireArchive a:
                        return false;
                    //return DownloadMediaFireArchive(archive, a.URL, download);
                    case DirectURLArchive a:
                        return DownloadUrlDirect(archive, a.URL, a.Headers);
                }

                return false;
            }
            catch (Exception ex)
            {
                Utils.Log($"Download error for file {archive.Name}");
                Utils.Log(ex.ToString());
                return false;
            }
        }

        public bool TestArchive(Archive archive)
        {
            try
            {
                switch (archive)
                {
                    case NexusMod a:
                        return TestNexusArchive(a);
                    case MEGAArchive a:
                        return TestMegaArchive(a);
                    case GoogleDriveMod a:
                        return TestGoogleDriveArchive(a);
                    case MODDBArchive a:
                        return TestModDbArchive(a);
                    case MediaFireArchive a:
                        return false;
                    //return DownloadMediaFireArchive(archive, a.URL, download);
                    case DirectURLArchive a:
                        return DownloadUrlDirect(archive, a.URL, a.Headers, true);
                }

                return false;
            }
            catch (Exception ex)
            {
                Utils.Log($"Download error for file {archive.Name}");
                Utils.Log(ex.ToString());
                return false;
            }
        }

        private bool TestNexusArchive(NexusMod archive)
        {
            return NexusClient.GetNexusDownloadLink(archive) != null;
        }

        private bool DownloadNexusArchive(NexusMod archive)
        {
            string url;
            try
            {
                url = NexusClient.GetNexusDownloadLink(archive);
            }
            catch (Exception ex)
            {
//                Info($"{a.Name} - Error Getting Nexus Download URL - {ex.Message}");
                return false;
            }

//          Info($"Downloading Nexus Archive - {archive.Name} - {a.GameName} - {a.ModID} - {a.FileID}");
            return DownloadUrlDirect(archive, url);
        }

        private void DownloadMediaFireArchive(MediaFireArchive archive)
        {
            throw new NotImplementedException();

//            var client = new HttpClient();
//            var result = client.GetStringSync(url);
//            var regex = new Regex("(?<= href =\\\").*\\.mediafire\\.com.*(?=\\\")");
//            var confirm = regex.Match(result);
//            DownloadURLDirect(a, confirm.ToString(), client);
        }

        private bool TestMegaArchive(MEGAArchive archive)
        {
            var fileUri = new Uri(archive.URL);
            return MegaClient.GetNodeFromLink(fileUri) != null;
        }

        private bool DownloadMegaArchive(MEGAArchive archive)
        {
            var fileUri = new Uri(archive.URL);
            var outputPath = Path.Combine(_downloadFolder, archive.Name);
            MegaClient.DownloadFile(fileUri, outputPath);
            return true;
        }

        private string GetConfirmedGoogleDriveUrl(GoogleDriveMod archive)
        {
            var initialUrl = $"https://drive.google.com/uc?id={archive.Id}&export=download";
            var client = new HttpClient();
            var result = client.GetStringSync(initialUrl);
            var regex = new Regex("(?<=/uc\\?export=download&amp;confirm=).*(?=;id=)");
            var confirm = regex.Match(result);
            return $"https://drive.google.com/uc?export=download&confirm={confirm}&id={archive.Id}";
        }

        private bool TestGoogleDriveArchive(GoogleDriveMod archive)
        {
            return GetConfirmedGoogleDriveUrl(archive) != null;
        }

        private bool DownloadGoogleDriveArchive(GoogleDriveMod archive)
        {
            var url = GetConfirmedGoogleDriveUrl(archive);
            return DownloadUrlDirect(archive, url);
        }

        private string GetModDbMirrorUrl(MODDBArchive archive)
        {
            var MIRROR_URL_REGEX = new Regex("https:\\/\\/www\\.moddb\\.com\\/downloads\\/mirror\\/.*(?=\\\")");

            var mirrorPage = _httpClient.GetStringSync(archive.URL);
            var match = MIRROR_URL_REGEX.Match(mirrorPage);

            return match.Value;
        }

        private bool TestModDbArchive(MODDBArchive archive)
        {
            return DownloadUrlDirect(archive, GetModDbMirrorUrl(archive), test: true);
        }

        private bool DownloadModDbArchive(MODDBArchive archive)
        {
            return DownloadUrlDirect(archive, GetModDbMirrorUrl(archive));
        }

        private bool TestUrlDirect(Archive archive, string url, List<string> headers)
        {
            return DownloadUrlDirect(archive, url, headers, true);
        }

        private HttpResponseMessage GetSync(string url, List<string> headers)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            headers?.ForEach(header =>
            {
                var idx = header.IndexOf(':');
                var key = header.Substring(0, idx);
                var value = header.Substring(idx + 1);
                request.Headers.Add(key, value);
            });

            var task = _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            task.Wait();

            return task.Result;
        }

        private bool DownloadUrlDirect(Archive archive, string url, 
            List<string> headers = null, bool test = false)
        {
            var response = GetSync(url, headers);

            if (!response.IsSuccessStatusCode)
                return false;


            // read for the stream to have been read fully
            var streamTask = response.Content.ReadAsStreamAsync();
            streamTask.Wait();

            if (streamTask.IsFaulted)
            {
//              Info($"While downloading {url} - {stream.Exception.ExceptionToString()}");
                return false;
            }

            // if all we are doing is testing, return now
            // TODO: Can we return prior to reading the entire stream?
            if (test)
                return true;

            long contentLength = 1;
            if (response.Content.Headers.Contains("Content-Length"))
            {
                var headerValue = response.Content.Headers.GetValues("Content-Length").First();
                long.TryParse(headerValue, out contentLength);
            }

            var outputPath = Path.Combine(_downloadFolder, archive.Name);

            using (var webStream = streamTask.Result)
            using (var fileStream = File.OpenWrite(outputPath))
            {
                long totalRead = 0;
                int bufferSize = 1024 * 32;

                var buffer = new byte[bufferSize];
                while (true)
                {
                    var read = webStream.Read(buffer, 0, bufferSize);
                    if (read == 0) break;

                    Utils.Status($"Downloading {archive.Name}", (int)(totalRead * 100 / contentLength));

                    fileStream.Write(buffer, 0, read);
                    totalRead += read;
                }
            }

            // trigger hashing of the downloaded archive
            HashCache.Instance.GetFileHash(outputPath);

            return true;
        }

    }
}
