﻿using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using Serilog.Debugging;

namespace Seq.Client.Serilog
{
    class HttpLogShipper : IDisposable
    {
        readonly string _apiKey;
        readonly int _batchPostingLimit;
        readonly Timer _timer;
        readonly TimeSpan _period;
        readonly object _stateLock = new object();
        volatile bool _unloading;
        readonly string _bookmarkFilename;
        readonly string _logFolder;
        readonly HttpClient _httpClient;
        readonly string _candidateSearchPath;

        const string ApiKeyHeaderName = "X-Seq-ApiKey";
        const string BulkUploadResource = "/api/events/raw";

        public HttpLogShipper(string serverUrl, string bufferBaseFilename, string apiKey, int batchPostingLimit, TimeSpan period)
        {
            _apiKey = apiKey;
            _batchPostingLimit = batchPostingLimit;
            _period = period;
            _httpClient = new HttpClient { BaseAddress = new Uri(serverUrl) };
            _bookmarkFilename = Path.GetFullPath(bufferBaseFilename + ".bookmark");
            _logFolder = Path.GetDirectoryName(_bookmarkFilename);
            _candidateSearchPath = Path.GetFileName(bufferBaseFilename) + "*.json";
            _timer = new Timer(s => OnTick());
            _period = period;

            AppDomain.CurrentDomain.DomainUnload += OnAppDomainUnloading;
            AppDomain.CurrentDomain.ProcessExit += OnAppDomainUnloading;

            SetTimer();
        }

        void OnAppDomainUnloading(object sender, EventArgs args)
        {
            CloseAndFlush();
        }

        void CloseAndFlush()
        {
            lock (_stateLock)
            {
                if (_unloading)
                    return;

                _unloading = true;
            }

            AppDomain.CurrentDomain.DomainUnload -= OnAppDomainUnloading;
            AppDomain.CurrentDomain.ProcessExit -= OnAppDomainUnloading;

            var wh = new ManualResetEvent(false);
            if (_timer.Dispose(wh))
                wh.WaitOne();

            OnTick();
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <filterpriority>2</filterpriority>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Free resources held by the sink.
        /// </summary>
        /// <param name="disposing">If true, called because the object is being disposed; if false,
        /// the object is being disposed from the finalizer.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            CloseAndFlush();
        }

        void SetTimer()
        {
            // Note, called under _stateLock

            _timer.Change(_period, Timeout.InfiniteTimeSpan);
        }

        void OnTick()
        {
            try
            {
                var count = 0;

                do
                {
                    using (var bookmark = File.Open(_bookmarkFilename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                    {
                        long nextLineBeginsAtOffset;
                        string currentFile;

                        TryReadBookmark(bookmark, out nextLineBeginsAtOffset, out currentFile);

                        var fileSet = GetFileSet();

                        if (currentFile == null || !File.Exists(currentFile))
                        {
                            nextLineBeginsAtOffset = 0;
                            currentFile = fileSet.FirstOrDefault();
                        }

                        if (currentFile != null)
                        {
                            var payload = new StringWriter();
                            payload.Write("{\"events\":[");
                            count = 0;
                            var delimStart = "";

                            using (var current = File.Open(currentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                current.Position = nextLineBeginsAtOffset;

                                string nextLine;
                                while (count < _batchPostingLimit &&
                                    TryReadLine(current, ref nextLineBeginsAtOffset, out nextLine))
                                {
                                    ++count;
                                    payload.Write(delimStart);
                                    payload.Write(nextLine);
                                    delimStart = ",";
                                }

                                payload.Write("]}");
                            }

                            if (count > 0)
                            {
                                var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                                if (!string.IsNullOrWhiteSpace(_apiKey))
                                    content.Headers.Add(ApiKeyHeaderName, _apiKey);

                                var result = _httpClient.PostAsync(BulkUploadResource, content).Result;
                                if (result.IsSuccessStatusCode)
                                {
                                    WriteBookmark(bookmark, nextLineBeginsAtOffset, currentFile);
                                }
                                else
                                {
                                    SelfLog.WriteLine("Received failed HTTP shipping result {0}: {1}", result.StatusCode, result.Content.ReadAsStringAsync().Result);
                                }
                            }
                            else
                            {
                                if (fileSet.Length == 2 && fileSet.First() == currentFile)
                                {
                                    WriteBookmark(bookmark, 0, fileSet[1]);
                                }

                                if (fileSet.Length > 2)
                                {
                                    File.Delete(fileSet[0]);
                                }
                            }
                        }
                    }
                }
                while (count == _batchPostingLimit);
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("Exception while emitting periodic batch from {0}: {1}", this, ex);
            }
            finally
            {
                lock (_stateLock)
                {
                    if (!_unloading)
                        SetTimer();
                }
            }
        }

        static void WriteBookmark(FileStream bookmark, long nextLineBeginsAtOffset, string currentFile)
        {
            using (var writer = new StreamWriter(bookmark))
            {
                writer.WriteLine("{0}:::{1}", nextLineBeginsAtOffset, currentFile);
            }
        }

        // The weakest link in this scheme, currently.
        // More effort's required - we don't want simple whitespace in this file to
        // cause us to get the offset wrong (and thus output invalid JSON)
        static bool TryReadLine(Stream current, ref long nextStart, out string nextLine)
        {
            var includesBom = nextStart == 0;

            if (current.Length <= nextStart)
            {
                nextLine = null;
                return false;
            }

            current.Position = nextStart;

            // Allocates a buffer each time; some major perf improvements are possible here;
            using (var reader = new StreamReader(current, Encoding.UTF8, false, 128, true))
            {
                nextLine = reader.ReadLine();
            }

            if (nextLine == null)
                return false;

            nextStart += Encoding.UTF8.GetByteCount(nextLine) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (includesBom)
                nextStart += 3;

            return true;
        }

        static void TryReadBookmark(Stream bookmark, out long nextLineBeginsAtOffset, out string currentFile)
        {
            nextLineBeginsAtOffset = 0;
            currentFile = null;

            if (bookmark.Length != 0)
            {
                string current;
                using (var reader = new StreamReader(bookmark, Encoding.UTF8, false, 128, true))
                {
                    current = reader.ReadLine();
                }

                if (current != null)
                {
                    bookmark.Position = 0;
                    var parts = current.Split(new[] { ":::" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        nextLineBeginsAtOffset = long.Parse(parts[0]);
                        currentFile = parts[1];
                    }
                }
                
            }
        }

        string[] GetFileSet()
        {
            return Directory.GetFiles(_logFolder, _candidateSearchPath)
                .OrderBy(n => n)
                .ToArray();
        }
    }
}