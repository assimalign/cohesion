using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpFormFeature"/> implementation installed by
/// <see cref="HttpContextFormExtensions.Form"/> and <c>ReadFormAsync</c>
/// when no feature is present.
/// </summary>
/// <remarks>
/// <para>
/// PR-1 scaffold: <see cref="ReadFormAsync"/> currently installs an empty
/// <see cref="HttpFormCollection"/> when nothing has been pre-attached. A
/// follow-up PR ports the multipart / urlencoded parser into this package and
/// will read against the request body via <c>context.Request.Body</c>.
/// </para>
/// </remarks>
internal sealed class HttpFormFeature : IHttpFormFeature
{
    private readonly HttpRequest? _request;
    private readonly Endpoint? _endpoint;
    private HttpFormOptions _options;
    private Task<IHttpFormCollection>? _parsedFormTask;
    private MediaTypeHeaderValue? _formContentType; // null iff _form is null
    private IHttpFormCollection? _form;


    /// <summary>
    /// Initializes a new instance of <see cref="FormFeature"/>.
    /// </summary>
    /// <param name="form">The <see cref="IFormCollection"/> to use as the backing store.</param>
    public HttpFormFeature(IHttpFormCollection form)
    {
        ArgumentNullException.ThrowIfNull(form);

        _form = form;
        _formContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        _options = HttpFormOptions.Default;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="FormFeature"/>.
    /// </summary>
    /// <param name="request">The <see cref="HttpRequest"/>.</param>
    public HttpFormFeature(HttpRequest request)
        : this(request, HttpFormOptions.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="FormFeature"/>.
    /// </summary>
    /// <param name="request">The <see cref="HttpRequest"/>.</param>
    /// <param name="options">The <see cref="FormOptions"/>.</param>
    public HttpFormFeature(HttpRequest request, HttpFormOptions options)
        : this(request, options, null)
    {
    }

    internal HttpFormFeature(HttpRequest request, HttpFormOptions options, Endpoint? endpoint)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        _request = request;
        _options = options;
        _endpoint = endpoint;
    }


    public string Name => nameof(HttpFormFeature);


    public IHttpFormCollection? Form
    {
        get
        {
            HandleUncheckedAntiforgeryValidationFeature();
            return _form;
        }
        set
        {
            _parsedFormTask = null;
            _form = value;
            if (_form is null)
            {
                _formContentType = null;
            }
            else
            {
                _formContentType ??= new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            }
        }
    }

    public Task<IHttpFormCollection> ReadFormAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Form is not null)
        {
            return Task.FromResult(Form);
        }

