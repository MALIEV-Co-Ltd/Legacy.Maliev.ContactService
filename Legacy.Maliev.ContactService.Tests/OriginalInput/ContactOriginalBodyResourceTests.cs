using System.Text;
using Legacy.Maliev.ContactService.Tests.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.ContactService.Tests.OriginalInput;

[Collection("Contact original body")]
public sealed class ContactOriginalBodyResourceTests(ContactRuntimePostgresFixture postgres)
    : IClassFixture<ContactRuntimePostgresFixture>
{
    private static byte[] LargeBody() => Encoding.UTF8.GetBytes(
        "{\"FirstName\":\"N\",\"MessageContent\":\"" + new string('ก', 40000) + "\"}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FragmentedSpill_PreservesProjectionAndReleasesOnlyOwnedBuffer(bool candidate)
    {
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var body = new FragmentedInput(LargeBody());
        var environment = new ReadEnvironment(body, ModelType(candidate), Metadata(factory.Services, candidate));
        try
        {
            var formatter = Formatter(factory.Services, candidate);
            Assert.True(formatter.CanRead(environment.Context));
            var result = await formatter.ReadAsync(environment.Context);
            Assert.True(result.IsModelSet);
            Assert.False(result.HasError);
            Assert.NotNull(result.Model);
            Assert.Empty(environment.State);
            Assert.Equal("N", candidate
                ? Assert.IsType<Legacy.Maliev.ContactService.Application.Models.UpsertContactRequestRequest>(result.Model).FirstName
                : Assert.IsType<SourceMessage>(result.Model).FirstName);
            var buffer = Assert.Single(environment.Buffers);
            var path = Assert.Single(environment.Paths);
            Assert.False(body.DisposedByFormatter);
            if (candidate)
            {
                Assert.Same(body, environment.Http.Request.Body);
                Assert.False(buffer.CanRead);
                Assert.False(File.Exists(path));
                Assert.Empty(environment.Completion.Buffers);
            }
            else
            {
                // Stock disposes a successfully drained buffer in its deserialize
                // finally; response completion remains the early-drain fallback.
                Assert.False(buffer.CanRead);
                Assert.False(File.Exists(path));
                Assert.Single(environment.Completion.Buffers);
            }
            await environment.Completion.CompleteAsync();
            environment.AssertReleased();
            Assert.False(body.DisposedByFormatter);
            Assert.False(body.DisposedByFormatter);
        }
        finally
        {
            await environment.CleanupAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledRequest_PinsIntentionalOwnedDrainDifferenceWithoutOwningCaller(bool candidate)
    {
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var body = new FragmentedInput(LargeBody());
        var environment = new ReadEnvironment(body, ModelType(candidate), Metadata(factory.Services, candidate), cancellation.Token);
        try
        {
            var formatter = Formatter(factory.Services, candidate);
            Assert.True(formatter.CanRead(environment.Context));
            if (candidate)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => formatter.ReadAsync(environment.Context));
                Assert.Equal(0, body.BytesRead);
                Assert.Same(body, environment.Http.Request.Body);
                Assert.Empty(environment.Completion.Buffers);
                Assert.NotEmpty(environment.Buffers); // Captured when RequestAborted is consumed.
                environment.AssertReleased();
                Assert.False(body.DisposedByFormatter);
            }
            else
            {
                var result = await formatter.ReadAsync(environment.Context);
                Assert.True(result.IsModelSet);
                Assert.False(result.HasError);
                Assert.Equal(body.ByteLength, body.BytesRead);
                Assert.False(body.CancellableTokenObserved); // Original stock drain uses None.
            }
            Assert.False(body.DisposedByFormatter);
            await environment.Completion.CompleteAsync();
            environment.AssertReleased();
            Assert.False(body.DisposedByFormatter);
        }
        finally
        {
            await environment.CleanupAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CandidateDrainFaultOrSizeLimit_ReleasesSpillRestoresBodyAndDoesNotBecomeMalformed400(bool fault)
    {
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var body = new FragmentedInput(LargeBody(), fault ? 40000 : int.MaxValue);
        var environment = new ReadEnvironment(body, ContactOriginalInputOracle.DtoType, Metadata(factory.Services, true));
        if (!fault) environment.Http.Features.Set<IHttpMaxRequestBodySizeFeature>(new RequestLimit { MaxRequestBodySize = 40000 });
        try
        {
            await Assert.ThrowsAsync<IOException>(() => Formatter(factory.Services, true).ReadAsync(environment.Context));
            Assert.Same(body, environment.Http.Request.Body);
            Assert.False(body.DisposedByFormatter);
            Assert.Single(environment.Buffers);
            Assert.Single(environment.Paths);
            Assert.Empty(environment.Completion.Buffers);
            environment.AssertReleased();
            Assert.False(body.DisposedByFormatter);
        }
        finally
        {
            await environment.CleanupAsync();
        }
    }

    [Fact]
    public async Task CharsetFailurePrecedesBodyRead_AndSuccessfulSeekableInputReturnsToCallerPosition()
    {
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        var formatter = Formatter(factory.Services, true);
        using (var body = new FragmentedInput(Encoding.UTF8.GetBytes("{}")))
        {
            var invalid = new ReadEnvironment(body, ContactOriginalInputOracle.DtoType, Metadata(factory.Services, true));
            invalid.Http.Request.ContentType = "application/json; charset=private-invalid-charset";
            try
            {
                Assert.True(formatter.CanRead(invalid.Context));
                var result = await formatter.ReadAsync(invalid.Context);
                Assert.True(result.HasError);
                Assert.Equal(0, body.BytesRead);
                Assert.Empty(invalid.Buffers);
                Assert.Same(body, invalid.Http.Request.Body);
                Assert.False(body.DisposedByFormatter);
            }
            finally
            {
                await invalid.CleanupAsync();
            }
        }
        using var seekable = new MemoryStream(Encoding.UTF8.GetBytes("SKIP{\"FirstName\":\"N\"}"), writable: false);
        seekable.Position = 4;
        var successful = new ReadEnvironment(seekable, ContactOriginalInputOracle.DtoType, Metadata(factory.Services, true));
        try
        {
            var result = await formatter.ReadAsync(successful.Context);
            Assert.True(result.IsModelSet);
            Assert.False(result.HasError);
            Assert.Equal(4, seekable.Position);
            Assert.True(seekable.CanRead);
            Assert.Same(seekable, successful.Http.Request.Body);
            successful.AssertReleased();
        }
        finally
        {
            await successful.CleanupAsync();
        }
    }

    [Fact]
    public async Task InvalidUtf8_PreservesUnexpectedDecoderFaultAndOwnedCleanupOnBothReaders()
    {
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        foreach (var candidate in new[] { false, true })
        {
            using var body = new FragmentedInput([0xff]);
            var environment = new ReadEnvironment(body, ModelType(candidate), Metadata(factory.Services, candidate));
            try
            {
                await Assert.ThrowsAsync<DecoderFallbackException>(() => Formatter(factory.Services, candidate).ReadAsync(environment.Context));
                Assert.False(body.DisposedByFormatter);
                if (candidate) Assert.Same(body, environment.Http.Request.Body);
                await environment.Completion.CompleteAsync();
                environment.AssertReleased();
                Assert.False(body.DisposedByFormatter);
            }
            finally
            {
                await environment.CleanupAsync();
            }
        }
    }

    private static Type ModelType(bool candidate) => candidate
        ? ContactOriginalInputOracle.DtoType : ContactOriginalInputOracle.SourceType;

    private static IModelMetadataProvider Metadata(IServiceProvider services, bool candidate) => candidate
        ? services.GetRequiredService<IModelMetadataProvider>() : new EmptyModelMetadataProvider();

    private static IInputFormatter Formatter(IServiceProvider services, bool candidate) => candidate
        ? services.GetRequiredService<IOptions<MvcOptions>>().Value.InputFormatters
            .Single(value => value.GetType().FullName == ContactOriginalBodyNative.FormatterName)
        : ContactOriginalInputOracle.Formatter();

    private sealed class ReadEnvironment
    {
        private readonly HashSet<FileBufferingReadStream> observed = [];
        private readonly HashSet<string> paths = [];
        internal DefaultHttpContext Http { get; } = new();
        internal CompletionFeature Completion { get; } = new();
        internal ModelStateDictionary State { get; } = new();
        internal InputFormatterContext Context { get; }
        internal FileBufferingReadStream[] Buffers { get { Observe(); return observed.ToArray(); } }
        internal string[] Paths { get { Observe(); return paths.ToArray(); } }

        internal ReadEnvironment(Stream body, Type type, IModelMetadataProvider metadata, CancellationToken cancellation = default)
        {
            Http.Features.Set<IHttpResponseFeature>(Completion);
            Http.Features.Set<IHttpRequestLifetimeFeature>(new LifetimeFeature(cancellation, Observe));
            Http.Request.Body = body;
            Http.Request.ContentType = "application/json";
            Http.Request.ContentLength = body is FragmentedInput fragmented ? fragmented.ByteLength : body.Length - body.Position;
            if (body is FragmentedInput input) input.Observe = Observe;
            Context = new(Http, "", State, metadata.GetMetadataForType(type),
                static (stream, encoding) => new HttpRequestStreamReader(stream, encoding), false);
        }

        private void Observe()
        {
            if (Http.Request.Body is FileBufferingReadStream buffer) observed.Add(buffer);
            foreach (var value in Completion.Buffers) observed.Add(value);
            foreach (var value in observed)
                if (value.TempFileName is { } path) paths.Add(path);
        }

        internal async Task CleanupAsync()
        {
            // Rescue only exact buffers observed in this test after assertions;
            // this cannot turn a failed ownership assertion into a passing test.
            List<Exception> failures = [];
            try { await Completion.CompleteAsync(); }
            catch (Exception failure) { failures.Add(failure); }
            foreach (var buffer in Buffers)
            {
                try { await buffer.DisposeAsync(); }
                catch (Exception failure) { failures.Add(failure); }
            }
            try { AssertReleased(); }
            catch (Exception failure) { failures.Add(failure); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }

        internal void AssertReleased()
        {
            foreach (var buffer in Buffers)
            {
                Assert.False(buffer.CanRead);
            }
            foreach (var path in Paths) Assert.False(File.Exists(path));
            // Paths were observed during draining, before disposal.
        }
    }

    private sealed class RequestLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    private sealed class LifetimeFeature(CancellationToken token, Action observe) : IHttpRequestLifetimeFeature
    {
        private CancellationToken value = token;
        public CancellationToken RequestAborted
        {
            get { observe(); return value; }
            set { this.value = value; }
        }
        public void Abort() { }
    }

    private sealed class CompletionFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> callbacks = [];
        private readonly List<FileBufferingReadStream> owned = [];
        internal FileBufferingReadStream[] Buffers => owned.ToArray();
        public override void OnCompleted(Func<object, Task> callback, object state)
        {
            callbacks.Add((callback, state));
            if (state is FileBufferingReadStream buffer) owned.Add(buffer);
        }
        internal async Task CompleteAsync()
        {
            var pending = callbacks.ToArray();
            callbacks.Clear();
            List<Exception> failures = [];
            foreach (var item in pending.Reverse())
            {
                try { await item.Callback(item.State); }
                catch (Exception failure) { failures.Add(failure); }
            }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
    }

    private sealed class FragmentedInput(byte[] bytes, int throwAfter = int.MaxValue) : Stream
    {
        private readonly MemoryStream input = new(bytes, writable: false);
        internal Action? Observe { get; set; }
        internal long ByteLength => bytes.LongLength;
        internal long BytesRead { get; private set; }
        internal bool CancellableTokenObserved { get; private set; }
        internal bool DisposedByFormatter { get; private set; }
        public override bool CanRead => !DisposedByFormatter;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            Observe?.Invoke();
            if (input.Position >= throwAfter) throw new IOException("controlled-input-fault");
            var read = input.Read(buffer, offset, Math.Min(count, 1024));
            BytesRead += read;
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            CancellableTokenObserved |= cancellationToken.CanBeCanceled;
            Observe?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            CancellableTokenObserved |= cancellationToken.CanBeCanceled;
            Observe?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (input.Position >= throwAfter) throw new IOException("controlled-input-fault");
            var read = input.Read(buffer.Span[..Math.Min(buffer.Length, 1024)]);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { DisposedByFormatter = true; input.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
