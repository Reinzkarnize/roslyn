﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Preview;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Editor.Tagging;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.CodeAnalysis.Workspaces;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace Microsoft.CodeAnalysis.Diagnostics;

internal abstract partial class AbstractPushOrPullDiagnosticsTaggerProvider<TTag>
{
    /// <summary>
    /// Low level tagger responsible for producing specific diagnostics tags for some feature for some particular <see
    /// cref="DiagnosticKind"/>.  It is itself never exported directly, but it it is used by the <see
    /// cref="PullDiagnosticsTaggerProvider"/> which aggregates its results and the results for all the other <see
    /// cref="DiagnosticKind"/> to produce all the diagnostics for that feature.
    /// </summary>
    private sealed class SingleDiagnosticKindPullTaggerProvider : AsynchronousTaggerProvider<TTag>
    {
        private readonly DiagnosticKind _diagnosticKind;
        private readonly IDiagnosticService _diagnosticService;
        private readonly IDiagnosticAnalyzerService _analyzerService;

        private readonly AbstractPushOrPullDiagnosticsTaggerProvider<TTag> _callback;

        protected override ImmutableArray<IOption2> Options => _callback.Options;

        public SingleDiagnosticKindPullTaggerProvider(
            AbstractPushOrPullDiagnosticsTaggerProvider<TTag> callback,
            DiagnosticKind diagnosticKind,
            IThreadingContext threadingContext,
            IDiagnosticService diagnosticService,
            IDiagnosticAnalyzerService analyzerService,
            IGlobalOptionService globalOptions,
            ITextBufferVisibilityTracker? visibilityTracker,
            IAsynchronousOperationListener listener)
            : base(threadingContext, globalOptions, visibilityTracker, listener)
        {
            _callback = callback;
            _diagnosticKind = diagnosticKind;
            _diagnosticService = diagnosticService;
            _analyzerService = analyzerService;
        }

        protected sealed override TaggerDelay EventChangeDelay => TaggerDelay.Short;
        protected sealed override TaggerDelay AddedTagNotificationDelay => TaggerDelay.OnIdle;

        /// <summary>
        /// When we hear about a new event cancel the costly work we're doing and compute against the latest snapshot.
        /// </summary>
        protected sealed override bool CancelOnNewWork => true;

        protected sealed override bool TagEquals(TTag tag1, TTag tag2)
            => _callback.TagEquals(tag1, tag2);

        protected sealed override ITaggerEventSource CreateEventSource(ITextView? textView, ITextBuffer subjectBuffer)
            => CreateEventSourceWorker(subjectBuffer, _diagnosticService);

        protected sealed override Task ProduceTagsAsync(
            TaggerContext<TTag> context, DocumentSnapshotSpan spanToTag, int? caretPosition, CancellationToken cancellationToken)
        {
            return ProduceTagsAsync(context, spanToTag, cancellationToken);
        }

        private async Task ProduceTagsAsync(
            TaggerContext<TTag> context, DocumentSnapshotSpan documentSpanToTag, CancellationToken cancellationToken)
        {
            if (!_callback.IsEnabled)
                return;

            var diagnosticMode = GlobalOptions.GetDiagnosticMode();
            if (!_callback.SupportsDiagnosticMode(diagnosticMode))
                return;

            var document = documentSpanToTag.Document;
            if (document == null)
                return;

            var snapshot = documentSpanToTag.SnapshotSpan.Snapshot;

            var project = document.Project;
            var workspace = project.Solution.Workspace;

            // See if we've marked any spans as those we want to suppress diagnostics for.
            // This can happen for buffers used in the preview workspace where some feature
            // is generating code that it doesn't want errors shown for.
            var buffer = snapshot.TextBuffer;
            var suppressedDiagnosticsSpans = (NormalizedSnapshotSpanCollection?)null;
            buffer?.Properties.TryGetProperty(PredefinedPreviewTaggerKeys.SuppressDiagnosticsSpansKey, out suppressedDiagnosticsSpans);

            var sourceText = snapshot.AsText();

            try
            {
                // If this is not the tagger for compiler-syntax, then suppress diagnostics until the project has fully
                // loaded.  This prevents the user from seeing spurious diagnostics while the project is in the process
                // of loading.  We do keep compiler-syntax as that's based purely on the parse tree, and doesn't need
                // correct project info to get reasonable results.
                if (_diagnosticKind != DiagnosticKind.CompilerSyntax)
                {
                    var service = project.Solution.Services.GetRequiredService<IWorkspaceStatusService>();
                    if (!await service.IsFullyLoadedAsync(cancellationToken).ConfigureAwait(false))
                        return;

                    using var _ = PooledHashSet<Project>.GetInstance(out var seenProjects);
                    if (!HasSuccessfullyLoaded(document.Project, seenProjects))
                        return;
                }

                var requestedSpan = documentSpanToTag.SnapshotSpan;

                var diagnostics = await _analyzerService.GetDiagnosticsForSpanAsync(
                    document,
                    requestedSpan.Span.ToTextSpan(),
                    diagnosticKind: _diagnosticKind,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                foreach (var diagnosticData in diagnostics)
                {
                    if (_callback.IncludeDiagnostic(diagnosticData))
                    {
                        var diagnosticSpans = _callback.GetLocationsToTag(diagnosticData)
                            .Select(loc => loc.UnmappedFileSpan.GetClampedTextSpan(sourceText).ToSnapshotSpan(snapshot));
                        foreach (var diagnosticSpan in diagnosticSpans)
                        {
                            if (diagnosticSpan.IntersectsWith(requestedSpan) && !IsSuppressed(suppressedDiagnosticsSpans, diagnosticSpan))
                            {
                                var tagSpan = _callback.CreateTagSpan(workspace, isLiveUpdate: true, diagnosticSpan, diagnosticData);
                                if (tagSpan != null)
                                    context.AddTag(tagSpan);
                            }
                        }
                    }
                }
            }
            catch (ArgumentOutOfRangeException ex) when (FatalError.ReportAndCatch(ex))
            {
                // https://devdiv.visualstudio.com/DefaultCollection/DevDiv/_workitems?id=428328&_a=edit&triage=false
                // explicitly report NFW to find out what is causing us for out of range. stop crashing on such
                // occasions
                return;
            }
        }

        private bool HasSuccessfullyLoaded(Project? project, HashSet<Project> seenProjects)
        {
            if (project != null && seenProjects.Add(project))
            {
                if (!project.State.HasAllInformation)
                    return false;

                // Ensure our dependencies have all information as well.  That's necessary so we can properly get
                // compilations for them.
                foreach (var reference in project.ProjectReferences)
                {
                    if (!HasSuccessfullyLoaded(project.Solution.GetProject(reference.ProjectId), seenProjects))
                        return false;
                }
            }

            return true;
        }

        private static bool IsSuppressed(NormalizedSnapshotSpanCollection? suppressedSpans, SnapshotSpan span)
            => suppressedSpans != null && suppressedSpans.IntersectsWith(span);
    }
}
