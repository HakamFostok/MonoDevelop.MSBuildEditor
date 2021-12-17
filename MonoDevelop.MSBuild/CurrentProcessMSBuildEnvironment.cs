// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;

using MonoDevelop.MSBuild.SdkResolution;

namespace MonoDevelop.MSBuild
{
	/// <summary>
	/// Describes the MSBuild environment of the current process
	/// </summary>
	class CurrentProcessMSBuildEnvironment : IMSBuildEnvironment
	{
		readonly Toolset toolset;
		readonly Dictionary<SdkReference, SdkInfo> resolvedSdks = new();
		readonly MSBuildSdkResolver sdkResolver;

		public CurrentProcessMSBuildEnvironment ()
		{
			var projectCollection = ProjectCollection.GlobalProjectCollection;
			toolset = projectCollection.GetToolset (projectCollection.DefaultToolsVersion);

			var msbuildExtensionsPath = toolset.Properties[ReservedProperties.ExtensionsPath].EvaluatedValue;
			SearchPaths = GetImportSearchPathsTable (toolset, msbuildExtensionsPath);

			sdkResolver = new MSBuildSdkResolver (this);
		}

		public string ToolsVersion => toolset.ToolsVersion;
        public string ToolsPath => toolset.ToolsPath;

		public bool TryGetToolsetProperty (string propertyName, out string value)
		{
			if (toolset.Properties.TryGetValue(propertyName, out var propVal)) {
				value = propVal?.EvaluatedValue;
				return true;
			}

			value = null;
			return false;
		}

		public IList<SdkInfo> GetRegisteredSdks () => Array.Empty<SdkInfo> ();

		public IReadOnlyDictionary<string, IReadOnlyList<string>> SearchPaths { get; }

		public Version EngineVersion => ProjectCollection.Version;

		public SdkInfo ResolveSdk (
			(string name, string version, string minimumVersion) sdk, string projectFile, string solutionPath)
		{
			var sdkRef = new SdkReference (sdk.name, sdk.version, sdk.minimumVersion);
			if (!resolvedSdks.TryGetValue (sdkRef, out SdkInfo sdkInfo)) {
				try {
					//FIXME: capture errors & warnings from logger and return those too?
					// FIX THIS, at least log to the static logger
					sdkInfo = sdkResolver.ResolveSdk (sdkRef, new NoopLoggingService (), null, projectFile, solutionPath);
				} catch (Exception ex) {
					LoggingService.LogError ("Error in SDK resolver", ex);
				}
				resolvedSdks[sdkRef] = sdkInfo;
			}
			return sdkInfo;
		}

		static IReadOnlyDictionary<string, IReadOnlyList<string>> GetImportSearchPathsTable (Toolset toolset, string msbuildExtensionsPath)
		{
			var dictProp = toolset.GetType ().GetProperty ("ImportPropertySearchPathsTable", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
			var dict = (IDictionary)dictProp.GetValue (toolset);
			var importPathsType = typeof (ProjectCollection).Assembly.GetType ("Microsoft.Build.Evaluation.ProjectImportPathMatch");
			var pathsField = importPathsType.GetField ("SearchPaths", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

			var converted = new Dictionary<string, IReadOnlyList<string>> ();
			var enumerator = dict.GetEnumerator ();
			while (enumerator.MoveNext ()) {
				if (enumerator.Value == null) {
					continue;
				}
				var key = (string)enumerator.Key;
				var val = (List<string>)pathsField.GetValue (enumerator.Value);

				if (key == ReservedProperties.ExtensionsPath || key == ReservedProperties.ExtensionsPath32 || key == ReservedProperties.ExtensionsPath64) {
					var oldVal = val;
					val = new List<string> (oldVal.Count + 1) { msbuildExtensionsPath };
					val.AddRange (oldVal);
				}

				converted.Add (key, val.AsReadOnly ());
			}

			return converted;
		}

		class NoopLoggingService : ILoggingService
		{
			public void LogCommentFromText (MSBuildContext buildEventContext, MessageImportance messageImportance, string message)
			{
			}

			public void LogErrorFromText (MSBuildContext buildEventContext, object subcategoryResourceName, object errorCode, object helpKeyword, string file, string message)
			{
			}

			public void LogFatalBuildError (MSBuildContext buildEventContext, Exception e, string projectFile)
			{
			}

			public void LogWarning (string message)
			{
			}

			public void LogWarningFromText (MSBuildContext bec, object p1, object p2, object p3, string projectFile, string warning)
			{
			}
		}
	}
}