        Form = new HttpFormCollection();
        return Task.FromResult(Form);
    }


    
    // Internal for testing.
    internal HttpFormOptions FormOptions => _options;

    private MediaTypeHeaderValue? ContentType
    {
        get
        {
            MediaTypeHeaderValue? mt = null;

            if (_request is not null)
            {
                _ = MediaTypeHeaderValue.TryParse(_request.Headers.ContentType, out mt);
            }

            if (_form is not null && mt is null)
            {
                mt = _formContentType;
            }

            return mt;
        }
    }

    /// <inheritdoc />
    public bool HasFormContentType
    {
        get
        {
            // Set directly
            if (Form != null)
            {
                return true;
            }

            if (_request is null)
            {
                return false;
            }

            var contentType = ContentType;
            return HasApplicationFormContentType(contentType) || HasMultipartFormContentType(contentType);
        }
    }

    internal bool HasInvalidAntiforgeryValidationFeature => ResolveHasInvalidAntiforgeryValidationFeature();



    /// <inheritdoc />
    public IHttpFormCollection ReadForm()
    {
        HandleUncheckedAntiforgeryValidationFeature();
        if (Form != null)
        {
            return Form;
        }

        if (!HasFormContentType)
        {
            throw new InvalidOperationException("This request does not have a Content-Type header. Forms are available from requests with bodies like POSTs and a form Content-Type of either application/x-www-form-urlencoded or multipart/form-data.");
        }

        // c.f., https://aka.ms/aspnet/forms-async
        return ReadFormAsync().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public Task<IHttpFormCollection> ReadFormAsync() => ReadFormAsync(CancellationToken.None);

    /// <inheritdoc />
    public Task<IHttpFormCollection> ReadFormAsync(CancellationToken cancellationToken)
    {
        HandleUncheckedAntiforgeryValidationFeature();
        // Avoid state machine and task allocation for repeated reads
        if (_parsedFormTask == null)
        {
            if (Form != null)
            {
                _parsedFormTask = Task.FromResult(Form);
            }
            else
            {
                _parsedFormTask = InnerReadFormAsync(cancellationToken);
            }
        }
        return _parsedFormTask;
    }

    private async Task<IHttpFormCollection> InnerReadFormAsync(CancellationToken cancellationToken)
    {
        if (_request is null)
        {
            throw new InvalidOperationException("Cannot read form from this request. Request is 'null'.");
        }

        HandleUncheckedAntiforgeryValidationFeature();
        _options = _endpoint is null ? _options : GetFormOptionsFromMetadata(_options, _endpoint);

        if (!HasFormContentType)
        {
            throw new InvalidOperationException("Incorrect Content-Type: " + _request.ContentType);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_request.ContentLength == 0)
        {
            return EmptyFOrm
        }

        if (_options.BufferBody)
        {
            _request.EnableRewind(_options.MemoryBufferThreshold, _options.BufferBodyLengthLimit);
        }

        FormCollection? formFields = null;
        FormFileCollection? files = null;

        // Some of these code paths use StreamReader which does not support cancellation tokens.
        using (cancellationToken.Register((state) => ((HttpContext)state!).Abort(), _request.HttpContext))
        {
            var contentType = ContentType;
            // Check the content-type
            if (HasApplicationFormContentType(contentType))
            {
                var encoding = FilterEncoding(contentType.Encoding);
                var formReader = new FormPipeReader(_request.BodyReader, encoding)
                {
                    ValueCountLimit = _options.ValueCountLimit,
                    KeyLengthLimit = _options.KeyLengthLimit,
                    ValueLengthLimit = _options.ValueLengthLimit,
                };
                formFields = new FormCollection(await formReader.ReadFormAsync(cancellationToken));
            }
            else if (HasMultipartFormContentType(contentType))
            {
                var formAccumulator = new KeyValueAccumulator();
                var sectionCount = 0;

                var boundary = GetBoundary(contentType, _options.MultipartBoundaryLengthLimit);
                var multipartReader = new MultipartReader(boundary, _request.Body)
                {
                    HeadersCountLimit = _options.MultipartHeadersCountLimit,
                    HeadersLengthLimit = _options.MultipartHeadersLengthLimit,
                    BodyLengthLimit = _options.MultipartBodyLengthLimit,
                };
                var section = await multipartReader.ReadNextSectionAsync(cancellationToken);
                while (section != null)
                {
                    sectionCount++;
                    if (sectionCount > _options.ValueCountLimit)
                    {
                        throw new InvalidDataException($"Form value count limit {_options.ValueCountLimit} exceeded.");
                    }
                    // Parse the content disposition here and pass it further to avoid reparsings
                    if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var contentDisposition))
                    {
                        throw new InvalidDataException("Form section has invalid Content-Disposition value: " + section.ContentDisposition);
                    }

                    if (contentDisposition.IsFileDisposition())
                    {
                        var fileSection = new FileMultipartSection(section, contentDisposition);

                        // Enable buffering for the file if not already done for the full body
                        section.EnableRewind(
                            _request.HttpContext.Response.RegisterForDispose,
                            _options.MemoryBufferThreshold, _options.MultipartBodyLengthLimit);

                        // Find the end
                        await section.Body.DrainAsync(cancellationToken);

                        var name = fileSection.Name;
                        var fileName = fileSection.FileName;

                        HttpFormFile file;
                        if (section.BaseStreamOffset.HasValue)
                        {
                            // Relative reference to buffered request body
                            file = new HttpFormFile(_request.Body, section.BaseStreamOffset.GetValueOrDefault(), section.Body.Length, name, fileName);
                        }
                        else
                        {
                            // Individually buffered file body
                            file = new FormFile(section.Body, 0, section.Body.Length, name, fileName);
                        }
                        file.Headers = new HeaderDictionary(section.Headers);

                        if (files == null)
                        {
                            files = new FormFileCollection();
                        }
                        files.Add(file);
                    }
                    else if (contentDisposition.IsFormDisposition())
                    {
                        var formDataSection = new FormMultipartSection(section, contentDisposition);

                        // Content-Disposition: form-data; name="key"
                        //
                        // value

                        // Do not limit the key name length here because the multipart headers length limit is already in effect.
                        var key = formDataSection.Name;
                        var value = await formDataSection.GetValueAsync(cancellationToken);

                        formAccumulator.Append(key, value);
                    }
                    else
                    {
                        // Ignore form sections with invalid content disposition
                    }

                    section = await multipartReader.ReadNextSectionAsync(cancellationToken);
                }

                if (formAccumulator.HasValues)
                {
                    formFields = new FormCollection(formAccumulator.GetResults(), files);
                }
            }
        }

        // Rewind so later readers don't have to.
        if (_request.Body.CanSeek)
        {
            _request.Body.Seek(0, SeekOrigin.Begin);
        }

        if (formFields != null)
        {
            Form = formFields;
        }
        else if (files != null)
        {
            Form = new FormCollection(null, files);
        }
        else
        {
            Form = FormCollection.Empty;
        }

        return Form;
    }

    private static Encoding FilterEncoding(Encoding? encoding)
    {
        // UTF-7 is insecure and should not be honored. UTF-8 will succeed for most cases.
        // https://learn.microsoft.com/dotnet/core/compatibility/syslib-warnings/syslib0001
        if (encoding == null || encoding.CodePage == 65000)
        {
            return Encoding.UTF8;
        }
        return encoding;
    }

    private static bool HasApplicationFormContentType([NotNullWhen(true)] MediaTypeHeaderValue? contentType)
    {
        // Content-Type: application/x-www-form-urlencoded; charset=utf-8
        return contentType != null && contentType.MediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMultipartFormContentType([NotNullWhen(true)] MediaTypeHeaderValue? contentType)
    {
        // Content-Type: multipart/form-data; boundary=----WebKitFormBoundarymx2fSWqWSd0OxQqq
        return contentType != null && contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase);
    }

    private bool ResolveHasInvalidAntiforgeryValidationFeature()
    {
        if (_request is null)
        {
            return false;
        }
        var hasInvokedMiddleware = _request.HttpContext.Items.ContainsKey("__AntiforgeryMiddlewareWithEndpointInvoked");
        var hasInvalidToken = _request.HttpContext.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false };
        return hasInvokedMiddleware && hasInvalidToken;
    }

    private void HandleUncheckedAntiforgeryValidationFeature()
    {
        if (HasInvalidAntiforgeryValidationFeature)
        {
            throw new InvalidOperationException("This form is being accessed with an invalid anti-forgery token. Validate the `IAntiforgeryValidationFeature` on the request before reading from the form.");
        }
    }

    // Content-Type: multipart/form-data; boundary="----WebKitFormBoundarymx2fSWqWSd0OxQqq"
    // The spec says 70 characters is a reasonable limit.
    private static string GetBoundary(MediaTypeHeaderValue contentType, int lengthLimit)
    {
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary);
        if (StringSegment.IsNullOrEmpty(boundary))
        {
            throw new InvalidDataException("Missing content-type boundary.");
        }
        if (boundary.Length > lengthLimit)
        {
            throw new InvalidDataException($"Multipart boundary length limit {lengthLimit} exceeded.");
        }
        return boundary.ToString();
    }

    private static HttpFormOptions GetFormOptionsFromMetadata(FormOptions baseFormOptions, Endpoint endpoint)
    {
        var formOptionsMetadatas = endpoint.Metadata
            .GetOrderedMetadata<IFormOptionsMetadata>();
        var metadataCount = formOptionsMetadatas.Count;
        if (metadataCount == 0)
        {
            return baseFormOptions;
        }
        var finalFormOptionsMetadata = new MutableFormOptionsMetadata(formOptionsMetadatas[metadataCount - 1]);
        for (int i = metadataCount - 2; i >= 0; i--)
        {
            formOptionsMetadatas[i].MergeWith(ref finalFormOptionsMetadata);
        }
        return finalFormOptionsMetadata.ResolveFormOptions(baseFormOptions);
    }
}
