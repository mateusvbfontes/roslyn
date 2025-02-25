﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis
{
    /// <summary>
    /// A recoverable TextAndVersion source that saves its text to temporary storage.
    /// </summary>
    internal sealed class RecoverableTextAndVersion : ITextVersionable, ITextAndVersionSource
    {
        private readonly SolutionServices _services;

        // Starts as ITextAndVersionSource and is replaced with RecoverableText when the TextAndVersion value is requested.
        // At that point the initial source is no longer referenced and can be garbage collected.
        private object _initialSourceOrRecoverableText;

        public bool CanReloadText { get; }

        public RecoverableTextAndVersion(ITextAndVersionSource initialSource, SolutionServices services)
        {
            _initialSourceOrRecoverableText = initialSource;
            _services = services;
            CanReloadText = initialSource.CanReloadText;
        }

        /// <returns>
        /// True if the <paramref name="source"/> is available, false if <paramref name="text"/> is returned.
        /// </returns>
        private bool TryGetInitialSourceOrRecoverableText([NotNullWhen(true)] out ITextAndVersionSource? source, [NotNullWhen(false)] out RecoverableText? text)
        {
            // store to local to avoid race:
            var sourceOrRecoverableText = _initialSourceOrRecoverableText;

            source = sourceOrRecoverableText as ITextAndVersionSource;
            if (source != null)
            {
                text = null;
                return true;
            }

            text = (RecoverableText)sourceOrRecoverableText;
            return false;
        }

        public ITemporaryTextStorageInternal? Storage
            => (_initialSourceOrRecoverableText as RecoverableText)?.Storage;

        public bool TryGetValue(LoadTextOptions options, [MaybeNullWhen(false)] out TextAndVersion value)
        {
            if (TryGetInitialSourceOrRecoverableText(out var source, out var recoverableText))
            {
                return source.TryGetValue(options, out value);
            }

            if (recoverableText.TryGetValue(out var text) && recoverableText.LoadTextOptions == options)
            {
                value = TextAndVersion.Create(text, recoverableText.Version, recoverableText.LoadDiagnostic);
                return true;
            }

            value = null;
            return false;
        }

        public bool TryGetTextVersion(LoadTextOptions options, out VersionStamp version)
        {
            if (_initialSourceOrRecoverableText is ITextVersionable textVersionable)
            {
                return textVersionable.TryGetTextVersion(options, out version);
            }

            if (TryGetValue(options, out var textAndVersion))
            {
                version = textAndVersion.Version;
                return true;
            }

            version = default;
            return false;
        }

        private async ValueTask<RecoverableText> GetRecoverableTextAsync(
            bool useAsync, LoadTextOptions options, CancellationToken cancellationToken)
        {
            if (_initialSourceOrRecoverableText is ITextAndVersionSource source)
            {
                // replace initial source with recoverable text if it hasn't been replaced already:
                var textAndVersion = useAsync
                    ? await source.GetValueAsync(options, cancellationToken).ConfigureAwait(false)
                    : source.GetValue(options, cancellationToken);

                Interlocked.CompareExchange(
                    ref _initialSourceOrRecoverableText,
                    value: new RecoverableText(source, textAndVersion, options, _services),
                    comparand: source);
            }

            // If we have a recoverable text but the options it was created for do not match the current options
            // and the initial source supports reloading, reload and replace the recoverable text.
            var recoverableText = (RecoverableText)_initialSourceOrRecoverableText;
            if (recoverableText.LoadTextOptions != options && recoverableText.InitialSource != null)
            {
                var textAndVersion = useAsync
                    ? await recoverableText.InitialSource.GetValueAsync(options, cancellationToken).ConfigureAwait(false)
                    : recoverableText.InitialSource.GetValue(options, cancellationToken);
                Interlocked.Exchange(
                    ref _initialSourceOrRecoverableText,
                    new RecoverableText(recoverableText.InitialSource, textAndVersion, options, _services));
            }

            return (RecoverableText)_initialSourceOrRecoverableText;
        }

        public TextAndVersion GetValue(LoadTextOptions options, CancellationToken cancellationToken)
        {
#pragma warning disable CA2012 // Use ValueTasks correctly
            var valueTask = GetRecoverableTextAsync(useAsync: false, options, cancellationToken);
#pragma warning restore CA2012 // Use ValueTasks correctly
            Contract.ThrowIfFalse(valueTask.IsCompleted, "GetRecoverableTextAsync should have completed synchronously since we passed 'useAsync: false'");
            var recoverableText = valueTask.GetAwaiter().GetResult();

            return recoverableText.ToTextAndVersion(recoverableText.GetValue(cancellationToken));
        }

        public async Task<TextAndVersion> GetValueAsync(LoadTextOptions options, CancellationToken cancellationToken)
        {
            var recoverableText = await GetRecoverableTextAsync(useAsync: true, options, cancellationToken).ConfigureAwait(false);
            return recoverableText.ToTextAndVersion(await recoverableText.GetValueAsync(cancellationToken).ConfigureAwait(false));
        }

        public async ValueTask<VersionStamp> GetVersionAsync(LoadTextOptions options, CancellationToken cancellationToken)
        {
            var recoverableText = await GetRecoverableTextAsync(useAsync: true, options, cancellationToken).ConfigureAwait(false);
            return recoverableText.Version;
        }

        private sealed class RecoverableText : WeaklyCachedRecoverableValueSource<SourceText>, ITextVersionable
        {
            private readonly ITemporaryStorageServiceInternal _storageService;
            public readonly VersionStamp Version;
            public readonly Diagnostic? LoadDiagnostic;
            public readonly ITextAndVersionSource? InitialSource;
            public readonly LoadTextOptions LoadTextOptions;

            public ITemporaryTextStorageInternal? _storage;

            public RecoverableText(ITextAndVersionSource source, TextAndVersion textAndVersion, LoadTextOptions options, SolutionServices services)
                : base(textAndVersion.Text)
            {
                _storageService = services.GetRequiredService<ITemporaryStorageServiceInternal>();

                Version = textAndVersion.Version;
                LoadDiagnostic = textAndVersion.LoadDiagnostic;
                LoadTextOptions = options;

                if (source.CanReloadText)
                {
                    // reloadable source must not cache results
                    Contract.ThrowIfTrue(source is LoadableTextAndVersionSource { CacheResult: true });

                    InitialSource = source;
                }
            }

            public TextAndVersion ToTextAndVersion(SourceText text)
                => TextAndVersion.Create(text, Version, LoadDiagnostic);

            public ITemporaryTextStorageInternal? Storage => _storage;

            protected override async Task<SourceText> RecoverAsync(CancellationToken cancellationToken)
            {
                Contract.ThrowIfNull(_storage);

                using (Logger.LogBlock(FunctionId.Workspace_Recoverable_RecoverTextAsync, cancellationToken))
                {
                    return await _storage.ReadTextAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            protected override SourceText Recover(CancellationToken cancellationToken)
            {
                Contract.ThrowIfNull(_storage);

                using (Logger.LogBlock(FunctionId.Workspace_Recoverable_RecoverText, cancellationToken))
                {
                    return _storage.ReadText(cancellationToken);
                }
            }

            protected override async Task SaveAsync(SourceText text, CancellationToken cancellationToken)
            {
                Contract.ThrowIfFalse(_storage == null); // Cannot save more than once

                var storage = _storageService.CreateTemporaryTextStorage();
                await storage.WriteTextAsync(text, cancellationToken).ConfigureAwait(false);

                // make sure write is done before setting _storage field
                Interlocked.CompareExchange(ref _storage, storage, null);
            }

            public bool TryGetTextVersion(LoadTextOptions options, out VersionStamp version)
            {
                version = Version;
                return options == LoadTextOptions;
            }
        }
    }
}
