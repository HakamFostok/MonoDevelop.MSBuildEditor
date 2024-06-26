// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;

using MonoDevelop.Xml.Logging;

using ProjectFileTools.NuGetSearch.Contracts;
using ProjectFileTools.NuGetSearch.Feeds;

namespace MonoDevelop.MSBuild.Editor.Completion
{
	partial class MSBuildCompletionSource
	{
		public partial class NuGetSearchUpdater
		{
			public NuGetSearchUpdater (MSBuildCompletionSource parent, MSBuildCompletionContext context, string tfm, string packageType, ILogger logger)
			{
				this.tfm = tfm;
				this.packageType = packageType;
				this.logger = logger;
				this.parent = parent;
				this.context = context;
			}

			readonly string tfm;
			readonly string packageType;
			readonly ILogger logger;
			readonly MSBuildCompletionSource parent;
			private readonly MSBuildCompletionContext context;
			IAsyncCompletionItemManager completionItemManager;

			// these fields are protected by the locker
			object locker = new object ();
			NuGetSearchJob searchJob;
			ImmutableArray<CompletionItem>? updatedList;
			ImmutableArray<CompletionItem> nonNuGetItems;
			bool isUpdatedListEnqueued;

			public ImmutableArray<CompletionItem> Update (
				IAsyncCompletionItemManager completionItemManager,
				AsyncCompletionSessionDataSnapshot data)
			{
				lock (locker) {
					// kick off an updated search if we need one
					if (searchJob == null || searchJob.IsOutdated (data)) {
						if (this.completionItemManager == null) {
							this.completionItemManager = completionItemManager;
							nonNuGetItems = ImmutableArray.CreateRange (
								data.InitialSortedItemList.Where (i => !i.Properties.TryGetProperty (typeof (Tuple<string, FeedKind>), out Tuple<string, FeedKind> info))
							);
						}
						searchJob?.Cancel ();
						searchJob = new NuGetSearchJob (this, data);
					}
					// apply the updated list if there is one
					isUpdatedListEnqueued = false;
					// FIXME: can we avoid the AsImmutableArray() here?
					return updatedList ?? data.InitialSortedItemList.ToImmutableArray ();
				}
			}

			void EnqueueUpdate (ImmutableArray<CompletionItem> newList, CancellationToken token)
			{
				lock (locker) {
					if (token.IsCancellationRequested) {
						return;
					}
					updatedList = newList;
					if (isUpdatedListEnqueued) {
						return;
					}
					isUpdatedListEnqueued = true;
				}

				var jtf = parent.provider.JoinableTaskContext.Factory;
				jtf.Run (async delegate {
					await jtf.SwitchToMainThreadAsync ();
					var session = context.Session;
					if (!session.IsDismissed) {
						var snapshot = session.TextView.TextSnapshot;
						session.OpenOrUpdate (
							new CompletionTrigger (CompletionTriggerReason.Invoke, snapshot),
							session.ApplicableToSpan.GetStartPoint (snapshot),
							CancellationToken.None);
					}
				});
			}

			class NuGetSearchJob
			{
				readonly IPackageFeedSearchJob<Tuple<string, FeedKind>> search;
				readonly NuGetSearchUpdater parent;
				readonly CancellationTokenSource cts = new CancellationTokenSource ();
				readonly AsyncCompletionSessionDataSnapshot data;

				public NuGetSearchJob (NuGetSearchUpdater parent, AsyncCompletionSessionDataSnapshot data)
				{
					this.parent = parent;
					this.data = data;

					var filterText = parent.context.Session.ApplicableToSpan.GetText (data.Snapshot);
					search = parent.parent.provider.PackageSearchManager.SearchPackageNames (filterText, parent.tfm);
					cts.Token.Register (search.Cancel);

					search.Updated += SearchUpdated;

					// it may have cached results, and SearchUpdated may never fire
					if (search.Results.Count > 0) {
						SearchUpdated (search, EventArgs.Empty);
					}
				}

				public bool IsOutdated (AsyncCompletionSessionDataSnapshot data)
					=> data.Snapshot.Version.VersionNumber > this.data.Snapshot.Version.VersionNumber;

				public void Cancel () => cts.Cancel ();

				void SearchUpdated (object sender, EventArgs e)
				{
					var token = cts.Token;

					int remainingFeeds = search.RemainingFeeds.Count;
					if (remainingFeeds == 0 || token.IsCancellationRequested) {
						search.Updated -= SearchUpdated;
					}

					if (token.IsCancellationRequested) {
						return;
					}

					var items = parent.parent.CreateNuGetItemsFromSearchResults (parent.context.DocumentationProvider, search.Results);

					// if remainingFeeds has changed, a new event has fired, bail out and let it do the work
					if (token.IsCancellationRequested || remainingFeeds > search.RemainingFeeds.Count) {
						return;
					}

					var newList = parent.nonNuGetItems.AddRange (items);
					parent.completionItemManager.SortCompletionListAsync (
						parent.context.Session,
						new AsyncCompletionSessionInitialDataSnapshot (newList, data.Snapshot, new CompletionTrigger (CompletionTriggerReason.Invoke, data.Snapshot)),
						token
					).LogTaskExceptionsAndForget (parent.logger);

					if (token.IsCancellationRequested || remainingFeeds > search.RemainingFeeds.Count) {
						return;
					}

					parent.EnqueueUpdate (newList, token);
				}
			}
		}
	}
}
