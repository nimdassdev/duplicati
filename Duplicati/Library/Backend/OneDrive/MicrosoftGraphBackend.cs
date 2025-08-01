// Copyright (C) 2025, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.
using Duplicati.Library.Backend.MicrosoftGraph;
using Duplicati.Library.Common.IO;
using Duplicati.Library.Interface;
using Duplicati.Library.Logging;
using Duplicati.Library.Utility;
using Duplicati.Library.Utility.Options;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Duplicati.Library.Backend
{
    /// <summary>
    /// Base class for all backends based on the Microsoft Graph API:
    /// https://developer.microsoft.com/en-us/graph/
    /// </summary>
    /// <remarks>
    /// HttpClient is used instead of OAuthHelper because OAuthHelper internally converts URLs to System.Uri, which throws UriFormatException when the URL contains ':' characters.
    /// https://stackoverflow.com/questions/2143856/why-does-colon-in-uri-passed-to-uri-makerelativeuri-cause-an-exception
    /// https://social.msdn.microsoft.com/Forums/vstudio/en-US/bf11fc74-975a-4c4d-8335-8e0579d17fdf/uri-containing-colons-incorrectly-throws-uriformatexception?forum=netfxbcl
    /// 
    /// Note that instead of using Task.Result to wait for the results of asynchronous operations,
    /// this class uses the Utility.Await() extension method, since it doesn't wrap exceptions in AggregateExceptions.
    /// </remarks>
    public abstract class MicrosoftGraphBackend : IBackend, IStreamingBackend, IQuotaEnabledBackend, IRenameEnabledBackend
    {
        private static readonly string LOGTAG = Log.LogTagFromType<MicrosoftGraphBackend>();

        private const string SERVICES_AGREEMENT = "https://www.microsoft.com/en-us/servicesagreement";
        private const string PRIVACY_STATEMENT = "https://privacy.microsoft.com/en-us/privacystatement";

        private const string BASE_ADDRESS = "https://graph.microsoft.com";

        private const string UPLOAD_SESSION_FRAGMENT_SIZE_OPTION = "fragment-size";
        private const string UPLOAD_SESSION_FRAGMENT_RETRY_COUNT_OPTION = "fragment-retry-count";
        private const string UPLOAD_SESSION_FRAGMENT_RETRY_DELAY_OPTION = "fragment-retry-delay";

        private const int UPLOAD_SESSION_FRAGMENT_DEFAULT_RETRY_COUNT = 5;
        private const int UPLOAD_SESSION_FRAGMENT_DEFAULT_RETRY_DELAY = 1000;

        /// <summary>
        /// Max size of file that can be uploaded in a single PUT request is 4 MB:
        /// https://developer.microsoft.com/en-us/graph/docs/api-reference/v1.0/api/driveitem_put_content
        /// </summary>
        private const int PUT_MAX_SIZE = 4 * 1000 * 1000;

        /// <summary>
        /// Max size of each individual upload in an upload session is 60 MiB:
        /// https://docs.microsoft.com/en-us/onedrive/developer/rest-api/api/driveitem_createuploadsession
        /// </summary>
        private const int UPLOAD_SESSION_FRAGMENT_MAX_SIZE = 60 * 1024 * 1024;

        /// <summary>
        /// Default fragment size of 10 MiB, as the documentation recommends something in the range of 5-10 MiB,
        /// and it still complies with the 320 KiB multiple requirement.
        /// </summary>
        private const int UPLOAD_SESSION_FRAGMENT_DEFAULT_SIZE = 10 * 1024 * 1024;

        /// <summary>
        /// Each fragment in an upload session must be a size that is multiple of this size.
        /// https://docs.microsoft.com/en-us/onedrive/developer/rest-api/api/driveitem_createuploadsession
        /// There is some confusion in the docs as to whether this is actually required, however...
        /// </summary>
        private const int UPLOAD_SESSION_FRAGMENT_MULTIPLE_SIZE = 320 * 1024;

        /// <summary>
        /// Cached copy of the PATH method
        /// </summary>
        private static readonly HttpMethod PatchMethod = new HttpMethod("PATCH");

        /// <summary>
        /// Dummy UploadSession given as an empty body to createUploadSession requests when using the OAuthHelper instead of the OAuthHttpClient.
        /// The API expects a ContentLength to be specified, but the body content is optional.
        /// Passing an empty object (or specifying the ContentLength explicitly) bypasses this error.
        /// </summary>
        private static readonly UploadSession dummyUploadSession = new UploadSession();

        protected delegate string DescriptionTemplateDelegate(string mssadescription, string mssalink, string msopdescription, string msoplink);

        private readonly JsonSerializer m_serializer = new JsonSerializer();
        private readonly OAuthHttpClient m_client;
        private readonly int fragmentSize;
        private readonly int fragmentRetryCount;
        private readonly int fragmentRetryDelay; // In milliseconds

        // Whenever a response includes a Retry-After header, we'll update this timestamp with when we can next
        // send a request. And before sending any requests, we'll make sure to wait until at least this time.
        // Since this may be read and written by multiple threads, it is stored as a long and updated using Interlocked.Exchange.
        private readonly RetryAfterHelper m_retryAfter;
        protected readonly TimeoutOptionsHelper.Timeouts m_timeouts;

        private string[]? dnsNames = null;

        private readonly Lazy<string> rootPathFromURL;
        private string RootPath => this.rootPathFromURL.Value;
        private string TokenUrl => AuthIdOptionsHelper.GetOAuthLoginUrl(ProtocolKey, null);

        protected MicrosoftGraphBackend()
        {
            // Constructor needed for dynamic loading to find it
            m_client = null!;
            m_retryAfter = null!;
            m_timeouts = null!;
            rootPathFromURL = null!;
        }

        protected MicrosoftGraphBackend(string url, string protocolKey, Dictionary<string, string?> options)
        {
            var authId = AuthIdOptionsHelper.Parse(options)
                .RequireCredentials(TokenUrl);

            if (options.TryGetValue(UPLOAD_SESSION_FRAGMENT_SIZE_OPTION, out var fragmentSizeStr) && int.TryParse(fragmentSizeStr, out this.fragmentSize))
            {
                // Make sure the fragment size is a multiple of the desired multiple size.
                // If it isn't, we round down to the nearest multiple below it.
                this.fragmentSize = (this.fragmentSize / UPLOAD_SESSION_FRAGMENT_MULTIPLE_SIZE) * UPLOAD_SESSION_FRAGMENT_MULTIPLE_SIZE;

                // Make sure the fragment size isn't larger than the maximum, or smaller than the minimum
                this.fragmentSize = Math.Max(Math.Min(this.fragmentSize, UPLOAD_SESSION_FRAGMENT_MAX_SIZE), UPLOAD_SESSION_FRAGMENT_MULTIPLE_SIZE);
            }
            else
            {
                this.fragmentSize = UPLOAD_SESSION_FRAGMENT_DEFAULT_SIZE;
            }

            if (!(options.TryGetValue(UPLOAD_SESSION_FRAGMENT_RETRY_COUNT_OPTION, out var fragmentRetryCountStr) && int.TryParse(fragmentRetryCountStr, out this.fragmentRetryCount)))
                this.fragmentRetryCount = UPLOAD_SESSION_FRAGMENT_DEFAULT_RETRY_COUNT;

            if (!(options.TryGetValue(UPLOAD_SESSION_FRAGMENT_RETRY_DELAY_OPTION, out var fragmentRetryDelayStr) && int.TryParse(fragmentRetryDelayStr, out this.fragmentRetryDelay)))
                this.fragmentRetryDelay = UPLOAD_SESSION_FRAGMENT_DEFAULT_RETRY_DELAY;

            this.m_client = new OAuthHttpClient(authId.AuthId!, protocolKey, authId.OAuthUrl);
            this.m_client.BaseAddress = new System.Uri(BASE_ADDRESS);

            this.m_retryAfter = RetryAfterHelper.CreateOrGetRetryAfterHelper(url);
            this.m_timeouts = TimeoutOptionsHelper.Parse(options);

            // Extract out the path to the backup root folder from the given URI. Since this can be an expensive operation, 
            // we will cache the value using a lazy initializer.
            // TODO: Should not call network methods in constructor
            this.rootPathFromURL = new Lazy<string>(() => MicrosoftGraphBackend.NormalizeSlashes(Utility.Utility.Await(this.GetRootPathFromUrlAsync(url, CancellationToken.None))));
        }

        public abstract string ProtocolKey { get; }

        public abstract string DisplayName { get; }

        public string Description => this.DescriptionTemplate(
                    "Microsoft Service Agreement",
                    SERVICES_AGREEMENT,
                    "Microsoft Online Privacy Statement",
                    PRIVACY_STATEMENT);

        public IList<ICommandLineArgument> SupportedCommands => [
            .. AuthIdOptionsHelper.GetOptions(TokenUrl),
            new CommandLineArgument(UPLOAD_SESSION_FRAGMENT_SIZE_OPTION, CommandLineArgument.ArgumentType.Integer, Strings.MicrosoftGraph.FragmentSizeShort, Strings.MicrosoftGraph.FragmentSizeLong, Library.Utility.Utility.FormatSizeString(UPLOAD_SESSION_FRAGMENT_DEFAULT_SIZE)),
            new CommandLineArgument(UPLOAD_SESSION_FRAGMENT_RETRY_COUNT_OPTION, CommandLineArgument.ArgumentType.Integer, Strings.MicrosoftGraph.FragmentRetryCountShort, Strings.MicrosoftGraph.FragmentRetryCountLong, UPLOAD_SESSION_FRAGMENT_DEFAULT_RETRY_COUNT.ToString()),
            new CommandLineArgument(UPLOAD_SESSION_FRAGMENT_RETRY_DELAY_OPTION, CommandLineArgument.ArgumentType.Integer, Strings.MicrosoftGraph.FragmentRetryDelayShort, Strings.MicrosoftGraph.FragmentRetryDelayLong, UPLOAD_SESSION_FRAGMENT_DEFAULT_RETRY_DELAY.ToString()),
            new CommandLineArgument("use-http-client", CommandLineArgument.ArgumentType.Boolean, Strings.MicrosoftGraph.UseHttpClientShort, Strings.MicrosoftGraph.UseHttpClientLong, "true", null, null, Strings.MicrosoftGraph.UseHttpClientDeprecated),
            .. AdditionalSupportedCommands,
            .. TimeoutOptionsHelper.GetOptions()
        ];

        public async Task<string[]> GetDNSNamesAsync(CancellationToken cancelToken)
        {
            if (dnsNames == null)
            {
                // The DNS names that this instance may need to access include:
                // - Core graph API endpoint
                // - Upload session endpoint (which seems to be different depending on the drive being accessed - not sure if it can vary for a single drive)
                // To get the upload session endpoint, we can start an upload session and then immediately cancel it.
                // We pick a random file name (using a guid) to make sure we don't conflict with an existing file
                var dnsTestFile = string.Format("DNSNameTest-{0}", Guid.NewGuid());
                var drivePrefix = await GetDrivePrefix(cancelToken).ConfigureAwait(false);
                var uploadSession = await this.PostAsync<UploadSession>(string.Format("{0}/root:{1}{2}:/createUploadSession", drivePrefix, this.RootPath, NormalizeSlashes(dnsTestFile)), dummyUploadSession, cancelToken).ConfigureAwait(false);

                // Canceling an upload session is done by sending a DELETE to the upload URL
                await m_retryAfter.WaitForRetryAfterAsync(cancelToken).ConfigureAwait(false);
                using (var request = new HttpRequestMessage(HttpMethod.Delete, uploadSession.UploadUrl))
                using (var response = await Utility.Utility.WithTimeout(m_timeouts.ShortTimeout, cancelToken,
                    ct => m_client.SendAsync(request, false, ct)).ConfigureAwait(false))
                    CheckResponse(response);

                dnsNames = new[] {
                        new System.Uri(BASE_ADDRESS).Host,
                        string.IsNullOrWhiteSpace(uploadSession.UploadUrl) ? null : new System.Uri(uploadSession.UploadUrl).Host,
                    }
                    .WhereNotNullOrWhiteSpace()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            return dnsNames;
        }

        public async Task<IQuotaInfo?> GetQuotaInfoAsync(CancellationToken cancelToken)
        {
            var drivePrefix = await GetDrivePrefix(cancelToken).ConfigureAwait(false);
            var driveInfo = await Utility.Utility.WithTimeout(m_timeouts.ShortTimeout, cancelToken,
                ct => GetAsync<Drive>(drivePrefix, ct)
            ).ConfigureAwait(false);

            if (driveInfo?.Quota != null)
            {
                // Some sources (SharePoint for example) seem to return 0 for these values even when the quota isn't exceeded..
                // As a special test, if all the returned values are 0, we pretend that no quota was reported.
                // This way we don't send spurious warnings because the quota looks like it is exceeded.
                if (driveInfo.Quota.Total != 0 || driveInfo.Quota.Remaining != 0 || driveInfo.Quota.Used != 0)
                    return new QuotaInfo(driveInfo.Quota.Total, driveInfo.Quota.Remaining);
            }

            return null;

        }

        /// <summary>
        /// Override-able fragment indicating the API version to use each query
        /// </summary>
        protected virtual string ApiVersion => "/v1.0";

        /// <summary>
        /// Normalized fragment (starting with a slash and ending without one) for the path to the drive to be used.
        /// For example: "/me/drive" for the default drive for a user.
        /// </summary>
        protected abstract Task<string> GetDrivePath(CancellationToken cancelToken);

        protected abstract DescriptionTemplateDelegate DescriptionTemplate { get; }

        protected virtual IList<ICommandLineArgument> AdditionalSupportedCommands => [];

        private async Task<string> GetDrivePrefix(CancellationToken cancelToken)
            => $"{ApiVersion}{await GetDrivePath(cancelToken).ConfigureAwait(false)}";

        public async Task CreateFolderAsync(CancellationToken cancelToken)
        {
            var parentFolder = "root";
            var parentFolderPath = string.Empty;
            var drivePrefix = await GetDrivePrefix(cancelToken).ConfigureAwait(false);

            foreach (string folder in this.RootPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string nextPath = parentFolderPath + "/" + folder;
                DriveItem folderItem;
                try
                {
                    folderItem = await Utility.Utility.WithTimeout(m_timeouts.ShortTimeout, cancelToken,
                        ct => GetAsync<DriveItem>(string.Format("{0}/root:{1}", drivePrefix, NormalizeSlashes(nextPath)), ct)
                    ).ConfigureAwait(false);
                }
                catch (DriveItemNotFoundException)
                {
                    var newFolder = new DriveItem()
                    {
                        Name = folder,
                        Folder = new FolderFacet(),
                    };

                    folderItem = await Utility.Utility.WithTimeout(m_timeouts.ShortTimeout, cancelToken,
                        ct => PostAsync(string.Format("{0}/items/{1}/children", drivePrefix, parentFolder), newFolder, ct)
                    ).ConfigureAwait(false);
                }

                parentFolder = folderItem.Id;
                parentFolderPath = nextPath;
            }
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<IFileEntry> ListAsync([EnumeratorCancellation] CancellationToken cancelToken)
        {
            var drivePrefix = await GetDrivePrefix(cancelToken).ConfigureAwait(false);
            await foreach (var item in this.Enumerate<DriveItem>(string.Format("{0}/root:{1}:/children", drivePrefix, this.RootPath), cancelToken).ConfigureAwait(false))
            {
                // Exclude non-files and deleted items (not sure if they show up in this listing, but make sure anyway)
                if (item.IsFile && !item.IsDeleted)
                {
                    yield return new FileEntry(
                        item.Name,
                        item.Size ?? 0, // Files should always have a size, but folders don't need it
                        item.FileSystemInfo?.LastAccessedDateTime?.UtcDateTime ?? new DateTime(),
                        item.FileSystemInfo?.LastModifiedDateTime?.UtcDateTime ?? item.LastModifiedDateTime?.UtcDateTime ?? new DateTime());
                }
            }
        }

        public async Task GetAsync(string remotename, string filename, CancellationToken cancelToken)
        {
            using (var fileStream = File.OpenWrite(filename))
                await GetAsync(remotename, fileStream, cancelToken).ConfigureAwait(false);
        }

        public async Task GetAsync(string remotename, Stream stream, CancellationToken cancelToken)
        {
            try
            {
                var drivePrefix = await GetDrivePrefix(cancelToken).ConfigureAwait(false);
                m_retryAfter.WaitForRetryAfter();
                string getUrl = string.Format("{0}/root:{1}{2}:/content", drivePrefix, this.RootPath, NormalizeSlashes(remotename));
                using (var response = await Utility.Utility.WithTimeout(m_timeouts.ShortTimeout, cancelToken,
                    ct => m_client.GetAsync(getUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                ).ConfigureAwait(false))
                {
                    CheckResponse(response);
                    using (var responseStream = await Utility.Utility.WithTimeout(m_timeouts.ShortTimeout, cancelToken,
                        ct => response.Content.ReadAsStreamAsync(ct)).ConfigureAwait(false))
                    using (var timeoutSream = responseStream.ObserveReadTimeout(m_timeouts.ReadWriteTimeout))
                        await Utility.Utility.CopyStreamAsync(responseStream, stream, cancelToken).ConfigureAwait(false);
                }
            }
            catch (DriveItemNotFoundException ex)
            {
                // If the item wasn't found, wrap the exception so normal handling can occur.
                throw new FileMissingException(ex);
            }
        }

        public async Task RenameAsync(string oldname, string newname, CancellationToken cancelToken)
        {
            try
            {
                var drivePrefix = await GetDrivePrefix(cancelToken).ConfigureAwait(false);
                await Utility.Utility.WithTimeout(m_timeouts.ShortTimeout, cancelToken,
                    ct => PatchAsync(string.Format("{0}/root:{1}{2}", drivePrefix, this.RootPath, NormalizeSlashes(oldname)), new DriveItem() { Name = newname }, ct)
                ).ConfigureAwait(false);
            }
            catch (DriveItemNotFoundException ex)
            {
                // If the item wasn't found, wrap the exception so normal handling can occur.
                throw new FileMissingException(ex);
            }
        }

        public async Task PutAsync(string remotename, string filename, CancellationToken cancelToken)
        {
            using (FileStream fileStream = File.OpenRead(filename))
                await PutAsync(remotename, fileStream, cancelToken).ConfigureAwait(false);
        }

        public async Task PutAsync(string remotename, Stream stream, CancellationToken cancelToken)
        {
            var drivePrefix = await GetDrivePrefix(cancelToken).ConfigureAwait(false);

            // PUT only supports up to 4 MB file uploads. There's a separate process for larger files.
            if (stream.Length < PUT_MAX_SIZE)
            {
                await m_retryAfter.WaitForRetryAfterAsync(cancelToken).ConfigureAwait(false);
                string putUrl = string.Format("{0}/root:{1}{2}:/content", drivePrefix, this.RootPath, NormalizeSlashes(remotename));
                using (var timeoutStream = stream.ObserveReadTimeout(m_timeouts.ReadWriteTimeout, false))
                using (var streamContent = new StreamContent(timeoutStream))
                {
                    streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    using (var response = await this.m_client.PutAsync(putUrl, streamContent, cancelToken).ConfigureAwait(false))
                    {
                        // Make sure this response is a valid drive item, though we don't actually use it for anything currently.
                        await this.ParseResponseAsync<DriveItem>(response, cancelToken).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                // This file is too large to be sent in a single request, so we need to send it in pieces in an upload session:
                // https://docs.microsoft.com/en-us/onedrive/developer/rest-api/api/driveitem_createuploadsession
                // The documentation seems somewhat contradictory - it states that uploads must be done sequentially,
                // but also states that the nextExpectedRanges value returned may indicate multiple ranges...
                // For now, this plays it safe and does a sequential upload.
                string createSessionUrl = string.Format("{0}/root:{1}{2}:/createUploadSession", drivePrefix, this.RootPath, NormalizeSlashes(remotename));
                await m_retryAfter.WaitForRetryAfterAsync(cancelToken).ConfigureAwait(false);
                using (var createSessionRequest = new HttpRequestMessage(HttpMethod.Post, createSessionUrl))
                using (var createSessionResponse = await Utility.Utility.WithTimeout(m_timeouts.ShortTimeout, cancelToken, ct => this.m_client.SendAsync(createSessionRequest, ct)).ConfigureAwait(false))
                {
                    var uploadSession = await this.ParseResponseAsync<UploadSession>(createSessionResponse, cancelToken).ConfigureAwait(false);

                    // If the stream's total length is less than the chosen fragment size, then we should make the buffer only as large as the stream.
                    int bufferSize = (int)Math.Min(this.fragmentSize, stream.Length);

                    long read = 0;
                    for (long offset = 0; offset < stream.Length; offset += read)
                    {
                        // If the stream isn't long enough for this to be a full buffer, then limit the length
                        long currentBufferSize = bufferSize;
                        if (stream.Length < offset + bufferSize)
                        {
                            currentBufferSize = stream.Length - offset;
                        }

                        using (var subStream = new ReadLimitLengthStream(stream, offset, currentBufferSize))
                        {
                            read = subStream.Length;

                            int fragmentCount = (int)Math.Ceiling((double)stream.Length / bufferSize);
                            int retryCount = this.fragmentRetryCount;
                            for (int attempt = 0; attempt < retryCount; attempt++)
                            {
                                await m_retryAfter.WaitForRetryAfterAsync(cancelToken).ConfigureAwait(false);

                                int fragmentNumber = (int)(offset / bufferSize);
                                Log.WriteVerboseMessage(
                                    LOGTAG,
                                    "MicrosoftGraphFragmentUpload",
                                    "Uploading fragment {0}/{1} of remote file {2}",
                                    fragmentNumber,
                                    fragmentCount,
                                    remotename);

                                using (var request = new HttpRequestMessage(HttpMethod.Put, uploadSession.UploadUrl))
                                using (var timeoutStream = subStream.ObserveReadTimeout(m_timeouts.ReadWriteTimeout, false))
                                using (var fragmentContent = new StreamContent(timeoutStream))
                                {
                                    fragmentContent.Headers.ContentLength = read;
                                    fragmentContent.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + read - 1, stream.Length);

                                    request.Content = fragmentContent;

                                    try
                                    {
                                        // The uploaded put requests will error if they are authenticated
                                        using (var response = await Utility.Utility.WithTimeout(m_timeouts.ShortTimeout, cancelToken, ct => this.m_client.SendAsync(request, false, ct)).ConfigureAwait(false))
                                        {
                                            // Note: On the last request, the json result includes the default properties of the item that was uploaded
                                            await this.ParseResponseAsync<UploadSession>(response, cancelToken).ConfigureAwait(false);
                                        }
                                    }
                                    catch (MicrosoftGraphException ex)
                                    {
                                        if (subStream.Position != 0)
                                        {
                                            if (subStream.CanSeek)
                                            {
                                                // Make sure to reset the substream to its start in case this is a retry
                                                subStream.Seek(0, SeekOrigin.Begin);
                                            }
                                            else
                                            {
                                                // If any of the source stream was read and the substream can't be seeked back to the beginning,
                                                // then the internal retry mechanism can't be used and the caller will have to retry this whole file.
                                                // Should we consider signaling to the graph API that we're abandoning this upload session?
                                                await this.ThrowUploadSessionException(
                                                    uploadSession,
                                                    createSessionResponse,
                                                    fragmentNumber,
                                                    fragmentCount,
                                                    ex,
                                                    cancelToken).ConfigureAwait(false);
                                            }
                                        }

                                        // Error handling based on recommendations here:
                                        // https://docs.microsoft.com/en-us/onedrive/developer/rest-api/api/driveitem_createuploadsession#best-practices
                                        if (attempt >= retryCount - 1)
                                        {
                                            // We've used up all our retry attempts
                                            await this.ThrowUploadSessionException(
                                                uploadSession,
                                                createSessionResponse,
                                                fragmentNumber,
                                                fragmentCount,
                                                ex,
                                                cancelToken).ConfigureAwait(false);
                                        }
                                        else if ((int)ex.StatusCode >= 500 && (int)ex.StatusCode < 600)
                                        {
                                            // If a 5xx error code is hit, we should use an exponential backoff strategy before retrying.
                                            // To make things simpler, we just use the current attempt number as the exponential factor.
                                            // If there was a Retry-After header, we'll wait for that right before sending the next request as well.
                                            TimeSpan delay = TimeSpan.FromMilliseconds((int)Math.Pow(2, attempt) * this.fragmentRetryDelay);

                                            Log.WriteRetryMessage(
                                                LOGTAG,
                                                "MicrosoftGraphFragmentRetryIn",
                                                ex,
                                                "Uploading fragment {0}/{1} of remote file {2} failed and will be retried in {3}",
                                                fragmentNumber,
                                                fragmentCount,
                                                remotename,
                                                delay);

                                            await Task.Delay(delay).ConfigureAwait(false);
                                            continue;
                                        }
                                        else if (ex.StatusCode == HttpStatusCode.NotFound)
                                        {
                                            // 404 is a special case indicating the upload session no longer exists, so the fragment shouldn't be retried.
                                            // Instead we'll let the caller re-attempt the whole file.
                                            await this.ThrowUploadSessionException(
                                                uploadSession,
                                                createSessionResponse,
                                                fragmentNumber,
                                                fragmentCount,
                                                ex,
                                                cancelToken).ConfigureAwait(false);
                                        }
                                        else if ((int)ex.StatusCode >= 400 && (int)ex.StatusCode < 500)
                                        {
                                            // If a 4xx error code is hit, we should retry without the exponential backoff attempt.
                                            Log.WriteRetryMessage(
                                                LOGTAG,
                                                "MicrosoftGraphFragmentRetry",
                                                ex,
                                                "Uploading fragment {0}/{1} of remote file {2} failed and will be retried",
                                                fragmentNumber,
                                                fragmentCount,
                                                remotename);

                                            continue;
                                        }
                                        else
                                        {
                                            // Other errors should be rethrown
                                            await this.ThrowUploadSessionException(
                                                uploadSession,
                                                createSessionResponse,
                                                fragmentNumber,
                                                fragmentCount,
                                                ex,
                                                cancelToken).ConfigureAwait(false);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        // Any other exceptions should also cause the upload session to be canceled.
                                        await this.ThrowUploadSessionException(
                                            uploadSession,
                                            createSessionResponse,
                                            fragmentNumber,
                                            fragmentCount,
                                            ex,
                                            cancelToken).ConfigureAwait(false);
                                    }

                                    // If we successfully sent this piece, then we can break out of the retry loop
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        public async Task DeleteAsync(string remotename, CancellationToken cancelToken)
        {
            try
            {
                var drivePrefix = await GetDrivePrefix(cancelToken).ConfigureAwait(false);
                await m_retryAfter.WaitForRetryAfterAsync(cancelToken).ConfigureAwait(false);
                string deleteUrl = string.Format("{0}/root:{1}{2}", drivePrefix, this.RootPath, NormalizeSlashes(remotename));
                using (var response = await Utility.Utility.WithTimeout(m_timeouts.ShortTimeout, cancelToken, ct => this.m_client.DeleteAsync(deleteUrl, ct)).ConfigureAwait(false))
                    this.CheckResponse(response);
            }
            catch (DriveItemNotFoundException ex)
            {
                // Wrap the existing item not found error in a 'FolderMissingException'
                throw new FileMissingException(ex);
            }
        }

        public async Task TestAsync(CancellationToken cancelToken)
        {
            try
            {
                var drivePrefix = await GetDrivePrefix(cancelToken).ConfigureAwait(false);
                string rootPath = string.Format("{0}/root:{1}", drivePrefix, this.RootPath);
                await this.GetAsync<DriveItem>(rootPath, cancelToken).ConfigureAwait(false);
            }
            catch (DriveItemNotFoundException ex)
            {
                // Wrap the existing item not found error in a 'FolderMissingException'
                throw new FolderMissingException(ex);
            }

            await this.TestReadWritePermissionsAsync(cancelToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (this.m_client != null)
            {
                this.m_client.Dispose();
            }
        }

        protected virtual Task<string> GetRootPathFromUrlAsync(string url, CancellationToken cancelToken)
        {
            // Extract out the path to the backup root folder from the given URI
            var uri = new Utility.Uri(url);

            return Task.FromResult(Utility.Uri.UrlDecode(uri.HostAndPath));
        }

        protected Task<T> GetAsync<T>(string url, CancellationToken cancelToken)
        {
            return this.SendRequestAsync<T>(HttpMethod.Get, url, cancelToken);
        }

        protected Task<T> PostAsync<T>(string url, T body, CancellationToken cancelToken) where T : class
        {
            return this.SendRequestAsync(HttpMethod.Post, url, body, cancelToken);
        }

        protected Task<T> PatchAsync<T>(string url, T body, CancellationToken cancelToken) where T : class
        {
            return this.SendRequestAsync(PatchMethod, url, body, cancelToken);
        }

        private async Task<T> SendRequestAsync<T>(HttpMethod method, string url, CancellationToken cancelToken)
        {
            using (var request = new HttpRequestMessage(method, url))
            {
                return await this.SendRequestAsync<T>(request, cancelToken).ConfigureAwait(false);
            }
        }

        private async Task<T> SendRequestAsync<T>(HttpMethod method, string url, T body, CancellationToken cancelToken) where T : class
        {
            using (var request = new HttpRequestMessage(method, url))
            using (request.Content = this.PrepareContent(body))
            {
                return await this.SendRequestAsync<T>(request, cancelToken).ConfigureAwait(false);
            }
        }

        private async Task<T> SendRequestAsync<T>(HttpRequestMessage request, CancellationToken cancelToken)
        {
            await m_retryAfter.WaitForRetryAfterAsync(cancelToken).ConfigureAwait(false);
            using (var response = await this.m_client.SendAsync(request, cancelToken).ConfigureAwait(false))
            {
                return await this.ParseResponseAsync<T>(response, cancelToken).ConfigureAwait(false);
            }
        }

        private async IAsyncEnumerable<T> Enumerate<T>(string url, [EnumeratorCancellation] CancellationToken cancelToken)
        {
            var nextUrl = url;
            while (!string.IsNullOrEmpty(nextUrl))
            {
                GraphCollection<T> results;
                try
                {
                    results = await Utility.Utility.WithTimeout(m_timeouts.ListTimeout, cancelToken, ct => this.GetAsync<GraphCollection<T>>(nextUrl, ct)).ConfigureAwait(false);
                }
                catch (DriveItemNotFoundException ex)
                {
                    // If there's an 'item not found' exception here, it means the root folder didn't exist.
                    throw new FolderMissingException(ex);
                }

                foreach (T result in results.Value ?? [])
                    yield return result;

                nextUrl = results.ODataNextLink;
            }
        }

        private void CheckResponse(HttpResponseMessage response)
        {
            m_retryAfter.SetRetryAfter(response.Headers.RetryAfter);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // It looks like this is an 'item not found' exception, so wrap it in a new exception class to make it easier to pick things out.
                    throw new DriveItemNotFoundException(response);
                }
                else
                {
                    // Throw a wrapper exception to make it easier for the caller to look at specific status codes, etc.
                    throw new MicrosoftGraphException(response);
                }
            }
        }

        private async Task<T> ParseResponseAsync<T>(HttpResponseMessage response, CancellationToken cancelToken)
        {
            CheckResponse(response);
            using (var responseStream = await Utility.Utility.WithTimeout(m_timeouts.ShortTimeout, cancelToken, ct => response.Content.ReadAsStreamAsync(ct)).ConfigureAwait(false))
            using (var timeoutStream = responseStream.ObserveReadTimeout(m_timeouts.ReadWriteTimeout))
            using (var reader = new StreamReader(timeoutStream))
            using (var jsonReader = new JsonTextReader(reader))
                return m_serializer.Deserialize<T>(jsonReader)
                    ?? throw new MicrosoftGraphException("Failed to parse response", response);
        }

        private async Task ThrowUploadSessionException(
            UploadSession uploadSession,
            HttpResponseMessage createSessionResponse,
            int fragment,
            int fragmentCount,
            Exception ex,
            CancellationToken cancelToken)
        {
            // Before throwing the exception, cancel the upload session
            // The uploaded delete request will error if it is authenticated
            using (var request = new HttpRequestMessage(HttpMethod.Delete, uploadSession.UploadUrl))
            using (var response = await this.m_client.SendAsync(request, false, cancelToken).ConfigureAwait(false))
            {
                // Note that the response body should always be empty in this case.
                await this.ParseResponseAsync<UploadSession>(response, cancelToken).ConfigureAwait(false);
            }

            throw new UploadSessionException(createSessionResponse, fragment, fragmentCount, ex);
        }

        /// <summary>
        /// Normalizes the slashes in a url fragment. For example:
        ///   "" => ""
        ///   "test" => "/test"
        ///   "test/" => "/test"
        ///   "a\b" => "/a/b"
        /// </summary>
        /// <param name="url">Url fragment to normalize</param>
        /// <returns>Normalized fragment</returns>
        private static string NormalizeSlashes(string url)
        {
            url = url.Replace('\\', '/');

            if (url.Length != 0 && !url.StartsWith("/", StringComparison.Ordinal))
                url = "/" + url;

            if (url.EndsWith("/", StringComparison.Ordinal))
                url = url.Substring(0, url.Length - 1);

            return url;
        }

        private StringContent? PrepareContent<T>(T body)
        {
            if (body == null)
                return null;
            return new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
        }
    }
}
