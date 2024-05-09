// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;

using MonoDevelop.MSBuild.SdkResolution;

namespace MonoDevelop.MSBuild
{
	/// <summary>Describes an MSBuild installation</summary>
	public interface IMSBuildEnvironment
	{
		Version EngineVersion { get; }
		string ToolsVersion { get; }
		string ToolsPath { get; }

		/// <summary>
		/// Properties defined by the MSBuild toolset, such as MSBuildExtensionsPaths
		/// </summary>
		IReadOnlyDictionary<string, string> ToolsetProperties { get; }

		IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

		/// <summary>
		/// Multivalued fallback properties for use only when evaluating the first property in Import elements. Defined in projectImportSearchPaths in app.config.
		/// </summary>
		IReadOnlyDictionary<string, string[]> ProjectImportSearchPaths { get; }

		IList<SdkInfo> GetRegisteredSdks ();

		// TODO: should this support reporting warnings up to the caller as user-visible warnings rather than log messages?
		SdkInfo ResolveSdk (MSBuildSdkReference sdkReference, string projectFile, string solutionPath, ILogger? logger);
	}

	public static class MSBuildEnvironmentExtensions
	{
		public static IEnumerable<string> EnumerateFilesInToolsPath (this IMSBuildEnvironment env, string searchPattern)
		{
			if (env.ToolsPath == null) {
				return Enumerable.Empty<string> ();
			}
			return Directory.EnumerateFiles (env.ToolsPath, searchPattern);
		}
	}
}
